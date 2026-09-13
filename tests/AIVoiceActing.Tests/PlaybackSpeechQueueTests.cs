namespace AIVoiceActing.Tests;

using AIVoiceActing.Domain;
using AIVoiceActing.Infrastructure.Audio;
using AIVoiceActing.Ports;
using AIVoiceActing.Tests.Mock;
using Xunit;

/// <summary>
/// The playback queue's observable contract: sequential order, live volume, CancelCurrent
/// stops the in-flight line but keeps the backlog, Clear drops everything, and an
/// unsupported sink (macOS) skips audio while the queue stays alive. Items carry distinct
/// sample payloads so play order is observable through the fake sink.
/// </summary>
public sealed class PlaybackSpeechQueueTests
{
    private static SpeechItem Item(float tag, long requestedAtTicks = 0) => new(
        new SpeakerIdentity("npc:x", "x", null, null, null, null),
        new SynthesisRequest("default", "line", 0.5f, []),
        new SynthesisResult([tag], 24000),
        requestedAtTicks);

    [Fact]
    public void StaleItems_AreDropped_FreshOnesPlay()
    {
        var sink = new FakeAudioSink();
        using var queue = new PlaybackSpeechQueue(
            sink, staleAfterMsFactory: () => 20_000);

        // Old line (arrived 60s ago) is dropped at dequeue; fresh line plays.
        queue.Enqueue(Item(1, Environment.TickCount64 - 60_000));
        queue.Enqueue(Item(2, Environment.TickCount64 - 1_000));
        Assert.True(
            SpinWait.SpinUntil(() => sink.Played.Count == 1, Seconds(5)),
            "expected exactly the fresh item to play");

        Assert.Equal([2f], sink.Played.Select(p => p.Audio.Samples[0]));
    }

    [Fact]
    public void ZeroTimestamp_AndDisabledWindow_NeverDrop()
    {
        var sink = new FakeAudioSink();
        using var queue = new PlaybackSpeechQueue(
            sink, staleAfterMsFactory: () => 0);

        // RequestedAtTicks=0 (user-initiated tests) plus a disabled window (0): both play.
        queue.Enqueue(Item(1, Environment.TickCount64 - 600_000));
        queue.Enqueue(Item(2, 0));
        Assert.True(
            SpinWait.SpinUntil(() => sink.Played.Count == 2, Seconds(5)),
            "expected both items to play when dropping is disabled");
    }

    [Fact]
    public void PlaysItemsSequentially_InEnqueueOrder_WithLiveVolume()
    {
        var sink = new FakeAudioSink();
        float volume = 0.5f;
        using var queue = new PlaybackSpeechQueue(sink, () => volume);

        queue.Enqueue(Item(1));
        queue.Enqueue(Item(2));
        Assert.True(
            SpinWait.SpinUntil(() => sink.Played.Count == 2, Seconds(5)),
            "expected both items to play");

        Assert.Equal([1f, 2f], sink.Played.Select(p => p.Audio.Samples[0]));

        volume = 1.5f;
        queue.Enqueue(Item(3));
        Assert.True(
            SpinWait.SpinUntil(() => sink.Played.Count == 3, Seconds(5)),
            "expected the third item to play");

        Assert.Equal(1.5f, sink.Played[2].Volume);
    }

    [Fact]
    public void CancelCurrent_StopsInFlight_KeepsBacklog()
    {
        var sink = new FakeAudioSink
        {
            // Every play blocks until cancelled (like device playback mid-line).
            OnPlay = (_, _, cancel) => cancel,
        };
        using var queue = new PlaybackSpeechQueue(sink);

        queue.Enqueue(Item(1));
        queue.Enqueue(Item(2));
        Assert.True(SpinWait.SpinUntil(() => sink.Played.Count == 1, Seconds(5)));

        queue.CancelCurrent();
        Assert.True(
            SpinWait.SpinUntil(() => sink.Played.Count == 2, Seconds(5)),
            "expected the backlog to play after the in-flight line was cancelled");

        Assert.Equal(1, sink.CancelCalls);
        Assert.Equal([1f, 2f], sink.Played.Select(p => p.Audio.Samples[0]));
    }

    [Fact]
    public async Task Clear_DropsBacklog_AndCancelsCurrent()
    {
        var sink = new FakeAudioSink
        {
            OnPlay = (_, _, cancel) => cancel,
        };
        using var queue = new PlaybackSpeechQueue(sink);

        queue.Enqueue(Item(1));
        queue.Enqueue(Item(2));
        Assert.True(SpinWait.SpinUntil(() => sink.Played.Count == 1, Seconds(5)));

        queue.Clear();

        Assert.Equal(1, sink.CancelCalls);
        Assert.Single(sink.Played);

        // The backlog item was dropped, not played after the cancel released the worker.
        await Task.Delay(200);
        Assert.Single(sink.Played);
    }

    [Fact]
    public async Task UnsupportedSink_SkipsAudio_QueueStaysAlive()
    {
        var sink = new FakeAudioSink { IsSupported = false };
        using var queue = new PlaybackSpeechQueue(sink);

        queue.Enqueue(Item(1));
        queue.Enqueue(Item(2));
        await Task.Delay(200);
        Assert.Empty(sink.Played);

        // Supported flips on (device appears): subsequent items play normally.
        sink.IsSupported = true;
        queue.Enqueue(Item(3));
        Assert.True(
            SpinWait.SpinUntil(() => sink.Played.Count == 1, Seconds(5)),
            "expected the queue to resume playing once the sink is supported");
    }

    [Fact]
    public void Dispose_CancelsCurrent_AndIsIdempotent()
    {
        var sink = new FakeAudioSink { OnPlay = (_, _, cancel) => cancel };
        var queue = new PlaybackSpeechQueue(sink);
        queue.Enqueue(Item(1));
        Assert.True(SpinWait.SpinUntil(() => sink.Played.Count == 1, Seconds(5)));

        queue.Dispose();
        queue.Dispose();

        Assert.Equal(1, sink.CancelCalls);
    }

    [Fact]
    public void PlaybackError_IsLogged_NotFatal()
    {
        var sink = new FakeAudioSink
        {
            OnPlay = (_, _, _) => throw new InvalidOperationException("device gone"),
        };
        var log = new FakeLogSink();
        using var queue = new PlaybackSpeechQueue(sink, log: log);

        queue.Enqueue(Item(1));
        queue.Enqueue(Item(2));
        Assert.True(
            SpinWait.SpinUntil(
                () => log.Snapshot().Any(e => e.Level == "Error") && sink.Played.Count == 2,
                Seconds(5)),
            "expected the failed play to be logged and the next item to still play");
    }

    private static TimeSpan Seconds(int s) => TimeSpan.FromSeconds(s);
}
