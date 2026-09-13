namespace AIVoiceActing.Tests;

using AIVoiceActing.Infrastructure.Kokoro;
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

    private static string NewDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "aiva-kokoro", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }
}
