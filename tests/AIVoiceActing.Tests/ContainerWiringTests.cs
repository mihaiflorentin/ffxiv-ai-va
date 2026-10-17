namespace AIVoiceActing.Tests;

using AIVoiceActing.Container;
using AIVoiceActing.Domain;
using AIVoiceActing.Domain.Pipeline;
using AIVoiceActing.Ports;
using AIVoiceActing.Tests.Mock;
using Xunit;

/// <summary>
/// Pins the plugin-wiring seams the review round flagged: the cutscene condition factory
/// must flow through SessionIdDeriver so cutscene lines join the shared "cutscene" context
/// window, and OnlySayFirstOrLastName must be a live (read-per-call) option on the
/// announcer's prefix policy. Both are wired at the plugin ctor (Dalamud-bound), so the
/// container level is the testable seam.
/// </summary>
public sealed class ContainerWiringTests
{
    private static string VoicesManifestPath() => Path.Combine(AppContext.BaseDirectory, "voices.json");

    private static ServiceContainer CutsceneContainer(Func<bool> cutsceneActive) => new(
        logSinkOverride: new FakeLogSink(),
        profileStorePathFactory: () => Path.Combine(Path.GetTempPath(), Path.GetRandomFileName()),
        modelsDirFactory: () => Path.Combine(Path.GetTempPath(), Path.GetRandomFileName()),
        voicesManifestFactory: VoicesManifestPath,
        cutsceneActiveFactory: cutsceneActive,
        speechQueueFactory: () => new Mock.FakeSpeechQueue());

    private static TextEmitEvent TalkLine() => new(
        Source: TextSource.Talk,
        SpeakerName: "Sibold",
        Text: "Well met, adventurer.",
        RawText: "Well met, adventurer.",
        Hint: new SpeakerHint("Sibold", World: null, ObjectIndex: null, ModelCharaId: null, Race: 1, Tribe: 1, Sex: 1),
        ChatType: 0);

    [Fact]
    public void CutsceneFactory_True_FeedsTheSharedContextWindow()
    {
        using var container = CutsceneContainer(() => true);
        _ = container.Pipeline;

        container.PipelineSink.Emit(TalkLine());

        var appended = SpinWait.SpinUntil(
            () => container.DialogueSessions.GetContext(SessionIdDeriver.CutsceneSessionId).Count > 0,
            TimeSpan.FromSeconds(5));
        Assert.True(appended, "expected the line to join the shared cutscene context window");
    }

    [Fact]
    public async Task CutsceneFactory_False_FeedsNoWindow()
    {
        using var container = CutsceneContainer(() => false);
        _ = container.Pipeline;

        container.PipelineSink.Emit(TalkLine());

        // Detached dispatch: give the pipeline a beat, then confirm nothing was recorded.
        await Task.Delay(200);
        Assert.Empty(container.DialogueSessions.GetContext(SessionIdDeriver.CutsceneSessionId));
    }

    [Fact]
    public void OnlySayFirstOrLastName_FlowsLiveFromTheFactory()
    {
        var last = true;
        using var container = new ServiceContainer(
            sayPartialNameFactory: () => true,
            onlySayFirstOrLastNameFactory: () => last ? FirstOrLastName.Last : FirstOrLastName.First);

        Assert.Equal("Bloefhiswyn", container.Announcer.PartialName("Merlwyb Bloefhiswyn"));

        last = false; // option delegates are read per call
        Assert.Equal("Merlwyb", container.Announcer.PartialName("Merlwyb Bloefhiswyn"));
    }
    [Theory]
    [InlineData("kokoro", "KokoroSynthesizer")]
    [InlineData(null, "KokoroSynthesizer")]
    [InlineData("remote", "RemoteSynthesizer")]
    [InlineData("nonsense", "KokoroSynthesizer")]
    [InlineData("chatterbox-turbo", "ChatterboxSynthesizer")]
    [InlineData("turbo", "ChatterboxSynthesizer")] // alias still resolves
    [InlineData("f5", "F5Synthesizer")]
    public void EngineFactory_ResolvesThroughTheCatalog(string? engine, string expectedType)
    {
        using var container = new ServiceContainer(
            logSinkOverride: new FakeLogSink(),
            modelsDirFactory: () => Path.Combine(Path.GetTempPath(), Path.GetRandomFileName()),
            selectedEngineFactory: () => engine!);

        Assert.Equal(expectedType, container.SpeechSynthesizer.GetType().Name);
    }

    [Fact]
    public void EngineCatalog_IsInjectable()
    {
        var catalog = new Infrastructure.Engines.EngineCatalog();
        using var container = new ServiceContainer(
            logSinkOverride: new FakeLogSink(),
            engineCatalogFactory: () => catalog);

        Assert.Same(catalog, container.EngineCatalog);
    }
}
