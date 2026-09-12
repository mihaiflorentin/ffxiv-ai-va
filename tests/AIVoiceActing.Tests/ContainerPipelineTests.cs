namespace AIVoiceActing.Tests;

using AIVoiceActing.Container;
using AIVoiceActing.Domain;
using AIVoiceActing.Ports;
using AIVoiceActing.Tests.Mock;
using Xunit;

public sealed class ContainerPipelineTests
{
    private static string VoicesManifestPath() => Path.Combine(AppContext.BaseDirectory, "voices.json");

    [Fact]
    public void DialogueSessions_AreCached()
    {
        using var container = new ServiceContainer();

        Assert.Same(container.DialogueSessions, container.DialogueSessions);
    }

    [Fact]
    public void EmotionDirector_IsCached_AndIsTheRulesDirector()
    {
        using var container = new ServiceContainer();

        var first = container.EmotionDirector;
        Assert.Same(first, container.EmotionDirector);
        Assert.IsType<RulesEmotionDirector>(first);
    }

    [Fact]
    public void Lexicon_IsCached_AndDefaultIsPassthrough()
    {
        using var container = new ServiceContainer();

        var lexicon = container.Lexicon;
        Assert.Same(lexicon, container.Lexicon);
        Assert.Equal("unchanged text", lexicon.Apply("unchanged text"));
    }

    [Fact]
    public void Lexicon_AppliesConfiguredEntries()
    {
        using var container = new ServiceContainer(
            lexiconEntriesFactory: () => new Dictionary<string, string> { ["chocobo"] = "kweh" });

        Assert.Equal("The kweh!", container.Lexicon.Apply("The chocobo!"));
    }

    [Fact]
    public void SpeechQueue_ThrowsWithoutFactory()
    {
        using var container = new ServiceContainer();

        var ex = Assert.Throws<InvalidOperationException>(() => _ = container.SpeechQueue);
        Assert.Contains("speechQueueFactory", ex.Message);
    }

    [Fact]
    public void SpeechQueue_FactoryIsInvokedExactlyOnce()
    {
        var calls = 0;
        using var container = new ServiceContainer(speechQueueFactory: () =>
        {
            calls++;
            return new FakeSpeechQueue();
        });

        _ = container.SpeechQueue;
        _ = container.SpeechQueue;

        Assert.Equal(1, calls);
    }

    [Fact]
    public void SpeechHandler_IsCached()
    {
        using var temp = new TempDir();
        using var container = new ServiceContainer(
            logSinkOverride: new FakeLogSink(),
            profileStorePathFactory: () => temp.Path("voice-assignments.json"),
            modelsDirFactory: () => temp.Path("models"),
            voicesManifestFactory: VoicesManifestPath,
            speechQueueFactory: () => new FakeSpeechQueue());

        var handler = container.SpeechHandler;

        Assert.Same(handler, container.SpeechHandler);
        handler.CancelCurrent();
    }

    [Fact]
    public void SpeechHandler_ProfileLookup_CreatesAndPersistsProfiles()
    {
        using var temp = new TempDir();
        using var container = new ServiceContainer(
            logSinkOverride: new FakeLogSink(),
            profileStorePathFactory: () => temp.Path("voice-assignments.json"),
            voicesManifestFactory: VoicesManifestPath,
            speechQueueFactory: () => new FakeSpeechQueue());

        var speaker = new SpeakerIdentity("npc: sibold", "Sibold", Race: 1, Tribe: 1, Sex: 0, World: null);
        var profile = container.ProfileStore.GetOrCreate(
            speaker.Key,
            () => container.VoiceMap.SlotsFor(VoiceGroup.Male, speaker.Race),
            speaker.Race,
            speaker.Tribe,
            speaker.Sex);

        Assert.Equal("default", profile.ReferenceVoiceId);
        Assert.True(File.Exists(temp.Path("voice-assignments.json")));
    }

    private sealed class TempDir : IDisposable
    {
        public TempDir() => this.Root = Directory.CreateTempSubdirectory("aiva-tests").FullName;

        private string Root { get; }

        public string Path(string name) => System.IO.Path.Combine(this.Root, name);

        public void Dispose()
        {
            if (Directory.Exists(this.Root))
            {
                Directory.Delete(this.Root, recursive: true);
            }
        }
    }
}
