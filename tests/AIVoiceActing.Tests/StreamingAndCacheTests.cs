namespace AIVoiceActing.Tests;

using AIVoiceActing.Domain;
using AIVoiceActing.Domain.Handlers;
using AIVoiceActing.Infrastructure.Audio;
using AIVoiceActing.Ports;
using AIVoiceActing.Tests.Mock;
using Xunit;

/// <summary>
/// Sentence streaming (chunker + handler loop + queue skip-line) and the render cache.
/// </summary>
public sealed class StreamingAndCacheTests
{
    private const string LongLine =
        "The Warriors of Light have returned from the frozen north at last. " +
        "We must gather the Scions before the Garleans move again! " +
        "Will you stand with us?";

    private static readonly SpeakerIdentity Speaker = new("npc: alphinaud", "Alphinaud", 1, 1, 0, null);

    private static SpeechRequestHandler Handler(
        FakeSpeechSynthesizer synth,
        FakeSpeechQueue queue,
        bool stream,
        SynthesisCache? cache = null) =>
        new(
            lexicon: new FakeLexicon(),
            dialogueSessions: new DialogueSessionFactory(),
            synthesizer: () => synth,
            queue: queue,
            profileLookup: _ => new VoiceProfile(Speaker.Key, "am_adam", 0f, DateTimeOffset.UnixEpoch, Custom: false),
            directorFactory: () => null,
            streamSentences: () => stream,
            cache: cache,
            engineKey: () => "kokoro");

    [Fact]
    public void Chunker_KeepsShortLinesWhole()
    {
        Assert.Equal(["Yes. Of course. Right away."], SentenceChunker.Split("  Yes. Of course. Right away.  "));
    }

    [Fact]
    public void StreamingChunker_PacksSentencesUpToThreeHundredChars()
    {
        var chunks = SentenceChunker.SplitForStreaming(LongLine);

        Assert.Equal(LongLine.Trim().Length <= 300 ? 1 : chunks.Count, chunks.Count);
        Assert.All(chunks, c => Assert.True(c.Length <= 300 || !c.Contains(". ")));
        Assert.Equal(LongLine.Trim(), string.Join(" ", chunks));
    }

    [Fact]
    public void StreamingChunker_SplitsOnlyAtSentenceEnds_AndKeepsAnOverlongSentenceWhole()
    {
        var long1 = string.Join(" ", Enumerable.Repeat("word", 70)) + "."; // 349 chars, one sentence
        var chunks = SentenceChunker.SplitForStreaming($"Hello there. {long1} Bye now.");

        Assert.Equal(["Hello there.", long1, "Bye now."], chunks);
    }

    [Fact]
    public void StreamingChunker_KeepsShortLinesWhole()
    {
        Assert.Equal(["Yes. Of course."], SentenceChunker.SplitForStreaming(" Yes. Of course. "));
    }

    [Fact]
    public void Chunker_SplitsLongLines_OnSentenceEnds()
    {
        var chunks = SentenceChunker.Split(LongLine);
        Assert.Equal(
            [
                "The Warriors of Light have returned from the frozen north at last.",
                "We must gather the Scions before the Garleans move again! Will you stand with us?",
            ],
            chunks);
        Assert.Equal(LongLine, string.Join(' ', chunks));
    }

    [Fact]
    public void Chunker_IgnoresDecimalsAndAbbreviations()
    {
        var line = "Bring me 3.5 ilms of e.g. aetherial thread and some cotton yarn, then return... " +
                   "Quickly now, before the merchants at the Steps of Nald close their stalls for the night!";
        var chunks = SentenceChunker.Split(line);
        Assert.Equal(2, chunks.Count);
        Assert.StartsWith("Bring me 3.5 ilms of e.g. aetherial", chunks[0]);
        Assert.EndsWith("return...", chunks[0]);
    }

    [Fact]
    public async Task Handler_Streams_ChunksShareLineId_AndEmotion()
    {
        var synth = new FakeSpeechSynthesizer();
        var queue = new FakeSpeechQueue();
        await Handler(synth, queue, stream: true).SpeakAsync(null, Speaker, LongLine, CancellationToken.None);

        Assert.Equal(2, queue.Enqueued.Count);
        Assert.Equal(2, synth.Calls.Count);
        Assert.NotEqual(0, queue.Enqueued[0].LineId);
        Assert.Equal(queue.Enqueued[0].LineId, queue.Enqueued[1].LineId);
        Assert.All(queue.Enqueued, i => Assert.False(string.IsNullOrEmpty(i.Emotion)));
    }

    [Fact]
    public async Task Handler_StreamingOff_SpeaksWholeLine()
    {
        var synth = new FakeSpeechSynthesizer();
        var queue = new FakeSpeechQueue();
        await Handler(synth, queue, stream: false).SpeakAsync(null, Speaker, LongLine, CancellationToken.None);

        var item = Assert.Single(queue.Enqueued);
        Assert.Equal(0, item.LineId);
        Assert.Equal(LongLine, item.Request.Text);
    }

    [Fact]
    public async Task Handler_RepeatedLine_IsServedFromCache()
    {
        var synth = new FakeSpeechSynthesizer();
        var queue = new FakeSpeechQueue();
        var cache = new SynthesisCache();
        var handler = Handler(synth, queue, stream: false, cache);

        await handler.SpeakAsync(null, Speaker, "For the Alliance!", CancellationToken.None);
        await handler.SpeakAsync(null, Speaker, "For the Alliance!", CancellationToken.None);

        Assert.Single(synth.Calls);
        Assert.Equal(2, queue.Enqueued.Count);
        Assert.Equal(1, cache.Count);
    }

    [Fact]
    public void Cache_KeyCoversVoiceShaping()
    {
        var a = new SynthesisRequest("af_heart", "Hello", 0.5f, [], Pitch: 1f);
        Assert.Equal(SynthesisCache.KeyFor("kokoro", a), SynthesisCache.KeyFor("kokoro", a with { }));
        Assert.NotEqual(SynthesisCache.KeyFor("kokoro", a), SynthesisCache.KeyFor("f5", a));
        Assert.NotEqual(SynthesisCache.KeyFor("kokoro", a), SynthesisCache.KeyFor("kokoro", a with { Pitch = 1.1f }));
        Assert.NotEqual(SynthesisCache.KeyFor("kokoro", a), SynthesisCache.KeyFor("kokoro", a with { Tags = ["whisper"] }));
    }

    [Fact]
    public void Cache_KeyIncludesTheEngineIdPrecisionAndDeviceClass()
    {
        var line = new SynthesisRequest("af_heart", "Hello", 0.5f, []);
        var turboGpu = SynthesisCache.KeyFor(SynthesisCache.EngineKey("chatterbox-turbo", "q4f16/gpu"), line);

        Assert.Equal(turboGpu, SynthesisCache.KeyFor(SynthesisCache.EngineKey("chatterbox-turbo", "q4f16/gpu"), line));
        Assert.NotEqual(turboGpu, SynthesisCache.KeyFor(SynthesisCache.EngineKey("chatterbox-turbo", "q4f16/cpu"), line)); // device class
        Assert.NotEqual(turboGpu, SynthesisCache.KeyFor(SynthesisCache.EngineKey("chatterbox-turbo", "fp16/gpu"), line)); // precision
        Assert.NotEqual(turboGpu, SynthesisCache.KeyFor(SynthesisCache.EngineKey("kokoro", "q4f16/gpu"), line)); // engine id
        Assert.Equal(SynthesisCache.KeyFor("kokoro", line), SynthesisCache.KeyFor(SynthesisCache.EngineKey("kokoro", string.Empty), line));
    }

    [Fact]
    public async Task Handler_ADeviceSwitch_IsACacheMiss_AndTheAudioIsFiledUnderTheKeyThatRenderedIt()
    {
        var synth = new FakeSpeechSynthesizer();
        var queue = new FakeSpeechQueue();
        var cache = new SynthesisCache();
        var variant = "q4f16/gpu";
        var handler = new SpeechRequestHandler(
            lexicon: new FakeLexicon(),
            dialogueSessions: new DialogueSessionFactory(),
            synthesizer: () => synth,
            queue: queue,
            profileLookup: _ => new VoiceProfile(Speaker.Key, "am_adam", 0f, DateTimeOffset.UnixEpoch, Custom: false),
            directorFactory: () => null,
            streamSentences: () => false,
            cache: cache,
            engineKey: () => SynthesisCache.EngineKey("chatterbox-turbo", variant));

        await handler.SpeakAsync(null, Speaker, "For the Alliance!", CancellationToken.None);
        variant = "q4f16/cpu"; // e.g. the GPU failed and the engine fell back
        await handler.SpeakAsync(null, Speaker, "For the Alliance!", CancellationToken.None);
        await handler.SpeakAsync(null, Speaker, "For the Alliance!", CancellationToken.None);

        Assert.Equal(2, synth.Calls.Count); // the CPU render was a miss once, then a hit
        Assert.Equal(2, cache.Count);
    }

    [Fact]
    public void Cache_EvictsLeastRecentlyUsed_WhenOverBudget()
    {
        var cache = new SynthesisCache();
        var size = (int)(SynthesisCache.MaxSamples / 4); // the largest entry accepted
        var audio = new SynthesisResult(new float[size], 24000);
        cache.Put("a", audio);
        cache.Put("b", audio);
        cache.Put("c", audio);
        Assert.True(cache.TryGet("a", out _)); // touch a: b is now the oldest
        cache.Put("d", audio);
        cache.Put("e", audio);

        Assert.False(cache.TryGet("b", out _));
        Assert.True(cache.TryGet("a", out _));
        Assert.True(cache.TryGet("e", out _));
    }

    [Fact]
    public void Cache_RejectsOversizedEntries()
    {
        var cache = new SynthesisCache();
        cache.Put("huge", new SynthesisResult(new float[(SynthesisCache.MaxSamples / 4) + 1], 24000));
        Assert.Equal(0, cache.Count);
    }

    [Fact]
    public void Queue_CancelCurrent_DropsRestOfStreamedLine_KeepsNextLine()
    {
        var sink = new FakeAudioSink { OnPlay = (_, _, cancel) => cancel };
        using var queue = new PlaybackSpeechQueue(sink);

        queue.Enqueue(Item(1, lineId: 7));
        queue.Enqueue(Item(2, lineId: 7));
        queue.Enqueue(Item(3, lineId: 8));
        Assert.True(SpinWait.SpinUntil(() => sink.Played.Count == 1, TimeSpan.FromSeconds(5)));
        Assert.Equal(7, queue.NowPlaying?.LineId);

        queue.CancelCurrent();
        Assert.True(SpinWait.SpinUntil(() => sink.Played.Count == 2, TimeSpan.FromSeconds(5)));
        Assert.Equal([1, 3], sink.Played.Select(p => p.Audio.Samples.Length));
        Assert.Equal(8, queue.NowPlaying?.LineId);

        queue.CancelCurrent();
        Assert.True(SpinWait.SpinUntil(() => queue.NowPlaying is null, TimeSpan.FromSeconds(5)));
    }

    private static SpeechItem Item(int length, long lineId) => new(
        Speaker,
        new SynthesisRequest("default", "line", 0.5f, []),
        new SynthesisResult(new float[length], 24000),
        LineId: lineId);
}
