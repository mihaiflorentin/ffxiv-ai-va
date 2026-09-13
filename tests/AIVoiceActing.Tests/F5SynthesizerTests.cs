namespace AIVoiceActing.Tests;

using AIVoiceActing.Infrastructure.F5;
using AIVoiceActing.Tests.Mock;
using Xunit;

/// <summary>
/// F5 adapter readiness contract: IsReady means "model files + at least one reference
/// clip present", NOT "transformer session already built" — the 1.3 GB session builds
/// lazily on first use (or the login pre-warm), so gating on construction would deadlock
/// the Test tab buttons that trigger the build.
/// </summary>
public sealed class F5SynthesizerTests
{
    [Fact]
    public void MissingModel_IsNotReady_WithDownloadHint()
    {
        using var synth = new F5Synthesizer(() => NewDir(), () => 4, new FakeLogSink());
        Assert.False(synth.IsReady);
        Assert.Contains("Models tab", synth.NotReadyReason);
    }

    [Fact]
    public void ModelWithoutClips_IsNotReady_WithClipsHint()
    {
        var modelsDir = NewDir();
        File.WriteAllText(F5Synthesizer.TransformerPathFor(modelsDir), "stub");

        using var synth = new F5Synthesizer(() => modelsDir, () => 4, new FakeLogSink());

        Assert.False(synth.IsReady);
        Assert.Contains("reference voices", synth.NotReadyReason);
    }

    [Fact]
    public void ModelAndClipBankPresent_IsReady_WithoutBuildingTheSession()
    {
        var modelsDir = NewDir();
        File.WriteAllText(F5Synthesizer.TransformerPathFor(modelsDir), "stub");
        var clipsDir = NewDir();
        File.WriteAllText(Path.Combine(clipsDir, "uk_male_casual.wav"), "stub");
        File.WriteAllText(Path.Combine(clipsDir, "uk_male_casual.txt"), "hello there");

        using var synth = new F5Synthesizer(
            () => modelsDir, () => 4, new FakeLogSink(), voicesDirFactory: () => clipsDir);

        Assert.True(synth.IsReady);
        Assert.Equal(string.Empty, synth.NotReadyReason);
    }

    private static string NewDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "aiva-f5", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }
}
