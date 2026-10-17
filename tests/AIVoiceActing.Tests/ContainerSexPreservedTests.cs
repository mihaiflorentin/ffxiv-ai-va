namespace AIVoiceActing.Tests;

using System.Reflection;
using AIVoiceActing.Container;
using AIVoiceActing.Domain;
using AIVoiceActing.Ports;
using AIVoiceActing.Tests.Mock;
using Xunit;

/// <summary>
/// Regression for issue 24 (female characters spoke with male voices under "Most expressive"): through the
/// real container and router, a female speaker's voice must reach each engine as a female voice. Kokoro
/// receives her (generated) voice id; Turbo, which only clones, receives a clip rendered by Kokoro from that
/// same id, never a bank default picked without regard to sex.
/// </summary>
public sealed class ContainerSexPreservedTests : IClassFixture<ContainerUniqueVoiceTests.StockBank>, IDisposable
{
    private static readonly SpeakerIdentity Woman = new("npc:cemi", "Cemi", Race: 3, Tribe: 1, Sex: 1, World: null);

    private readonly string root = Path.Combine(Path.GetTempPath(), "aiva-sex", Guid.NewGuid().ToString("N"));
    private readonly ContainerUniqueVoiceTests.StockBank bank;

    public ContainerSexPreservedTests(ContainerUniqueVoiceTests.StockBank bank)
    {
        this.bank = bank;
        Directory.CreateDirectory(this.root);
    }

    public void Dispose() => Directory.Delete(this.root, recursive: true);

    private ServiceContainer Container(string engine, bool unique, bool expressive = false) =>
        new(
            logSinkOverride: new FakeLogSink(),
            profileStorePathFactory: () => Path.Combine(this.root, "voice-assignments.json"),
            modelsDirFactory: () => Path.Combine(this.root, "models"),
            voicesManifestFactory: () => Path.Combine(AppContext.BaseDirectory, "voices.json"),
            speechQueueFactory: () => new FakeSpeechQueue(),
            kokoroVoicesDirFactory: () => this.bank.Dir,
            uniqueVoicePerCharacterFactory: () => unique,
            generatedVoicesDirFactory: () => Path.Combine(this.root, "voices-generated"),
            selectedEngineFactory: () => engine,
            expressiveEngineFactory: () => expressive,
            expressiveEngineIdFactory: () => "chatterbox-turbo");

    private static void Set(ServiceContainer container, string field, object value) =>
        typeof(ServiceContainer).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(container, value);

    private static FakeSpeechSynthesizer Fake() => new()
    {
        SynthesizeFunc = (_, _) => new SynthesisResult(new float[24000 * 4], 24000),
    };

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task MostExpressive_TurboPrimary_ClonesAClipRenderedFromTheWomansKokoroVoice(bool unique)
    {
        using var container = this.Container("chatterbox-turbo", unique);
        var voiceId = container.ResolveProfile(Woman).ReferenceVoiceId;
        Assert.Matches("^.f_", voiceId);
        var turbo = Fake();
        var kokoro = Fake();
        Set(container, "speechSynthesizer", turbo);
        Set(container, "kokoroReferenceRenderer", kokoro);

        // A calm line: not "emotional", yet it must still be cloned from her own voice.
        await container.RoutedSynthesizer.SynthesizeAsync(
            new SynthesisRequest(voiceId, "Good morning.", 0.5f, [], Pitch: 1.1f), CancellationToken.None);

        Assert.Equal(voiceId, Assert.Single(kokoro.Calls).Request.ReferenceVoiceId);
        var clip = Assert.Single(turbo.Calls).Request.ReferenceVoiceId;
        Assert.True(Path.IsPathRooted(clip), clip);
        Assert.Contains(ServiceContainer.ExpressiveClipsFolder, clip);
        Assert.Contains(voiceId, clip);
    }

    [Fact]
    public async Task Balanced_EmotionalLine_ClonesAClipRenderedFromTheWomansKokoroVoice_CalmLineStaysOnKokoro()
    {
        Directory.CreateDirectory(Path.Combine(this.root, "models"));
        foreach (var asset in Infrastructure.Onnx.ModelCatalog.TurboRequiredAssets)
        {
            using var file = File.Create(Path.Combine(this.root, "models", asset.FileName));
            file.SetLength(asset.SizeBytes ?? 1024);
        }

        using var container = this.Container("kokoro", unique: true, expressive: true);
        var voiceId = container.ResolveProfile(Woman).ReferenceVoiceId;
        var kokoro = Fake();
        var turbo = Fake();
        Set(container, "speechSynthesizer", kokoro);
        Set(container, "expressiveSynthesizer", turbo);

        await container.RoutedSynthesizer.SynthesizeAsync(
            new SynthesisRequest(voiceId, "Calm.", 0.5f, []), CancellationToken.None);
        await container.RoutedSynthesizer.SynthesizeAsync(
            new SynthesisRequest(voiceId, "Aaah!", 0.9f, []), CancellationToken.None);

        var clip = Assert.Single(turbo.Calls).Request.ReferenceVoiceId;
        Assert.Contains(voiceId, clip);
        Assert.Contains(kokoro.Calls, c => c.Request.Text == "Calm." && c.Request.ReferenceVoiceId == voiceId);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Fast_Kokoro_ReceivesTheWomansVoiceId(bool unique)
    {
        using var container = this.Container("kokoro", unique);
        var voiceId = container.ResolveProfile(Woman).ReferenceVoiceId;
        var kokoro = Fake();
        Set(container, "speechSynthesizer", kokoro);

        await container.RoutedSynthesizer.SynthesizeAsync(
            new SynthesisRequest(voiceId, "Hello.", 0.5f, []), CancellationToken.None);

        Assert.Equal(voiceId, Assert.Single(kokoro.Calls).Request.ReferenceVoiceId);
        Assert.Matches("^.f_", voiceId);
    }

    [Theory]
    [InlineData("bf_gen_3a0db9ea2d33", "uk_female_soft")]
    [InlineData("af_gen_1", "uk_female_soft")]
    [InlineData("af_bella", "uk_female_soft")]
    [InlineData("jf_alpha", "uk_female_soft")]
    [InlineData("bm_gen_3a0db9ea2d33", "uk_male_casual")]
    [InlineData("bf_emma", "uk_female_posh")]
    [InlineData(null, "uk_male_casual")]
    public void BundledClipFallback_FollowsTheVoicesSex(string? voiceId, string expected) =>
        Assert.Equal(expected, ReferenceClipBank.ClipIdFor(voiceId));
}
