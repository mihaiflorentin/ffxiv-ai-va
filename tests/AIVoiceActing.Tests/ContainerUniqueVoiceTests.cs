namespace AIVoiceActing.Tests;

using System.Reflection;
using AIVoiceActing.Container;
using AIVoiceActing.Domain;
using AIVoiceActing.Ports;
using AIVoiceActing.Tests.Mock;
using Xunit;

/// <summary>
/// Unique voice per character through the real container: the first resolution blends a voice
/// from the speaker's casting set, the profile store keeps it, a new session finds it again,
/// and the expressive-engine reference clip is rendered once per voice.
/// </summary>
public sealed class ContainerUniqueVoiceTests : IClassFixture<ContainerUniqueVoiceTests.StockBank>, IDisposable
{
    /// <summary>Stock-voice tensors written once for the whole class (they are large).</summary>
    public sealed class StockBank : IDisposable
    {
        public StockBank()
        {
            this.Dir = Path.Combine(Path.GetTempPath(), "aiva-stockbank", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(this.Dir);
            var index = 0;
            foreach (var voice in CoreVoiceManifest.Load().Voices)
            {
                var values = new float[NpyHeader.StyleRows * NpyHeader.StyleColumns];
                Array.Fill(values, 0.01f * (++index));
                File.WriteAllBytes(Path.Combine(this.Dir, voice.Id + ".npy"), NpyHeader.WriteStyleTensor(values));
            }
        }

        public string Dir { get; }

        public void Dispose() => Directory.Delete(this.Dir, recursive: true);
    }

    private static readonly SpeakerIdentity LalafellMale =
        new("npc:tataru", "Tataru", Race: 3, Tribe: 1, Sex: 0, World: null);

    private readonly string root = Path.Combine(Path.GetTempPath(), "aiva-unique", Guid.NewGuid().ToString("N"));
    private readonly StockBank bank;

    public ContainerUniqueVoiceTests(StockBank bank)
    {
        this.bank = bank;
        Directory.CreateDirectory(this.root);
    }

    public void Dispose() => Directory.Delete(this.root, recursive: true);

    private ServiceContainer Container(bool unique = true) =>
        new(
            logSinkOverride: new FakeLogSink(),
            profileStorePathFactory: () => Path.Combine(this.root, "voice-assignments.json"),
            modelsDirFactory: () => Path.Combine(this.root, "models"),
            voicesManifestFactory: () => Path.Combine(AppContext.BaseDirectory, "voices.json"),
            speechQueueFactory: () => new FakeSpeechQueue(),
            kokoroVoicesDirFactory: () => this.bank.Dir,
            uniqueVoicePerCharacterFactory: () => unique,
            generatedVoicesDirFactory: () => Path.Combine(this.root, "voices-generated"));

    [Fact]
    public void FirstResolution_GivesAGeneratedVoice_KeepsTheSlotsTuning_AndPersistsIt()
    {
        using var container = this.Container();

        var profile = container.ResolveProfile(LalafellMale);

        Assert.True(VoiceBlend.IsGeneratedId(profile.ReferenceVoiceId), profile.ReferenceVoiceId);
        Assert.StartsWith("bm_gen_", profile.ReferenceVoiceId);
        Assert.False(profile.Custom);
        Assert.Equal(1.12f, profile.Pitch);
        Assert.Equal(1.05f, profile.Speed);
        Assert.Equal(profile, container.ProfileStore.Entries.Single());
        Assert.True(container.VoiceLibrary.Has(profile.ReferenceVoiceId));
        var record = Assert.Single(Directory.GetFiles(Path.Combine(this.root, "voices-generated"), "*.json"));
        Assert.Contains("npc:tataru", File.ReadAllText(record));
    }

    [Fact]
    public void LaterLinesAndNewSessions_ReuseTheSameVoice()
    {
        string first;
        using (var container = this.Container())
        {
            first = container.ResolveProfile(LalafellMale).ReferenceVoiceId;
            Assert.Equal(first, container.ResolveProfile(LalafellMale).ReferenceVoiceId);
        }

        using var next = this.Container();
        Assert.Equal(first, next.ResolveProfile(LalafellMale).ReferenceVoiceId);
        Assert.Single(Directory.GetFiles(Path.Combine(this.root, "voices-generated"), "*.npy"));
    }

    [Fact]
    public void DifferentCharacters_GetDifferentVoices()
    {
        using var container = this.Container();

        var ids = Enumerable.Range(0, 12)
            .Select(i => container.ResolveProfile(
                new SpeakerIdentity($"npc:person {i}", $"Person {i}", 1, 1, 0, null)).ReferenceVoiceId)
            .ToHashSet();

        Assert.Equal(12, ids.Count);
    }

    [Fact]
    public void WithTheSettingOff_NoVoiceIsGenerated()
    {
        using var container = this.Container(unique: false);

        var profile = container.ResolveProfile(LalafellMale);

        Assert.False(VoiceBlend.IsGeneratedId(profile.ReferenceVoiceId));
        Assert.False(Directory.Exists(Path.Combine(this.root, "voices-generated")));
    }

    [Fact]
    public void AHandPickedVoice_IsNeverReplaced()
    {
        using var container = this.Container();
        container.ProfileStore.SetOverride(LalafellMale.Key, "bm_daniel", 0f);

        var profile = container.ResolveProfile(LalafellMale);

        Assert.Equal("bm_daniel", profile.ReferenceVoiceId);
        Assert.True(profile.Custom);
    }

    [Fact]
    public void KeepAsIs_PinsTheGeneratedVoice()
    {
        using var container = this.Container();
        var generated = container.ResolveProfile(LalafellMale);

        container.ProfileStore.SetVoice(LalafellMale.Key, generated.ReferenceVoiceId, custom: true);

        var again = container.ResolveProfile(LalafellMale);
        Assert.Equal(generated.ReferenceVoiceId, again.ReferenceVoiceId);
        Assert.True(again.Custom);
        Assert.Equal(generated.Pitch, again.Pitch);
    }

    [Fact]
    public void Regenerate_ThroughTheGeneratorPort_SwapsTheVoiceInPlace()
    {
        using var container = this.Container();
        var generated = container.ResolveProfile(LalafellMale);

        var next = container.GeneratedVoices!.Regenerate(generated.ReferenceVoiceId)!;
        container.ProfileStore.SetVoice(LalafellMale.Key, next.Id, custom: false);

        Assert.Equal(next.Id, container.ResolveProfile(LalafellMale).ReferenceVoiceId);
        Assert.NotEqual(generated.ReferenceVoiceId, next.Id);
    }

    [Fact]
    public void AGeneratedVoiceWhoseFilesVanished_IsRebuiltTheSameWay()
    {
        string first;
        using (var container = this.Container())
        {
            first = container.ResolveProfile(LalafellMale).ReferenceVoiceId;
        }

        foreach (var file in Directory.GetFiles(Path.Combine(this.root, "voices-generated")))
        {
            File.Delete(file);
        }

        using var next = this.Container();
        Assert.Equal(first, next.ResolveProfile(LalafellMale).ReferenceVoiceId);
        Assert.Equal(2, Directory.GetFiles(Path.Combine(this.root, "voices-generated")).Length);
    }

    [Fact]
    public async Task ExpressiveClip_IsRenderedOncePerGeneratedVoice()
    {
        using var container = this.Container();
        var voiceId = container.ResolveProfile(LalafellMale).ReferenceVoiceId;
        var engine = new FakeSpeechSynthesizer
        {
            SynthesizeFunc = (_, _) => new SynthesisResult(new float[24000 * 4], 24000),
        };
        typeof(ServiceContainer)
            .GetField("speechSynthesizer", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(container, engine);
        var prepare = typeof(ServiceContainer).GetMethod(
            "PrepareExpressiveVoiceAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;

        async Task<string?> Clip(string line) => await (Task<string?>)prepare.Invoke(
            container, [new SynthesisRequest(voiceId, line, 0.9f, [], Pitch: 1.12f), CancellationToken.None])!;

        var first = await Clip("I cannot believe it!");
        var second = await Clip("A different line entirely.");

        Assert.NotNull(first);
        Assert.Equal(first, second);
        Assert.Single(engine.Calls);
        Assert.Equal(voiceId, engine.Calls[0].Request.ReferenceVoiceId);
        Assert.Contains(ServiceContainer.ExpressiveClipsFolder, first);
        Assert.Contains(voiceId, first);
    }
}
