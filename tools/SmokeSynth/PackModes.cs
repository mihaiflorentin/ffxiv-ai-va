namespace SmokeSynth;

using AIVoiceActing.Domain;
using AIVoiceActing.Infrastructure.Kokoro;
using AIVoiceActing.Infrastructure.Onnx;
using AIVoiceActing.Infrastructure.Storage;
using AIVoiceActing.Ports;

/// <summary>Voice pack authoring modes of the harness: <c>--validate-pack</c> and <c>--audition</c>.</summary>
internal static class PackModes
{
    /// <summary>About three seconds of speech with Kokoro at default speed.</summary>
    public const string DefaultPreviewLine = "The road is long, but the company is good.";

    public static int Validate(string packDirectory)
    {
        var report = VoicePackValidator.Validate(packDirectory);
        if (report.Manifest is { } manifest)
        {
            Console.WriteLine($"pack:     {manifest.Id} {manifest.Version} \"{manifest.Name}\" ({manifest.License.Spdx})");
            Console.WriteLine($"voices:   {manifest.Voices.Count}");
            foreach (var voice in manifest.Voices)
            {
                var kinds = string.Join('+', voice.Realizations.Select(r => r.Kind));
                Console.WriteLine($"  {voice.Id,-18} {voice.Accent,-10} {voice.Sex,-6} {voice.Quality,-12} {kinds}");
            }
        }

        foreach (var warning in report.Warnings)
        {
            Console.WriteLine($"warning:  {warning}");
        }

        foreach (var error in report.Errors)
        {
            Console.Error.WriteLine($"error:    {error}");
        }

        Console.WriteLine(report.IsValid ? "VALID" : $"INVALID ({report.Errors.Count} problem(s))");
        return report.IsValid ? 0 : 1;
    }

    public static int Audition(string packDirectory, string? outputDirectory, string modelsDir, string? line, string? only)
    {
        if (string.IsNullOrWhiteSpace(outputDirectory))
        {
            Console.Error.WriteLine("--audition needs --out <dir>.");
            return 1;
        }

        var report = VoicePackValidator.Validate(packDirectory);
        if (report.Manifest is not { } manifest)
        {
            foreach (var error in report.Errors)
            {
                Console.Error.WriteLine($"error:    {error}");
            }

            return 1;
        }

        var dir = Path.GetFullPath(packDirectory);
        var library = new FileVoiceLibrary(() => [Path.GetDirectoryName(dir.TrimEnd(Path.DirectorySeparatorChar)) ?? dir]);
        var voices = manifest.Voices.Where(v => only is null || v.Id == only).ToList();
        if (voices.Count == 0)
        {
            Console.Error.WriteLine(only is null ? "The pack has no voices." : $"Voice \"{only}\" is not in the pack.");
            return 1;
        }

        Directory.CreateDirectory(outputDirectory);
        var log = new ConsoleLog();
        using var synthesizer = new KokoroSynthesizer(() => modelsDir, () => 4, log, voiceLibrary: library);
        var failures = 0;
        foreach (var voice in voices)
        {
            var info = library.Find(voice.Id);
            if (info is not { Available: true } || !info.Realizations.Any(r => r.Kind == RealizationKind.StyleTensor && r.Available))
            {
                Console.Error.WriteLine($"{voice.Id}: skipped, {info?.UnavailableReason ?? "no usable style tensor"}.");
                failures++;
                continue;
            }

            try
            {
                var request = new SynthesisRequest(
                    ReferenceVoiceId: voice.Id,
                    Text: line ?? DefaultPreviewLine,
                    Exaggeration: 0.5f,
                    Tags: [],
                    Pitch: 1.0f,
                    Speed: 1.0f);
                var result = synthesizer.SynthesizeAsync(request, CancellationToken.None).GetAwaiter().GetResult();
                var fileName = voice.PreviewFile is null ? voice.Id + ".wav" : Path.GetFileName(voice.PreviewFile);
                var path = Path.Combine(outputDirectory, fileName);
                WavCodec.WriteMono24k(path, result.Samples);
                Console.WriteLine($"{voice.Id,-18} {result.Samples.Length / (double)result.SampleRate,5:0.0} s  {path}");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"{voice.Id}: synthesis failed: {ex.Message}");
                failures++;
            }
        }

        Console.WriteLine(failures == 0 ? $"AUDITIONED {voices.Count} voice(s)" : $"{failures} voice(s) failed");
        return failures == 0 ? 0 : 2;
    }

    private sealed class ConsoleLog : ILogSink
    {
        public void Info(string msg) => Console.WriteLine(msg);

        public void Warn(string msg) => Console.WriteLine(msg);

        public void Error(string msg, Exception? ex = null) =>
            Console.Error.WriteLine(ex is null ? msg : $"{msg}: {ex.Message}");
    }
}
