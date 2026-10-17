namespace AIVoiceActing.Tests;

using AIVoiceActing.Infrastructure.Kokoro;
using AIVoiceActing.Infrastructure.Storage;
using AIVoiceActing.Ports;
using KokoroSharp;
using KokoroSharp.Core;
using AIVoiceActing.Infrastructure.Onnx;
using AIVoiceActing.Tests.Mock;
using Xunit;

/// <summary>
/// Kokoro adapter readiness contract: IsReady means "model + voice banks present", NOT
/// "ONNX session already built" — the session builds lazily on first use, so gating on
/// construction deadlocks the Test tab (buttons that would build it stay disabled).
/// </summary>
public sealed class KokoroSynthesizerTests
{
    [Fact]
    public void MissingModel_IsNotReady_WithDownloadHint()
    {
        using var synth = new KokoroSynthesizer(() => NewDir(), () => 4, new FakeLogSink());
        Assert.False(synth.IsReady);
        Assert.Contains("Models tab", synth.NotReadyReason);
    }

    [Fact]
    public void ModelAndVoicesPresent_IsReady_WithoutBuildingTheSession()
    {
        var modelsDir = NewDir();
        File.WriteAllText(Path.Combine(modelsDir, ModelCatalog.KokoroModelFileName), "stub");
        var voicesDir = NewDir();
        File.WriteAllText(Path.Combine(voicesDir, "af_heart.npy"), "stub");

        using var synth = new KokoroSynthesizer(
            () => modelsDir, () => 4, new FakeLogSink(), voicesDirFactory: () => voicesDir);

        Assert.True(synth.IsReady);
        Assert.Equal(string.Empty, synth.NotReadyReason);
    }

    [Fact]
    public void PackVoice_LoadsItsTensorUnderAnInternalNameThatSelectsThePhonemizer()
    {
        var root = PackFixture.NewDir();
        PackFixture.WritePack(root, "pack-a", "isles.ie_m_01");
        var library = new FileVoiceLibrary(() => [root]);
        var log = new FakeLogSink();
        using var synth = new KokoroSynthesizer(() => NewDir(), () => 4, log, voiceLibrary: library);

        var voice = synth.ResolveVoice("isles.ie_m_01");

        // en-gb phonemizer -> Kokoro's British letter; the public id is untouched.
        Assert.Equal("bm_pk_isles.ie_m_01", voice.Name);
        Assert.Equal(KokoroLanguage.BritishEnglish, voice.Language);
        Assert.Equal(510, voice.Features.GetLength(0));
        Assert.Equal(256, voice.Features.GetLength(2));
        Assert.Contains(log.Snapshot(), e => e.Message.Contains("isles.ie_m_01") && e.Message.Contains("en-gb"));
        Assert.Same(voice, synth.ResolveVoice("isles.ie_m_01"));
    }

    [Fact]
    public void PackVoice_WithAmericanPhonemizer_GetsTheAmericanLetter()
    {
        var root = PackFixture.NewDir();
        var dir = Path.Combine(root, "pack-us");
        PackFixture.WriteTensor(dir, "style/us.npy");
        File.WriteAllText(
            Path.Combine(dir, "voicepack.json"),
            PackFixture.Manifest("pack-us", [("us.f_01", "style/us.npy")], accent: "en-IE", sex: "female", phonemizer: "en-us"));
        using var synth = new KokoroSynthesizer(
            () => NewDir(), () => 4, new FakeLogSink(), voiceLibrary: new FileVoiceLibrary(() => [root]));

        var voice = synth.ResolveVoice("us.f_01");

        Assert.Equal("af_pk_us.f_01", voice.Name);
        Assert.Equal(KokoroLanguage.AmericanEnglish, voice.Language);
    }

    [Fact]
    public void ReplacedTensorFile_IsReloaded()
    {
        var root = PackFixture.NewDir();
        var dir = PackFixture.WritePack(root, "pack-a", "isles.ie_m_01");
        using var synth = new KokoroSynthesizer(
            () => NewDir(), () => 4, new FakeLogSink(), voiceLibrary: new FileVoiceLibrary(() => [root]));
        var first = synth.ResolveVoice("isles.ie_m_01");

        var path = Path.Combine(dir, "style", "isles.ie_m_01.npy");
        File.WriteAllBytes(path, PackFixture.Npy());
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(5));

        Assert.NotSame(first, synth.ResolveVoice("isles.ie_m_01"));
    }

    [Fact]
    public void MissingOrUnavailableVoice_FallsBackToTheDefaultWithALogLine()
    {
        EnsureDefaultVoiceInBank();
        var root = PackFixture.NewDir();
        var dir = PackFixture.WritePack(root, "pack-a", "isles.ie_m_01");
        File.Delete(Path.Combine(dir, "style", "isles.ie_m_01.npy"));
        var log = new FakeLogSink();
        using var synth = new KokoroSynthesizer(
            () => NewDir(), () => 4, log, voiceLibrary: new FileVoiceLibrary(() => [root]));

        Assert.Equal("af_heart", synth.ResolveVoice("isles.ie_m_01").Name);
        Assert.Equal("af_heart", synth.ResolveVoice("pack.that.was.removed").Name);
        Assert.Equal(2, log.Snapshot().Count(e => e.Level == "Warn" && e.Message.Contains("falling back to af_heart")));
    }

    [Fact]
    public void StockVoice_ResolvesByNameFromTheBank()
    {
        EnsureDefaultVoiceInBank();
        using var synth = new KokoroSynthesizer(
            () => NewDir(), () => 4, new FakeLogSink(), voiceLibrary: new FileVoiceLibrary(() => []));

        Assert.Equal("af_heart", synth.ResolveVoice("af_heart").Name);
    }

    /// <summary>The bank is process-wide static state; give the fallback something to land on.</summary>
    private static void EnsureDefaultVoiceInBank()
    {
        lock (KokoroVoiceManager.Voices)
        {
            if (!KokoroVoiceManager.Voices.Any(v => v.Name == "af_heart"))
            {
                KokoroVoiceManager.Voices.Add(new KokoroVoice { Name = "af_heart", Features = new float[510, 1, 256] });
            }
        }
    }

    private static string NewDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "aiva-kokoro", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }
}
