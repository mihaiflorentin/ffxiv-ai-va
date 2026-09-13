namespace AIVoiceActing.Tests;

using AIVoiceActing.Infrastructure.Onnx;
using AIVoiceActing.Ports;
using AIVoiceActing.Tests.Mock;
using Xunit;

/// <summary>
/// Turbo variant readiness: same "all catalog files present" contract as legacy
/// chatterbox, but over the Turbo asset set (turbo-* file names) and with no LM
/// override seam (the Turbo LM is fp32 by design).
/// </summary>
public sealed class ChatterboxTurboSynthesizerTests
{
    private sealed class FakeTokenizer : ITextTokenizer
    {
        public int[] Encode(string text) => [1, 2, 3];
    }

    [Fact]
    public void MissingAssets_IsNotReady_WithDownloadHint()
    {
        using var synth = ChatterboxSynthesizer.CreateTurbo(NewDir(), _ => null, log: new FakeLogSink());
        Assert.False(synth.IsReady);
        Assert.Contains("Models tab", synth.NotReadyReason);
    }

    [Fact]
    public void AllAssetsPresent_IsReady_WithoutBuildingSessions()
    {
        var dir = NewDir();
        foreach (var asset in ModelCatalog.TurboRequiredAssets)
        {
            // Readiness probes size within tolerance, not content; SetLength creates
            // sparse multi-GB stubs without allocating the bytes.
            using var file = File.Create(Path.Combine(dir, asset.FileName));
            file.SetLength(asset.SizeBytes ?? 1024);
        }

        var defaultVoice = Path.Combine(dir, "default.wav");
        WavCodec.WriteMono24k(defaultVoice, [0f]);
        using var synth = ChatterboxSynthesizer.CreateTurbo(
            dir,
            _ => defaultVoice,
            log: new FakeLogSink(),
            tokenizerFactory: () => new FakeTokenizer());
        Assert.True(synth.IsReady);
    }

    [Fact]
    public void Turbo_IsNotTheLegacyVariant_FileSetsDiffer()
    {
        // The Turbo LM is pinned fp32; the legacy default LM is q4. A wired-up wrong
        // variant would silently look for legacy file names.
        Assert.DoesNotContain(ModelCatalog.TurboRequiredAssets, a => a.FileName == ModelCatalog.LanguageModelQ4FileName);
        Assert.Contains(ModelCatalog.TurboRequiredAssets, a => a.FileName == ModelCatalog.TurboLanguageModelFileName);
    }

    private static string NewDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "aiva-turbo", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }
}
