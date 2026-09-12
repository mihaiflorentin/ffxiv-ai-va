namespace SmokeSynth;

using System.Diagnostics;
using AIVoiceActing.Infrastructure.Onnx;
using AIVoiceActing.Ports;

/// <summary>
/// Offline smoke harness for the AIVoiceActing synthesis engine: loads the Chatterbox ONNX
/// sessions, performs one tagged/exaggerated synthesis, writes a canonical 24 kHz WAV, and
/// reports the real-time factor. Exit 0 on success; non-zero with a clear message otherwise.
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        string? text = null;
        string? reference = null;
        string? executionProvider = null;
        string? output = null;
        string? modelsDir = null;
        string? lmOverride = null;
        var tags = new List<string>();
        double exaggeration = 0.5;
        bool printRealTimeFactor = false;

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
                case "--ep" when i + 1 < args.Length:
                    executionProvider = args[++i];
                    break;
                case "--models" when i + 1 < args.Length:
                    modelsDir = args[++i];
                    break;
                case "--lm" when i + 1 < args.Length:
                    lmOverride = args[++i];
                    break;
                case "--out" when i + 1 < args.Length:
                    output = args[++i];
                    break;
                case "--rtf":
                    printRealTimeFactor = true;
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

        // Default reference: the bundled MIT fallback clip copied next to the binary.
        reference ??= Path.Combine(AppContext.BaseDirectory, "voices", "default_voice.wav");
        if (!File.Exists(reference))
        {
            Console.Error.WriteLine($"Reference voice not found: {reference}");
            return 1;
        }

        modelsDir ??= "models";
        try
        {
            var log = new ConsoleLogSink();
            var tokenizer = new TokenizersDotNetTokenizer(
                Path.Combine(modelsDir, ModelCatalog.TokenizerJsonFileName));
            var synthesizer = new ChatterboxSynthesizer(
                modelsDir: modelsDir,
                voicePathResolver: _ => reference,
                tokenizerFactory: () => tokenizer,
                executionProvider: executionProvider ?? "auto",
                log: log,
                languageModelOverride: lmOverride);

            var request = new SynthesisRequest(
                ReferenceVoiceId: "default",
                Text: text,
                Exaggeration: (float)exaggeration,
                Tags: tags);

            var clock = Stopwatch.StartNew();
            var result = synthesizer.SynthesizeAsync(request, CancellationToken.None)
                .GetAwaiter()
                .GetResult();
            clock.Stop();

            WavCodec.WriteMono24k(output, result.Samples);
            var durationSeconds = result.Samples.Length / (double)result.SampleRate;

            Console.WriteLine($"ep:          {synthesizer.EffectiveEp}");
            Console.WriteLine($"audio:       {durationSeconds:0.00} s ({result.Samples.Length} samples @ {result.SampleRate} Hz)");
            Console.WriteLine($"out:         {output}");
            if (printRealTimeFactor)
            {
                var synthSeconds = clock.Elapsed.TotalSeconds;
                Console.WriteLine($"synth:       {synthSeconds:0.00} s");
                Console.WriteLine($"RTF:         {synthSeconds / durationSeconds:0.000}");
            }

            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Synthesis failed: {ex.Message}");
            return 2;
        }
    }

    private static void PrintUsage()
    {
        Console.WriteLine("""
            Usage: SmokeSynth --text <line> --out <file.wav> [options]

            Required:
              --text <line>        Dialogue line to perform.
              --out <file.wav>     Output WAV path.

            Options:
              --ref <voice.wav>      Reference voice clip (default: bundled voices/default_voice.wav).
              --exaggeration <0..1>  Performance intensity (default 0.5).
              --tags <a,b,c>         Comma-separated paralinguistic tags.
              --ep <name>            Execution provider (auto|coreml|cpu|directml; default auto).
              --models <dir>         Model directory (default ./models).
              --rtf                  Print the real-time factor after synthesis.
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
