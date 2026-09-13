namespace AIVoiceActing.Tests;

using AIVoiceActing.Container;
using AIVoiceActing.Domain.Pipeline;
using AIVoiceActing.Tests.Mock;
using Xunit;

/// <summary>
/// Container wiring for the capture pipeline and the voiced-line courtesy: the pipeline is
/// cached, the sink is the pipeline's stream head, and OnVoiceLinePlayback cancels current
/// speech before notifying observers (the plugin polls the talk addons there).
/// </summary>
public sealed class ContainerVoiceLineTests : IDisposable
{
    private readonly TempDir tempDir = new();

    public void Dispose() => this.tempDir.Dispose();

    private ServiceContainer Container(FakeSpeechQueue queue)
    {
        var container = new ServiceContainer(
            logSinkOverride: new FakeLogSink(),
            profileStorePathFactory: () => Path.Combine(this.tempDir.Path, "voice-assignments.json"),
            modelsDirFactory: () => Path.Combine(this.tempDir.Path, "models"),
            voicesManifestFactory: () => Path.Combine(AppContext.BaseDirectory, "voices.json"),
            speechQueueFactory: () => queue);
        return container;
    }

    [Fact]
    public void Pipeline_ResolvesWithoutQueueFactory()
    {
        // Plugin-constructor-equivalent resolution path with NO speech queue factory:
        // the pipeline (and its handler → queue chain) must not throw at load (review
        // round 1 — the no-op queue fallback covers this until the audio sink lands).
        var container = new ServiceContainer(
            logSinkOverride: new FakeLogSink(),
            profileStorePathFactory: () => Path.Combine(this.tempDir.Path, "voice-assignments.json"),
            modelsDirFactory: () => Path.Combine(this.tempDir.Path, "models"),
            voicesManifestFactory: () => Path.Combine(AppContext.BaseDirectory, "voices.json"));
        try
        {
            var pipeline = container.Pipeline;
            Assert.Same(pipeline, container.Pipeline);
            pipeline.NotifyVoiceLinePlayback(); // resolves SpeechHandler → no-op queue
        }
        finally
        {
            container.Dispose();
        }
    }

    [Fact]
    public void Pipeline_IsCached()
    {
        var queue = new FakeSpeechQueue();
        using var container = this.Container(queue);
        Assert.Same(container.Pipeline, container.Pipeline);
    }

    [Fact]
    public void PipelineSink_IsThePipelineStreamHead()
    {
        var queue = new FakeSpeechQueue();
        using var container = this.Container(queue);
        Assert.Same(container.Pipeline.Sink, container.PipelineSink);
    }

    [Fact]
    public void OnVoiceLinePlayback_CancelsSpeech_ThenNotifiesObservers()
    {
        var queue = new FakeSpeechQueue();
        using var container = this.Container(queue);

        var notified = 0;
        container.VoiceLinePlaybackObserved += () => notified++;

        container.OnVoiceLinePlayback();

        Assert.Equal(1, queue.CancelCurrentCalls);
        Assert.Equal(1, notified);
    }

    [Fact]
    public void WiredDetector_RaisingTheEvent_CancelsSpeechThroughTheContainer()
    {
        // Integration: the same subscription the plugin ctor performs — the detector's
        // event must reach the container's courtesy cancel path and its observers.
        var queue = new FakeSpeechQueue();
        using var container = this.Container(queue);
        var detector = new FakeVoiceLineDetector();
        container.WireVoiceLineDetector(detector);

        var notified = 0;
        container.VoiceLinePlaybackObserved += () => notified++;

        detector.Raise();

        Assert.Equal(1, queue.CancelCurrentCalls);
        Assert.Equal(1, notified);
    }

    [Fact]
    public void UnwiredDetector_RaisingTheEvent_IsANoOp()
    {
        var queue = new FakeSpeechQueue();
        using var container = this.Container(queue);
        var detector = new FakeVoiceLineDetector();
        container.WireVoiceLineDetector(detector);
        container.UnwireVoiceLineDetector();

        detector.Raise();

        Assert.Equal(0, queue.CancelCurrentCalls);
    }

    [Fact]
    public void Dispose_UnwiresTheDetector()
    {
        var queue = new FakeSpeechQueue();
        var container = this.Container(queue);
        var detector = new FakeVoiceLineDetector();
        container.WireVoiceLineDetector(detector);
        container.Dispose();

        detector.Raise();

        Assert.Equal(0, queue.CancelCurrentCalls);
    }

    [Fact]
    public void Rewiring_ReplacesThePreviousDetector()
    {
        var queue = new FakeSpeechQueue();
        using var container = this.Container(queue);
        var first = new FakeVoiceLineDetector();
        var second = new FakeVoiceLineDetector();
        container.WireVoiceLineDetector(first);
        container.WireVoiceLineDetector(second);

        first.Raise();
        second.Raise();

        Assert.Equal(1, queue.CancelCurrentCalls);
    }
    [Fact]
    public void Filters_DefaultToPermissivePassthrough()
    {
        var queue = new FakeSpeechQueue();
        using var container = this.Container(queue);

        Assert.True(container.TextGate.IsTextGood("anything"));
        Assert.False(container.TextGate.IsTextBad("anything"));
        Assert.False(container.Announcer.ShouldProcessSpeaker("Yda"));
        Assert.True(container.FromYou.ShouldSayFromYou("Yda"));
        Assert.True(container.ChatGate.IsEnabled(57));
        Assert.False(container.RateLimiter.TryRateLimit(
            new AIVoiceActing.Domain.SpeakerIdentity("pc:X@66", "X", null, null, null, 66)));
    }

    private sealed class TempDir : IDisposable
    {
        public TempDir()
        {
            this.Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), "aiva-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(this.Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(this.Path, recursive: true);
            }
            catch (DirectoryNotFoundException)
            {
            }
        }
    }
}
