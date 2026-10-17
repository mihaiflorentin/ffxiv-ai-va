namespace AIVoiceActing.Tests;

using AIVoiceActing.Domain;
using AIVoiceActing.Infrastructure.Onnx;
using AIVoiceActing.Ports;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Xunit;

/// <summary>Runs only with AIVA_GPU_TESTS=1 on a machine with a Vulkan (or D3D12) GPU; CI skips it.</summary>
public sealed class GpuFactAttribute : FactAttribute
{
    public GpuFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("AIVA_GPU_TESTS") != "1")
        {
            this.Skip = "Set AIVA_GPU_TESTS=1 to run the GPU integration tests.";
        }
    }
}

public sealed class GpuIntegrationTests : IDisposable
{
    // add.onnx: c = a + b over float[1,4] (opset 17), generated with the onnx python package.
    private static readonly byte[] AddModel =
    [
        0x08, 0x08, 0x3a, 0x54, 0x0a, 0x0e, 0x0a, 0x01, 0x61, 0x0a, 0x01, 0x62, 0x12, 0x01, 0x63, 0x22, 0x03, 0x41,
        0x64, 0x64, 0x12, 0x03, 0x61, 0x64, 0x64, 0x5a, 0x13, 0x0a, 0x01, 0x61, 0x12, 0x0e, 0x0a, 0x0c, 0x08, 0x01,
        0x12, 0x08, 0x0a, 0x02, 0x08, 0x01, 0x0a, 0x02, 0x08, 0x04, 0x5a, 0x13, 0x0a, 0x01, 0x62, 0x12, 0x0e, 0x0a,
        0x0c, 0x08, 0x01, 0x12, 0x08, 0x0a, 0x02, 0x08, 0x01, 0x0a, 0x02, 0x08, 0x04, 0x62, 0x13, 0x0a, 0x01, 0x63,
        0x12, 0x0e, 0x0a, 0x0c, 0x08, 0x01, 0x12, 0x08, 0x0a, 0x02, 0x08, 0x01, 0x0a, 0x02, 0x08, 0x04, 0x42, 0x04,
        0x0a, 0x00, 0x10, 0x11,
    ];

    private readonly string dir = Path.Combine(Path.GetTempPath(), "aiva-gpu", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(this.dir))
        {
            Directory.Delete(this.dir, recursive: true);
        }
    }

    private static string LibraryPath()
    {
        var rid = OperatingSystem.IsWindows() ? "win-x64" : OperatingSystem.IsMacOS() ? "osx-arm64" : "linux-x64";
        return Path.Combine(AppContext.BaseDirectory, "runtimes", rid, "native", WebGpuRuntime.LibraryFileName);
    }

    [GpuFact]
    public void TheRuntimeRegisters_AndListsAtLeastOneGpu()
    {
        var runtime = new WebGpuRuntime(null, isWine: false, libraryPathOverride: LibraryPath());

        var listing = runtime.Enumerate();

        Assert.True(listing.Available, listing.Failure);
        Assert.NotEmpty(listing.Adapters);
    }

    [GpuFact]
    public void AToyGraph_RunsOnTheGpu_ThroughTheSessionFactory_AndTheMarkerClearsAfterTheFirstRun()
    {
        var runtime = new WebGpuRuntime(null, isWine: false, libraryPathOverride: LibraryPath());
        var marker = Path.Combine(this.dir, "gpu-session.pending");
        var factory = new OnnxSessionFactory(runtime, new GpuCrashGuard(marker));
        var api = OperatingSystem.IsWindows() ? GpuApi.D3D12 : GpuApi.Vulkan;
        var plan = new DevicePlan(DeviceEp.WebGpu, api, GpuPreference.Performance, "test", true);

        var build = factory.Create(SessionRole.Lm, plan);
        using var options = build.Options;
        Assert.Equal(DeviceEp.WebGpu, build.Ep);
        Assert.True(File.Exists(marker));

        using var session = new InferenceSession(AddModel, options);
        var a = new DenseTensor<float>(new float[] { 1, 2, 3, 4 }, [1, 4]);
        var b = new DenseTensor<float>(new float[] { 10, 20, 30, 40 }, [1, 4]);
        using var result = session.Run(
        [
            NamedOnnxValue.CreateFromTensor("a", a),
            NamedOnnxValue.CreateFromTensor("b", b),
        ]);
        build.Guard!.Succeeded();

        Assert.Equal(new float[] { 11, 22, 33, 44 }, result[0].AsTensor<float>().ToArray());
        Assert.False(File.Exists(marker));
    }
}
