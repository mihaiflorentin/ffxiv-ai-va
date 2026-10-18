namespace AIVoiceActing.Tests;

using AIVoiceActing.Domain;
using AIVoiceActing.Infrastructure.Onnx;
using Microsoft.ML.OnnxRuntime.Tensors;
using Xunit;

/// <summary>The producer/consumer hand-over between the language model and the chunked decoder, and the prompt trim.</summary>
public sealed class SpeechTokenFeedTests
{
    private static readonly ChunkScheduleOptions Small = new(8, 1.0, 8, 4, 2, 2);

    [Fact]
    public void AConsumer_ReceivesChunksInOrder_WhileTheProducerKeepsAdding()
    {
        var feed = new SpeechTokenFeed();
        var schedule = new ChunkSchedule(Small);
        var received = new List<ChunkPlan>();
        var consumer = Task.Run(() =>
        {
            while (feed.WaitForPlan(schedule, CancellationToken.None) is { } plan)
            {
                lock (received)
                {
                    received.Add(plan);
                }
            }
        });

        for (var token = 0; token < 40; token++)
        {
            feed.Add(token);
            if (token % 9 == 0)
            {
                Thread.Sleep(2);
            }
        }

        feed.Finish([4299, 4299, 4299]);

        Assert.True(consumer.Wait(5000));
        Assert.Equal(43, received[^1].End);
        Assert.Equal(0, received[0].Start);
        for (var i = 1; i < received.Count; i++)
        {
            Assert.Equal(received[i - 1].End, received[i].Start);
        }

        Assert.Equal(40, feed.SpeechTokens().Length); // the silence suffix is not a speech token
        Assert.Equal(43, feed.Count);
    }

    [Fact]
    public void ASlice_ReturnsTheRequestedTokens()
    {
        var feed = new SpeechTokenFeed();
        for (var token = 0; token < 10; token++)
        {
            feed.Add(token * 10);
        }

        Assert.Equal([30L, 40L, 50L], feed.Slice(3, 6));
    }

    [Fact]
    public void AddingAfterFinish_IsIgnored()
    {
        var feed = new SpeechTokenFeed();
        feed.Add(1);
        feed.Finish([9]);
        feed.Add(2);

        Assert.Equal(2, feed.Count);
        Assert.True(feed.IsFinished);
    }

    [Fact]
    public void CancellingTheWait_StopsTheConsumerPromptly()
    {
        var feed = new SpeechTokenFeed();
        using var cts = new CancellationTokenSource();
        var consumer = Task.Run(() => feed.WaitForPlan(new ChunkSchedule(Small), cts.Token));

        Thread.Sleep(30);
        cts.Cancel();

        var error = Assert.Throws<AggregateException>(() => consumer.Wait(2000));
        Assert.IsAssignableFrom<OperationCanceledException>(error.InnerException);
    }

    [Fact]
    public void AProducerFailure_ReachesTheConsumer_WithItsOriginalType()
    {
        var feed = new SpeechTokenFeed();
        for (var token = 0; token < 20; token++)
        {
            feed.Add(token);
        }

        feed.Fail(new InvalidOperationException("the language model broke"));

        // Plans that were ready are not decoded after a failure: the line is over.
        var error = Assert.Throws<InvalidOperationException>(() => feed.WaitForPlan(new ChunkSchedule(Small), CancellationToken.None));
        Assert.Equal("the language model broke", error.Message);
    }

    [Fact]
    public void WhileItWaits_TheIdleCallbackRuns_OutsideTheFeedLock_SoTheModelIsNotHeldUp()
    {
        var feed = new SpeechTokenFeed();
        var idle = 0;
        var consumer = Task.Run(() => feed.WaitForPlan(new ChunkSchedule(Small), CancellationToken.None, () =>
        {
            Interlocked.Increment(ref idle);
            feed.Add(1); // takes the feed's lock: would deadlock if the callback ran inside it
            if (idle >= 3)
            {
                feed.Finish([]);
            }
        }));

        Assert.True(consumer.Wait(TimeSpan.FromSeconds(10)));
        Assert.True(idle >= 3);
    }

    [Fact]
    public void ACancelledToken_BeforeWaiting_ThrowsAtOnce()
    {
        var feed = new SpeechTokenFeed();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.Throws<OperationCanceledException>(() => feed.WaitForPlan(new ChunkSchedule(Small), cts.Token));
    }

    // ---- PromptTrim -------------------------------------------------------------------------------------

    private static (long[] Tokens, DenseTensor<float> Features) Prompt(int tokens)
    {
        var ids = Enumerable.Range(0, tokens).Select(i => (long)(1000 + i)).ToArray();
        var features = new DenseTensor<float>([1, tokens * 2, 3]);
        for (var frame = 0; frame < tokens * 2; frame++)
        {
            for (var mel = 0; mel < 3; mel++)
            {
                features[0, frame, mel] = (frame * 10) + mel;
            }
        }

        return (ids, features);
    }

    [Fact]
    public void Trim_KeepsTheTail_WithTwoFramesPerToken_StillAligned()
    {
        var (tokens, features) = Prompt(10);

        var (kept, trimmed) = PromptTrim.Tail(tokens, features, 4);

        Assert.Equal([1006L, 1007L, 1008L, 1009L], kept);
        Assert.Equal([1, 8, 3], trimmed.Dimensions.ToArray());
        Assert.Equal(12 * 10f, trimmed[0, 0, 0]); // frame 12 = the first frame of token 6
        Assert.Equal((19 * 10f) + 2, trimmed[0, 7, 2]);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(10)]
    [InlineData(500)]
    public void Trim_LeavesThePromptAlone_WhenThereIsNothingToCut(int keep)
    {
        var (tokens, features) = Prompt(10);

        var (kept, trimmed) = PromptTrim.Tail(tokens, features, keep);

        Assert.Same(tokens, kept);
        Assert.Same(features, trimmed);
    }

    [Fact]
    public void Trim_DoesNotOverrunAShorterFeatureTrack()
    {
        // The test fixtures' encoder reports 6 frames for 5 tokens; a trim must stay inside what exists.
        var tokens = new long[] { 1, 2, 3, 4, 5 };
        var features = new DenseTensor<float>([1, 6, 3]);

        var (kept, trimmed) = PromptTrim.Tail(tokens, features, 4);

        Assert.Equal(4, kept.Length);
        Assert.Equal(6, trimmed.Dimensions[1]);
    }
}
