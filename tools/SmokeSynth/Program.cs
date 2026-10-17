namespace SmokeSynth;

using System.Diagnostics;

using AIVoiceActing.Domain;
using AIVoiceActing.Infrastructure.Kokoro;
using AIVoiceActing.Infrastructure.Onnx;
using AIVoiceActing.Ports;

/// <summary>
/// Offline smoke harness for the AIVoiceActing synthesis engine: loads the Kokoro or Chatterbox Turbo
/// sessions, performs one synthesis, writes a canonical 24 kHz WAV, and
/// reports the real-time factor. Exit 0 on success; non-zero with a clear message otherwise.
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        string? text = null;
        string? reference = null;
        string? engine = null;
        string? executionProvider = null;
        string? output = null;
        string? modelsDir = null;
        string precision = "auto";
        int? threads = null;
        string? tokensOut = null;
        var tags = new List<string>();
        double exaggeration = 0.5;
        double pitch = 1.0;
        double speed = 1.0;
        bool printRealTimeFactor = false;
        string? device = null;
        string api = "auto";
        string power = "high";
        bool bench = false;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--help":
                case "-h":
                    PrintUsage();
                    return 1;
                case "--text" when i + 1 < args.Length:
                    text = args[++i];
                    break;
                case "--ref" when i + 1 < args.Length:
                    reference = args[++i];
                    break;
                case "--exaggeration" when i + 1 < args.Length:
                    if (!double.TryParse(args[++i], out exaggeration))
                    {
                        Console.Error.WriteLine($"Invalid value for --exaggeration: '{args[i]}'.");
                        return 1;
                    }

                    break;
                case "--tags" when i + 1 < args.Length:
                    tags.AddRange(args[++i].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
                    break;
                case "--pitch" when i + 1 < args.Length:
                    if (!double.TryParse(args[++i], out pitch))
                    {
                        Console.Error.WriteLine($"Invalid value for --pitch: '{args[i]}'.");
                        return 1;
                    }

                    break;
                case "--speed" when i + 1 < args.Length:
                    if (!double.TryParse(args[++i], out speed))
                    {
                        Console.Error.WriteLine($"Invalid value for --speed: '{args[i]}'.");
                        return 1;
                    }

                    break;
                case "--ep" when i + 1 < args.Length:
                    executionProvider = args[++i];
                    break;
                case "--engine" when i + 1 < args.Length:
                    engine = args[++i];
                    break;
                case "--models" when i + 1 < args.Length:
                    modelsDir = args[++i];
                    break;
                case "--precision" when i + 1 < args.Length:
                    precision = args[++i].ToLowerInvariant();
                    if (precision is not ("auto" or "fp32" or "fp16" or "q4f16"))
                    {
                        Console.Error.WriteLine("--precision must be auto, fp32, fp16 or q4f16.");
                        return 1;
                    }

                    break;
                case "--threads" when i + 1 < args.Length:
                    if (!int.TryParse(args[++i], out var parsedThreads) || parsedThreads < 1)
                    {
                        Console.Error.WriteLine("--threads must be a positive number.");
                        return 1;
                    }

                    threads = parsedThreads;
                    break;
                case "--tokens-out" when i + 1 < args.Length:
                    tokensOut = args[++i];
                    break;
                case "--out" when i + 1 < args.Length:
                    output = args[++i];
                    break;
                case "--rtf":
                    printRealTimeFactor = true;
                    break;
                case "--device" when i + 1 < args.Length:
                    device = args[++i].ToLowerInvariant();
                    if (device is not ("cpu" or "gpu"))
                    {
                        Console.Error.WriteLine("--device must be cpu or gpu.");
                        return 1;
                    }

                    break;
                case "--api" when i + 1 < args.Length:
                    api = args[++i].ToLowerInvariant();
                    if (api is not ("vulkan" or "d3d12"))
                    {
                        Console.Error.WriteLine("--api must be vulkan or d3d12.");
                        return 1;
                    }

                    break;
                case "--power" when i + 1 < args.Length:
                    power = args[++i].ToLowerInvariant();
                    if (power is not ("high" or "low"))
                    {
                        Console.Error.WriteLine("--power must be high or low.");
                        return 1;
                    }

                    break;
                case "--bench":
                    bench = true;
                    break;
                default:
                    Console.Error.WriteLine($"Unknown or malformed argument: {args[i]}");
                    PrintUsage();
                    return 1;
            }
        }

        if (string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(output))
        {
            Console.Error.WriteLine("Missing required arguments: --text and --out are required.");
            PrintUsage();
            return 1;
        }

        if (exaggeration is < 0.0 or > 1.0)
        {
            Console.Error.WriteLine("--exaggeration must be within 0.0..1.0.");
            return 1;
        }
        // Turbo clones a reference clip (default: the bundled MIT fallback clip); Kokoro takes a voice NAME (--ref af_heart).
        engine ??= "kokoro";
        if (engine is not ("kokoro" or "turbo" or "chatterbox-turbo"))
        {
            Console.Error.WriteLine($"Unknown --engine \"{engine}\". Use kokoro or turbo.");
            return 1;
        }

        if (engine != "kokoro")
        {
            reference ??= Path.Combine(AppContext.BaseDirectory, "voices", "default_voice.wav");
            if (!File.Exists(reference))
            {
                Console.Error.WriteLine($"Reference voice not found: {reference}");
                return 1;
            }
        }

        modelsDir ??= "models";
        try
        {
            var log = new ConsoleLogSink();
            ISpeechSynthesizer synthesizer;
            if (engine != "kokoro")
            {
                synthesizer = ChatterboxSynthesizer.CreateTurbo(
                    modelsDir: modelsDir,
                    voicePathResolver: _ => reference,
                    executionProvider: executionProvider ?? "cpu",
                    log: log,
                    intraOpThreads: threads,
                    devices: device is null ? null : BuildDeviceServices(modelsDir, device, api, power, precision, log));
            }
            else
            {
                if (device == "gpu")
                {
                    Console.WriteLine(DeviceModePolicy.Plan(new DevicePolicyInput(
                        DeviceMode.Gpu, "kokoro", DeviceReport.Unknown, false, false)).Reason);
                }

                synthesizer = new KokoroSynthesizer(
                    () => modelsDir!,
                    () => 4,
                    log);
            }

            var request = new SynthesisRequest(
                ReferenceVoiceId: engine == "kokoro" ? reference ?? "af_heart" : "default",
                Text: text,
                Exaggeration: (float)exaggeration,
                Tags: tags,
                Pitch: (float)pitch,
                Speed: (float)speed);

            var clock = Stopwatch.StartNew();
            var result = synthesizer.SynthesizeAsync(request, CancellationToken.None)
                .GetAwaiter()
                .GetResult();
            clock.Stop();

            WavCodec.WriteMono24k(output, result.Samples);
            var durationSeconds = result.Samples.Length / (double)result.SampleRate;

            Console.WriteLine($"ep:          {(synthesizer is ChatterboxSynthesizer chatterbox ? chatterbox.EffectiveEp : "cpu")}");
            if (synthesizer is ChatterboxSynthesizer turbo)
            {
                Console.WriteLine($"precision:   {turbo.EffectivePrecision} ({turbo.RenderVariant})");
                Console.WriteLine($"tokens:      {turbo.LastSpeechTokens.Count}");
                if (tokensOut is not null)
                {
                    File.WriteAllText(tokensOut, string.Join(',', turbo.LastSpeechTokens));
                }
            }

            Console.WriteLine($"audio:       {durationSeconds:0.00} s ({result.Samples.Length} samples @ {result.SampleRate} Hz)");
            Console.WriteLine($"out:         {output}");
            if (printRealTimeFactor)
            {
                var synthSeconds = clock.Elapsed.TotalSeconds;
                Console.WriteLine($"synth:       {synthSeconds:0.00} s");
                Console.WriteLine($"RTF:         {synthSeconds / durationSeconds:0.000}");
            }

            if (bench)
            {
                // The first line above paid session creation; time the steady state.
                var runs = new List<double>();
                for (var run = 0; run < 5; run++)
                {
                    var timer = Stopwatch.StartNew();
                    var again = synthesizer.SynthesizeAsync(request, CancellationToken.None).GetAwaiter().GetResult();
                    timer.Stop();
                    runs.Add(timer.Elapsed.TotalSeconds / (again.Samples.Length / (double)again.SampleRate));
                    Console.WriteLine($"bench {run + 1}/5:   {timer.Elapsed.TotalSeconds:0.00} s, RTF {runs[^1]:0.000}");
                }

                runs.Sort();
                Console.WriteLine($"bench RTF:   min {runs[0]:0.000}, median {runs[runs.Count / 2]:0.000}, max {runs[^1]:0.000}");
            }

            var status = synthesizer.Status;
            Console.WriteLine($"status:      {status.State}, requested {status.RequestedDevice}, active {status.ActiveDevice.Ep}{(status.ActiveDevice.Adapter.Length > 0 ? $" on {status.ActiveDevice.Adapter}" : string.Empty)}");
            if (status.Reason.Length > 0)
            {
                Console.WriteLine($"reason:      {status.Reason}");
            }

            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Synthesis failed: {ex.Message}");
            return 2;
        }
    }

    /// <summary>
    /// Device plumbing for the Turbo path: probe the GPU, honour a leftover crash marker (this tool
    /// runs outside Dalamud, so it just forces the CPU for this run), and build the planner.
    /// </summary>
    private static TurboDeviceServices BuildDeviceServices(string modelsDir, string device, string api, string power, string precision, ILogSink log)
    {
        var runtime = new WebGpuRuntime(
            pluginDirectory: null,
            RuntimeHost.DetectWine(),
            log,
            libraryPathOverride: FindWebGpuLibrary());
        var probe = new DeviceProbe(runtime, log: log);
        var report = probe.Detect();
        Console.WriteLine($"platform:    {report.Platform}");
        Console.WriteLine($"webgpu:      {(report.WebGpuAvailable ? "available" : $"unavailable ({report.Reason})")}");
        foreach (var adapter in report.Adapters)
        {
            Console.WriteLine($"adapter:     {adapter.Name} ({adapter.Backend})");
        }

        Directory.CreateDirectory(modelsDir);
        var guard = new GpuCrashGuard(Path.Combine(modelsDir, "gpu-session.pending"), log);
        var crashed = guard.TripIfLeftover(detail => Console.WriteLine($"crash guard: a previous run died on the GPU ({detail}); using the CPU this time."));
        var prefs = new DevicePreferences(
            device == "gpu" ? DeviceMode.Gpu : DeviceMode.Cpu,
            power == "low" ? GpuPreference.LowPower : GpuPreference.Performance,
            api switch
            {
                "vulkan" => GpuApi.Vulkan,
                "d3d12" => GpuApi.D3D12,
                _ => GpuApi.Auto,
            },
            DeviceBackend.Auto,
            crashed);
        return new TurboDeviceServices(
            new DevicePlanner(() => prefs, probe),
            new OnnxSessionFactory(runtime, guard, log),
            LmGpuLoopReady: true,
            Precision: () => precision);
    }

    private static string? FindWebGpuLibrary()
    {
        var rid = OperatingSystem.IsWindows() ? "win-x64"
            : OperatingSystem.IsMacOS() ? "osx-arm64"
            : "linux-x64";
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "runtimes", rid, "native", WebGpuRuntime.LibraryFileName),
            Path.Combine(AppContext.BaseDirectory, WebGpuRuntime.LibraryFileName),
        };
        return candidates.FirstOrDefault(File.Exists);
    }

    private static void PrintUsage()
    {
        Console.WriteLine("""
            Usage: SmokeSynth --text <line> --out <file.wav> [options]

            Required:
              --text <line>        Dialogue line to perform.
              --out <file.wav>     Output WAV path.

            Options:
              --engine <kokoro|turbo>  Engine (default kokoro; --ref is then a voice name such as af_heart).
              --ref <voice.wav>      Turbo: reference voice clip (default: bundled voices/default_voice.wav).
              --exaggeration <0..1>  Performance intensity (default 0.5).
              --tags <a,b,c>         Comma-separated paralinguistic tags.
              --ep <name>            Turbo without --device: execution provider (auto|coreml|cpu|directml; default cpu).
              --precision <p>        Turbo with --device: auto|fp32|fp16|q4f16 (default auto: q4f16 on a verified GPU, else fp32).
              --threads <n>          Turbo: CPU threads for the encoder, embedding and decoder (default: half the logical cores; the plugin's CPU impact setting is 2/4/8).
              --tokens-out <file>    Turbo: write the generated speech token ids (comma separated) for A/B comparison.
              --models <dir>         Model directory (default ./models).
              --rtf                  Print the real-time factor after synthesis.
              --device <cpu|gpu>     Turbo only: where the language model may run. Prints the probe, plan and status.
                                     Kokoro never uses the GPU. The GPU also needs a verified model (fp16/q4f16).
              --api <vulkan|d3d12>   WebGPU graphics API (default: vulkan off Windows, d3d12 on Windows).
              --power <high|low>     Adapter class: discrete (high) or integrated (low).
              --bench                Time 5 more runs after the first and print min/median/max RTF.
              --help                 Show this help.
            """);
    }

    /// <summary>Console ILogSink for the harness.</summary>
    private sealed class ConsoleLogSink : ILogSink
    {
        public void Info(string msg) => Console.WriteLine(msg);

        public void Warn(string msg) => Console.WriteLine(msg);

        public void Error(string msg, Exception? ex = null) =>
            Console.Error.WriteLine(ex is null ? msg : $"{msg}: {ex.Message}");
    }
}
