namespace AIVoiceActing.Tests;

using AIVoiceActing.Domain;
using AIVoiceActing.Domain.Handlers;
using AIVoiceActing.Infrastructure.Audio;
using AIVoiceActing.Ports;
using AIVoiceActing.Tests.Mock;
using Xunit;

/// <summary>
/// Chunked audio on its way to the speakers: the handler hands a line to the queue as soon as the first
/// piece exists, the queue plays the stream while it fills, and skipping the line stops the synthesizer.
/// </summary>
public sealed class StreamingPlaybackTests
{
    private static readonly SpeakerIdentity Speaker = new("npc: alphinaud", "Alphinaud", 1, 1, 0, null);

    private static SpeechRequestHandler Handler(
        FakeSpeechSynthesizer synth,
        FakeSpeechQueue queue,
        bool streamSentences = true,
        SynthesisCache? cache = null,
        Func<string, string>? stutter = null) =>
        new(
            lexicon: new FakeLexicon(),
            dialogueSessions: new DialogueSessionFactory(),
            synthesizer: () => synth,
            queue: queue,
            profileLookup: _ => new VoiceProfile(Speaker.Key, "am_adam", 0f, DateTimeOffset.UnixEpoch, Custom: false),
            directorFactory: () => null,
            removeStutters: stutter,
            streamSentences: () => streamSentences,
            cache: cache,
            engineKey: () => "turbo");

    private static (FakeSpeechSynthesizer Synth, FakeSpeechQueue Queue) Rig(bool synthStreams = true, bool queueStreams = true) =>
        (new FakeSpeechSynthesizer { SupportsStreaming = synthStreams }, new FakeSpeechQueue { SupportsStreaming = queueStreams });

    // ---- handler --------------------------------------------------------------------------------------

    [Fact]
    public async Task AStreamingEngine_HandsTheLineToTheQueueAsAStream_AndTheAudioArrivesInOrder()
    {
        var (synth, queue) = Rig();
        var seenWhenQueued = new List<int>();
        synth.AfterPiece = _ =>
        {
            lock (queue.Enqueued)
            {
                if (queue.Enqueued.Count > 0)
                {
                    seenWhenQueued.Add(queue.Enqueued[0].Stream!.Buffered);
                }
            }
        };

        await Handler(synth, queue).SpeakAsync(null, Speaker, "For the Alliance!", CancellationToken.None);

        var item = Assert.Single(queue.Enqueued);
        Assert.NotNull(item.Stream);
        Assert.Empty(item.Audio.Samples);
        Assert.Equal(24000, item.Stream.SampleRate);
        Assert.True(item.Stream.IsCompleted);
        Assert.Equal([0.1f, 0.2f, 0.3f, 0.4f, 0.5f], item.Stream.ReadAllBlocking());
        Assert.Single(synth.StreamingCalls);
        Assert.Empty(synth.Calls); // the whole-line path was not used
        Assert.Equal([2, 4, 5], seenWhenQueued); // queued with the first piece, then filled while the rest rendered
    }

    [Fact]
    public async Task TheItemIsQueued_OnlyWhenTheFirstAudioExists()
    {
        var (synth, queue) = Rig();
        var queuedBeforeFirstPiece = -1;
        synth.StreamPieces = _ =>
        {
            queuedBeforeFirstPiece = queue.Enqueued.Count;
            return [[0.5f]];
        };

        await Handler(synth, queue).SpeakAsync(null, Speaker, "For the Alliance!", CancellationToken.None);

        Assert.Equal(0, queuedBeforeFirstPiece);
        Assert.Single(queue.Enqueued);
    }

    [Fact]
    public async Task TheLeadingPause_BecomesSilenceAtTheStartOfTheStream()
    {
        var (synth, queue) = Rig();
        var handler = Handler(synth, queue);

        // A sad line plans a breath before it (EmotionRules); read the plan back from the first item.
        await handler.SpeakAsync(null, Speaker, "I... I have lost them all.", CancellationToken.None);

        var item = Assert.Single(queue.Enqueued);
        var samples = item.Stream!.ReadAllBlocking();
        var lead = samples.TakeWhile(v => v == 0f).Count();
        Assert.Equal(samples.Length - 5, lead); // the five rendered samples come after the silence
    }

    [Fact]
    public async Task WhenTheEngineCannotStream_OrTheQueueCannot_TheOldPathRuns()
    {
        var (synth, queue) = Rig(synthStreams: false);
        await Handler(synth, queue).SpeakAsync(null, Speaker, "For the Alliance!", CancellationToken.None);
        Assert.Null(Assert.Single(queue.Enqueued).Stream);
        Assert.Single(synth.Calls);

        var (synth2, queue2) = Rig(queueStreams: false);
        await Handler(synth2, queue2).SpeakAsync(null, Speaker, "For the Alliance!", CancellationToken.None);
        Assert.Null(Assert.Single(queue2.Enqueued).Stream);
        Assert.Empty(synth2.StreamingCalls);
    }

    [Fact]
    public async Task WithTheStreamingSettingOff_NothingStreams()
    {
        var (synth, queue) = Rig();

        await Handler(synth, queue, streamSentences: false).SpeakAsync(null, Speaker, "For the Alliance!", CancellationToken.None);

        Assert.Null(Assert.Single(queue.Enqueued).Stream);
    }

    [Fact]
    public async Task AStreamedLine_IsCachedWhole_AndARepeatPlaysFromTheCacheWithoutStreaming()
    {
        var (synth, queue) = Rig();
        var cache = new SynthesisCache();
        var handler = Handler(synth, queue, cache: cache);

        await handler.SpeakAsync(null, Speaker, "For the Alliance!", CancellationToken.None);
        await handler.SpeakAsync(null, Speaker, "For the Alliance!", CancellationToken.None);

        Assert.Equal(1, cache.Count);
        Assert.Single(synth.StreamingCalls); // the second line never reached the engine
        Assert.Equal(2, queue.Enqueued.Count);
        Assert.NotNull(queue.Enqueued[0].Stream);
        Assert.Null(queue.Enqueued[1].Stream);
        Assert.Equal([0.1f, 0.2f, 0.3f, 0.4f, 0.5f], queue.Enqueued[1].Audio.Samples);
    }

    [Fact]
    public async Task LongLines_StillSplitIntoSentences_AndEachStreamsUnderOneLineId()
    {
        var (synth, queue) = Rig();
        const string line =
            "The Warriors of Light have returned from the frozen north at last. " +
            "We must gather the Scions before the Garleans move again! Will you stand with us?";

        await Handler(synth, queue).SpeakAsync(null, Speaker, line, CancellationToken.None);

        Assert.Equal(2, queue.Enqueued.Count);
        Assert.All(queue.Enqueued, i => Assert.NotNull(i.Stream));
        Assert.NotEqual(0, queue.Enqueued[0].LineId);
        Assert.Equal(queue.Enqueued[0].LineId, queue.Enqueued[1].LineId);
        Assert.Equal(2, synth.StreamingCalls.Count);
    }

    [Fact]
    public async Task SkippingTheLineMidRender_StopsTheSynthesizer_AndDropsTheRestOfTheLine()
    {
        var (synth, queue) = Rig();
        const string line =
            "The Warriors of Light have returned from the frozen north at last. " +
            "We must gather the Scions before the Garleans move again! Will you stand with us?";
        var pieces = 0;
        synth.StreamPieces = _ => Enumerable.Range(0, 50).Select(_ => new float[] { 0.1f });
        synth.AfterPiece = i =>
        {
            pieces++;
            if (i == 2)
            {
                // The player pressed skip: the queue cancels the stream it is playing.
                queue.Enqueued[0].Stream!.Cancel();
            }
        };

        await Handler(synth, queue).SpeakAsync(null, Speaker, line, CancellationToken.None);

        Assert.Equal(3, pieces); // the synthesizer stopped at the next piece
        Assert.Single(queue.Enqueued); // and the second sentence was never rendered
        Assert.Single(synth.StreamingCalls);
    }

    [Fact]
    public async Task ACancelledSpeakRequest_EndsTheStream_AndPropagates()
    {
        var (synth, queue) = Rig();
        using var cts = new CancellationTokenSource();
        synth.AfterPiece = i =>
        {
            if (i == 1)
            {
                cts.Cancel();
            }
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => Handler(synth, queue).SpeakAsync(null, Speaker, "For the Alliance!", cts.Token));

        Assert.True(queue.Enqueued[0].Stream!.IsCancelled);
    }

    [Fact]
    public async Task AFailureMidLine_EndsTheStreamCleanly_AndStopsTheLine()
    {
        var (synth, queue) = Rig();
        const string line =
            "The Warriors of Light have returned from the frozen north at last. " +
            "We must gather the Scions before the Garleans move again! Will you stand with us?";
        synth.AfterPiece = i =>
        {
            if (i == 1)
            {
                synth.Throw = new SpeechSynthesisException("decoder blew up");
                throw synth.Throw;
            }
        };

        await Handler(synth, queue).SpeakAsync(null, Speaker, line, CancellationToken.None);

        var item = Assert.Single(queue.Enqueued);
        Assert.True(item.Stream!.IsCompleted); // the device is not left waiting for audio that will never come
        Assert.Single(synth.StreamingCalls);
    }

    // ---- queue ----------------------------------------------------------------------------------------

    private static SpeechItem Streamed(SpeechAudioStream stream, long lineId = 0) => new(
        new SpeakerIdentity("npc:x", "x", null, null, null, null),
        new SynthesisRequest("default", "line", 0.5f, []),
        new SynthesisResult([], stream.SampleRate),
        0,
        lineId,
        null,
        stream);

    [Fact]
    public void TheQueue_PlaysAStreamAsItFills_ThroughTheSink()
    {
        var sink = new FakeAudioSink();
        using var queue = new PlaybackSpeechQueue(sink);
        var stream = new SpeechAudioStream(24000);
        stream.Append(Enumerable.Repeat(0.2f, 2000).ToArray());
        queue.Enqueue(Streamed(stream));

        Assert.True(SpinWait.SpinUntil(() => queue.NowPlaying is not null, TimeSpan.FromSeconds(5)));
        stream.Append(Enumerable.Repeat(0.2f, 3000).ToArray());
        stream.Complete();

        Assert.True(SpinWait.SpinUntil(() => sink.Streamed.Count == 1, TimeSpan.FromSeconds(5)));
        Assert.Equal(5000, sink.Streamed[0].Length);
        Assert.Equal(0f, sink.Streamed[0][0]); // faded in
        Assert.Equal(0.2f, sink.Streamed[0][2500], 4);
        Assert.Equal(24000, sink.StreamedSampleRates[0]);
        Assert.True(SpinWait.SpinUntil(() => queue.NowPlaying is null, TimeSpan.FromSeconds(5)));
        Assert.True(queue.SupportsStreaming);
    }

    [Fact]
    public void TheQueue_AppliesTheLiveVolume_ToStreamedAudio()
    {
        var sink = new FakeAudioSink();
        using var queue = new PlaybackSpeechQueue(sink, () => 0.5f);
        var stream = new SpeechAudioStream(24000);
        stream.Append(Enumerable.Repeat(0.4f, 4000).ToArray());
        stream.Complete();

        queue.Enqueue(Streamed(stream));

        Assert.True(SpinWait.SpinUntil(() => sink.Streamed.Count == 1, TimeSpan.FromSeconds(5)));
        Assert.Equal(0.2f, sink.Streamed[0][2000], 4);
    }

    [Fact]
    public void CancelCurrent_CancelsTheStreamBeingPlayed_AndSkipsTheRestOfItsLine_ButNotTheNextLine()
    {
        var sink = new FakeAudioSink();
        using var queue = new PlaybackSpeechQueue(sink);
        var first = new SpeechAudioStream(24000);
        var second = new SpeechAudioStream(24000);
        var next = new SpeechAudioStream(24000);
        first.Append(new float[1000]);
        queue.Enqueue(Streamed(first, lineId: 7));
        queue.Enqueue(Streamed(second, lineId: 7));
        queue.Enqueue(Streamed(next, lineId: 8));
        next.Append(Enumerable.Repeat(0.3f, 1000).ToArray());
        next.Complete();
        Assert.True(SpinWait.SpinUntil(() => queue.NowPlaying is not null, TimeSpan.FromSeconds(5)));

        queue.CancelCurrent();

        Assert.True(first.IsCancelled); // the synthesizer feeding it is told to stop
        Assert.True(SpinWait.SpinUntil(() => sink.Streamed.Count == 2, TimeSpan.FromSeconds(5)));
        Assert.True(second.IsCancelled, "the rest of the skipped line is dropped without playing");
        Assert.Equal(1000, sink.Streamed[1].Length);
    }

    [Fact]
    public void Clear_CancelsQueuedStreams_SoTheirSynthesizersStop()
    {
        var sink = new FakeAudioSink { OnPlay = (_, _, cancel) => cancel };
        using var queue = new PlaybackSpeechQueue(sink);
        var playing = new SpeechAudioStream(24000);
        var waiting = new SpeechAudioStream(24000);
        playing.Append(new float[500]);
        queue.Enqueue(Streamed(playing));
        Assert.True(SpinWait.SpinUntil(() => queue.NowPlaying is not null, TimeSpan.FromSeconds(5)));
        queue.Enqueue(Streamed(waiting));

        queue.Clear();

        Assert.True(waiting.IsCancelled);
        Assert.True(playing.IsCancelled);
    }

    [Fact]
    public void AnUnsupportedSink_DropsStreamsAndTellsTheSynthesizerToStop()
    {
        var sink = new FakeAudioSink { IsSupported = false };
        using var queue = new PlaybackSpeechQueue(sink);
        var stream = new SpeechAudioStream(24000);

        queue.Enqueue(Streamed(stream));

        Assert.True(SpinWait.SpinUntil(() => stream.IsCancelled, TimeSpan.FromSeconds(5)));
        Assert.Empty(sink.Streamed);
    }

    [Fact]
    public void ASinkThatCannotStream_PlaysTheWholeLineOnceItIsComplete()
    {
        var sink = new FakeAudioSink { StreamingDisabled = true };
        using var queue = new PlaybackSpeechQueue(sink);
        var stream = new SpeechAudioStream(24000);
        stream.Append(Enumerable.Repeat(0.2f, 1500).ToArray());
        queue.Enqueue(Streamed(stream));

        Thread.Sleep(50);
        Assert.Empty(sink.Played); // nothing yet: the whole line is not there
        stream.Append(Enumerable.Repeat(0.2f, 1500).ToArray());
        stream.Complete();

        Assert.True(SpinWait.SpinUntil(() => sink.Played.Count == 1, TimeSpan.FromSeconds(5)));
        Assert.Equal(3000, sink.Played[0].Audio.Samples.Length);
    }
}
