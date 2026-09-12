namespace SmokeSynth;

/// <summary>
/// Offline smoke harness for the AIVoiceActing synthesis engine. The Chatterbox ONNX
/// synthesizer lands in a later step; today this validates the CLI contract and prints
/// the parsed arguments.
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        string? text = null;
        string? reference = null;
        string? executionProvider = null;
        string? output = null;
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
                case "--exaggeration" when i + 1 < args.Length
                        && double.TryParse(args[++i], out exaggeration):
                    break;
                case "--tags" when i + 1 < args.Length:
                    tags.AddRange(args[++i].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
                    break;
                case "--ep" when i + 1 < args.Length:
                    executionProvider = args[++i];
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

        if (string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(reference) || string.IsNullOrWhiteSpace(output))
        {
            Console.Error.WriteLine("Missing required arguments: --text, --ref and --out are required.");
            PrintUsage();
            return 1;
        }

        if (exaggeration is < 0.0 or > 1.0)
        {
            Console.Error.WriteLine("--exaggeration must be within 0.0..1.0.");
            return 1;
        }

        Console.WriteLine("AIVoiceActing SmokeSynth");
        Console.WriteLine($"  text:         {text}");
        Console.WriteLine($"  ref:          {reference}");
        Console.WriteLine($"  exaggeration: {exaggeration:0.##}");
        Console.WriteLine($"  tags:         {(tags.Count > 0 ? string.Join(", ", tags) : "(none)")}");
        Console.WriteLine($"  ep:           {executionProvider ?? "(default)"}");
        Console.WriteLine($"  out:          {output}");
        Console.WriteLine($"  rtf:          {(printRealTimeFactor ? "print" : "no")}");
        Console.WriteLine("Arguments validated; engine synthesis is wired in a later step.");
        return 0;
    }

    private static void PrintUsage()
    {
        Console.WriteLine("""
            Usage: SmokeSynth --text <line> --ref <voice.wav> --out <file.wav> [options]

            Required:
              --text <line>        Dialogue line to perform.
              --ref <voice.wav>    Reference voice clip (timbre seed).
              --out <file.wav>     Output WAV path.

            Options:
              --exaggeration <0..1>  Performance intensity (default 0.5).
              --tags <a,b,c>         Comma-separated paralinguistic tags.
              --ep <name>            Execution provider override (dml|coreml|cpu).
              --rtf                  Print the real-time factor after synthesis.
              --help                 Show this help.
            """);
    }
}
