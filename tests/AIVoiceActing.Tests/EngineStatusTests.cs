namespace AIVoiceActing.Tests;

using AIVoiceActing.Domain;
using AIVoiceActing.Infrastructure.Kokoro;
using AIVoiceActing.Infrastructure.Onnx;
using AIVoiceActing.Ports;
using AIVoiceActing.Tests.Mock;
using Xunit;

/// <summary>Engine status snapshots and their transitions (NotInstalled, Loading, Ready, FellBack, Failed).</summary>
public sealed class EngineStatusTests : IDisposable
{
    private readonly List<string> dirs = [];

    public void Dispose()
    {
        foreach (var dir in this.dirs.Where(Directory.Exists))
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    private string NewDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "aiva-status", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        this.dirs.Add(dir);
        return dir;
    }

    private sealed class FakeTokenizer : ITextTokenizer
    {
        public int[] Encode(string text) => [1, 2, 3];
    }

    // ---- Kokoro: never on the GPU -------------------------------------------------------------

    [Fact]
    public void Kokoro_NotInstalled_ThenReady_AlwaysOnTheCpu()
    {
        var modelsDir = this.NewDir();
        var voicesDir = this.NewDir();
        File.WriteAllText(Path.Combine(voicesDir, "af_heart.npy"), "stub");
        using var synth = new KokoroSynthesizer(() => modelsDir, () => 4, new FakeLogSink(), voicesDirFactory: () => voicesDir);

        var missing = synth.Status;
        Assert.Equal(EngineState.NotInstalled, missing.State);
        Assert.Contains("Models tab", missing.Reason);
        Assert.Equal(DeviceEp.Cpu, missing.ActiveDevice.Ep);

        File.WriteAllText(Path.Combine(modelsDir, ModelCatalog.KokoroModelFileName), "stub");
        System.Threading.Thread.Sleep(2100); // the asset probe is cached for 2 s
        var ready = synth.Status;

        Assert.Equal(EngineState.Ready, ready.State);
        Assert.Equal(DeviceMode.Cpu, ready.RequestedDevice);
        Assert.Equal(DeviceEp.Cpu, ready.ActiveDevice.Ep);
        Assert.False(ready.CanRetryGpu);
        Assert.Contains("Kokoro", ready.Reason);
    }

    [Fact]
    public async Task Kokoro_RetryIsANoOp()
    {
        using var synth = new KokoroSynthesizer(() => this.NewDir(), () => 4, new FakeLogSink());

        await ((ISpeechSynthesizer)synth).RetryAsync(DeviceMode.Gpu);

        Assert.Equal(EngineState.NotInstalled, synth.Status.State);
    }

    // ---- default (engines without a device choice) --------------------------------------------

    [Fact]
    public void EnginesWithoutADeviceChoice_UseTheReadinessDefault()
    {
        ISpeechSynthesizer ready = new FakeSpeechSynthesizer { IsReady = true };
        ISpeechSynthesizer missing = new FakeSpeechSynthesizer { IsReady = false, NotReadyReason = "Missing it" };

        Assert.Equal(EngineState.Ready, ready.Status.State);
        Assert.Equal(EngineState.NotInstalled, missing.Status.State);
        Assert.Equal("Missing it", missing.Status.Reason);
        Assert.Equal(DeviceEp.Cpu, ready.Status.ActiveDevice.Ep);
    }

    [Fact]
    public void TheRouter_ReportsThePrimaryEnginesStatus()
    {
        var primary = new FakeSpeechSynthesizer { IsReady = false, NotReadyReason = "Primary missing" };
        var router = new ExpressiveRouter(() => primary, () => null, (_, _) => Task.FromResult<string?>(null));

        Assert.Equal("Primary missing", ((ISpeechSynthesizer)router).Status.Reason);
    }

    // ---- Turbo: language model placement ------------------------------------------------------

    private (ChatterboxSynthesizer Synth, FakeDevicePlanner Planner, int[] ClearCalls, string Dir) Turbo(
        bool installed = true, bool gpuLoopReady = false)
    {
        var dir = this.NewDir();
        if (installed)
        {
            foreach (var asset in ModelCatalog.TurboRequiredAssets)
            {
                using var file = File.Create(Path.Combine(dir, asset.FileName));
                file.SetLength(asset.SizeBytes ?? 1024);
            }
        }

        var voice = Path.Combine(dir, "default.wav");
        WavCodec.WriteMono24k(voice, [0f]);
        var planner = new FakeDevicePlanner();
        var guard = new GpuCrashGuard(Path.Combine(dir, "gpu-session.pending"));
        var clears = new int[1];
        var services = new TurboDeviceServices(
            planner,
            new OnnxSessionFactory(new FakeWebGpuRuntime(), guard),
            ClearCrashFlag: () => clears[0]++,
            LmGpuLoopReady: gpuLoopReady);
        var synth = ChatterboxSynthesizer.CreateTurbo(
            dir, _ => voice, log: new FakeLogSink(), tokenizerFactory: () => new FakeTokenizer(), devices: services);
        return (synth, planner, clears, dir);
    }

    [Fact]
    public void Turbo_MissingModels_IsNotInstalled()
    {
        var (synth, _, _, _) = this.Turbo(installed: false);
        using var _s = synth;

        var status = synth.Status;

        Assert.Equal(EngineState.NotInstalled, status.State);
        Assert.Contains("Models tab", status.Reason);
    }

    [Fact]
    public void Turbo_OnTheCpuByChoice_IsReadyWithoutFallbackText()
    {
        var (synth, planner, _, _) = this.Turbo();
        using var _s = synth;
        planner.Preferences = DevicePreferences.Default with { Mode = DeviceMode.Cpu };
        planner.CheapPlan = DevicePlan.OnCpu("Running on the CPU, as you chose.", wantedGpu: false);

        var status = synth.Status;

        Assert.Equal(EngineState.Ready, status.State);
        Assert.Equal(DeviceMode.Cpu, status.RequestedDevice);
        Assert.Equal(DeviceEp.Cpu, status.ActiveDevice.Ep);
        Assert.False(status.CanRetryGpu);
    }

    [Fact]
    public void Turbo_WantedGpuButUnverifiedModel_FellBack_NoRetryOffered()
    {
        var (synth, planner, _, _) = this.Turbo();
        using var _s = synth;
        planner.CheapPlan = DevicePlan.OnCpu("The GPU is only used with voice models that have been tested with it; the installed one runs on the CPU.", wantedGpu: true);

        var status = synth.Status;

        Assert.Equal(EngineState.FellBack, status.State);
        Assert.Equal(DeviceEp.Cpu, status.ActiveDevice.Ep);
        Assert.Contains("tested", status.Reason);
        Assert.False(status.CanRetryGpu);
        Assert.True(planner.Calls.All(c => c.Cheap), "status polling must never run the expensive plan");
    }

    [Fact]
    public void Turbo_CrashGuardTripped_FellBack_WithRetry()
    {
        var (synth, planner, _, _) = this.Turbo();
        using var _s = synth;
        planner.Preferences = DevicePreferences.Default with { GpuDisabledByCrashGuard = true };
        planner.CheapPlan = DevicePlan.OnCpu("The game stopped while using the GPU last time, so the GPU is turned off. Use Retry on GPU to try again.", wantedGpu: true);

        var status = synth.Status;

        Assert.Equal(EngineState.FellBack, status.State);
        Assert.True(status.CanRetryGpu);
        Assert.Contains("Retry on GPU", status.Reason);
    }

    [Fact]
    public void Turbo_AVerifiedGpuPlan_IsReportedAsCpuUntilTheGpuLoopExists()
    {
        var (synth, planner, _, _) = this.Turbo(gpuLoopReady: false);
        using var _s = synth;
        planner.CheapPlan = new DevicePlan(DeviceEp.WebGpu, GpuApi.Vulkan, GpuPreference.Performance, "gpu", true);

        var status = synth.Status;

        Assert.Equal(EngineState.FellBack, status.State);
        Assert.Equal(DeviceEp.Cpu, status.ActiveDevice.Ep);
        Assert.Contains("CPU", status.Reason);
        Assert.False(status.CanRetryGpu);
    }

    [Fact]
    public void Turbo_AVerifiedGpuPlan_IsReady_OnTheGpu_OnceTheLoopExists()
    {
        var (synth, planner, _, _) = this.Turbo(gpuLoopReady: true);
        using var _s = synth;
        planner.LastReport = new DeviceReport(DevicePlatform.Wine, [new DeviceAdapter("AMD", "Radeon RX 9070 XT", "Vulkan")], true, string.Empty);
        planner.CheapPlan = new DevicePlan(DeviceEp.WebGpu, GpuApi.Vulkan, GpuPreference.Performance, "Using the high-performance GPU through Vulkan.", true);

        var status = synth.Status;

        Assert.Equal(EngineState.Ready, status.State);
        Assert.Equal(DeviceEp.WebGpu, status.ActiveDevice.Ep);
        Assert.Equal("Radeon RX 9070 XT", status.ActiveDevice.Adapter);
        Assert.Equal(GpuApi.Vulkan, status.ActiveDevice.Api);
    }

    [Fact]
    public async Task Turbo_Retry_ClearsTheCrashFlag_PassesTheModeOnce_AndShowsLoadingThenFailed()
    {
        var (synth, planner, clears, _) = this.Turbo();
        using var _s = synth;
        planner.Plan = DevicePlan.OnCpu("cpu", wantedGpu: false);
        planner.CheapPlan = planner.Plan;
        planner.Gate = new ManualResetEventSlim(false);

        var retry = synth.RetryAsync(DeviceMode.Gpu);
        Assert.True(planner.Entered.Wait(TimeSpan.FromSeconds(10)), "the retry never reached the planner");

        var loading = synth.Status;
        Assert.Equal(EngineState.Loading, loading.State);
        Assert.Equal(DeviceMode.Gpu, loading.RequestedDevice);

        planner.Gate.Set();
        await retry;

        // The stub model files are not real ONNX graphs, so the load fails and the status says so.
        var failed = synth.Status;
        Assert.Equal(EngineState.Failed, failed.State);
        Assert.Contains("could not load", failed.Reason);
        Assert.Equal(1, clears[0]);
        Assert.Contains(planner.Calls, c => !c.Cheap && c.Override == DeviceMode.Gpu);
    }

    [Fact]
    public async Task Turbo_RetryOnCpu_DoesNotTouchTheCrashFlag()
    {
        var (synth, planner, clears, _) = this.Turbo();
        using var _s = synth;
        planner.Plan = DevicePlan.OnCpu("cpu");

        await synth.RetryAsync(DeviceMode.Cpu);

        Assert.Equal(0, clears[0]);
        Assert.Contains(planner.Calls, c => !c.Cheap && c.Override == DeviceMode.Cpu);
    }

    // ---- planner --------------------------------------------------------------------------------

    [Fact]
    public void Planner_SkipsTheExpensiveInputs_ForKokoroAndCpuMode()
    {
        var probe = new FakeDeviceProbe();
        var planner = new DevicePlanner(() => DevicePreferences.Default, probe, new VerifiedGpuCombos(_ => throw new InvalidOperationException("must not hash")));

        var kokoro = planner.PlanFor("kokoro", "/nope", "kokoro.onnx", cheap: false);
        var cpu = new DevicePlanner(() => DevicePreferences.Default with { Mode = DeviceMode.Cpu }, probe)
            .PlanFor(DeviceModePolicy.TurboEngineId, "/nope", "turbo-language-model-q4f16.onnx", cheap: false);

        Assert.Equal(DeviceEp.Cpu, kokoro.Ep);
        Assert.Equal(DeviceEp.Cpu, cpu.Ep);
        Assert.Equal(0, probe.DetectCalls);
    }

    [Fact]
    public void Planner_PicksTheGpu_ForAVerifiedModel_AndOnlyDetectsOnTheFullPath()
    {
        var dir = this.NewDir();
        var combo = VerifiedGpuCombos.Combos[1];
        foreach (var file in combo.Files)
        {
            using var stream = File.Create(Path.Combine(dir, file.FileName));
            stream.SetLength(file.SizeBytes);
        }

        var probe = new FakeDeviceProbe
        {
            Next = new DeviceReport(DevicePlatform.Wine, [new DeviceAdapter("AMD", "GPU", "Vulkan")], true, string.Empty),
        };
        var combos = new VerifiedGpuCombos(path => VerifiedGpuCombos.Combos.SelectMany(c => c.Files).Single(f => f.FileName == Path.GetFileName(path)).Sha256);
        var planner = new DevicePlanner(() => DevicePreferences.Default, probe, combos);

        var cheap = planner.PlanFor(DeviceModePolicy.TurboEngineId, dir, combo.GraphFile, cheap: true);
        Assert.Equal(DeviceEp.Cpu, cheap.Ep); // no report yet: never loads the runtime from a status poll
        Assert.Equal(0, probe.DetectCalls);

        var full = planner.PlanFor(DeviceModePolicy.TurboEngineId, dir, combo.GraphFile, cheap: false);
        Assert.Equal(DeviceEp.WebGpu, full.Ep);
        Assert.Equal(GpuApi.Vulkan, full.Api);
        Assert.Equal(1, probe.DetectCalls);
    }

    [Fact]
    public void Planner_ModeOverride_BeatsTheSettings()
    {
        var planner = new DevicePlanner(() => DevicePreferences.Default, new FakeDeviceProbe());

        var plan = planner.PlanFor(DeviceModePolicy.TurboEngineId, "/nope", "x.onnx", cheap: true, modeOverride: DeviceMode.Cpu);

        Assert.False(plan.WantedGpu);
    }
}
