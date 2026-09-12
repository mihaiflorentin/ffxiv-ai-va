namespace AIVoiceActing.Tests;

using AIVoiceActing.Infrastructure.Onnx;
using AIVoiceActing.Ports;
using Xunit;

/// <summary>
/// IsReady must tell the truth: every required asset (or the LM actually in use) present
/// and size-sane, and the default reference voice existing on disk — not merely the
/// resolver lambda being non-null.
/// </summary>
public sealed class ChatterboxSynthesizerReadyTests
{
    private sealed class FakeTokenizer : ITextTokenizer
    {
        public int[] Encode(string text) => [1, 2, 3];
    }

    private static readonly string Dir = Path.Combine(Path.GetTempPath(), $"aiva-ready-{Guid.NewGuid():N}");

    private static ChatterboxSynthesizer NewSynthesizer(string? lmOverride = null) =>
        new(
            modelsDir: Dir,
            voicePathResolver: voiceId => voiceId == "default"
                ? Path.Combine(Dir, "default_voice.wav")
                : Path.Combine(Dir, "voices", $"{voiceId}.wav"),
            tokenizerFactory: () => new FakeTokenizer(),
            executionProvider: "cpu",
            languageModelOverride: lmOverride);

    [Fact]
    public void IsReady_FalseWhenDefaultVoiceAbsent_TrueWhenPlaced()
    {
        try
        {
            CreateRequiredAssets();
            // The catalog loop creates the pinned-size asset; remove it to exercise the
            // missing-voice branch (IsReady must consult the resolver, not the lambda).
            File.Delete(Path.Combine(Dir, "default_voice.wav"));
            var synthesizer = NewSynthesizer();
            Assert.False(synthesizer.IsReady); // assets fine, but default_voice.wav missing

            WavCodec.WriteMono24k(Path.Combine(Dir, "default_voice.wav"), [0f]);
            Assert.True(synthesizer.IsReady);
        }
        finally
        {
            Directory.Delete(Dir, recursive: true);
        }
    }

    [Fact]
    public void IsReady_UsesLanguageModelOverride()
    {
        var dir = Dir + "-override";
        try
        {
            CreateRequiredAssets(dir);
            // q4 files deliberately absent: the fp32 override must satisfy readiness.
            var synthesizer = new ChatterboxSynthesizer(
                modelsDir: dir,
                voicePathResolver: voiceId => Path.Combine(dir, "default_voice.wav"),
                tokenizerFactory: () => new FakeTokenizer(),
                executionProvider: "cpu",
                languageModelOverride: ModelCatalog.LanguageModelFp32FileName);
            Assert.False(synthesizer.IsReady); // override file still missing

            SparseFile(Path.Combine(dir, ModelCatalog.LanguageModelFp32FileName), 171387);
            SparseFile(Path.Combine(dir, ModelCatalog.LanguageModelFp32DataFileName), 2080632832);
            WavCodec.WriteMono24k(Path.Combine(dir, "default_voice.wav"), [0f]);
            Assert.True(synthesizer.IsReady);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    private static void CreateRequiredAssets(string? dir = null)
    {
        dir ??= Dir;
        Directory.CreateDirectory(dir);
        foreach (var asset in ModelCatalog.ChatterboxRequiredAssets)
        {
            if (asset.SizeBytes is { } size)
            {
                SparseFile(Path.Combine(dir, asset.FileName), size);
            }
        }
    }

    private static void SparseFile(string path, long size)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write);
        stream.SetLength(size); // sparse on APFS: exact size without the disk cost
    }
}
