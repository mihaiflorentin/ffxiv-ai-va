namespace AIVoiceActing.Tests;

using AIVoiceActing.Domain;
using Xunit;

/// <summary>Cross-fade math and the stitching of overlapping decoder windows into one stream.</summary>
public sealed class ChunkStitcherTests
{
    private const int Spt = 960;

    [Fact]
    public void CrossFade_IsEqualPower_AndRunsFromTheOutgoingSideToTheIncomingSide()
    {
        var length = 960;
        var ones = Enumerable.Repeat(1f, length).ToArray();
        var zeros = new float[length];

        var fadingOut = ChunkStitcher.CrossFade(ones, zeros);
        var fadingIn = ChunkStitcher.CrossFade(zeros, ones);

        Assert.True(fadingOut[0] > 0.9999f);
        Assert.True(fadingOut[^1] < 0.002f);
        Assert.True(fadingIn[0] < 0.002f);
        Assert.True(fadingIn[^1] > 0.9999f);
        for (var i = 0; i < length; i++)
        {
            // The two weights are cosine and sine of the same angle: their powers always sum to one.
            Assert.Equal(1.0, (fadingOut[i] * fadingOut[i]) + (fadingIn[i] * fadingIn[i]), 4);
            if (i > 0)
            {
                Assert.True(fadingOut[i] <= fadingOut[i - 1]); // monotone: no bump inside the fade
                Assert.True(fadingIn[i] >= fadingIn[i - 1]);
            }
        }
    }

    [Fact]
    public void CrossFade_OfTwoUncorrelatedSignals_KeepsTheEnergy()
    {
        var random = new Random(7);
        var a = Enumerable.Range(0, 20000).Select(_ => (float)((random.NextDouble() * 2) - 1)).ToArray();
        var b = Enumerable.Range(0, 20000).Select(_ => (float)((random.NextDouble() * 2) - 1)).ToArray();

        var mixed = ChunkStitcher.CrossFade(a, b);

        var energy = (Func<float[], double>)(x => x.Sum(v => (double)v * v) / x.Length);
        Assert.InRange(energy(mixed) / ((energy(a) + energy(b)) / 2), 0.95, 1.05);
    }

    [Fact]
    public void CrossFade_RejectsStretchesOfDifferentLength()
    {
        Assert.Throws<ArgumentException>(() => ChunkStitcher.CrossFade(new float[10], new float[11]));
    }

    [Fact]
    public void CrossFade_OfNothing_IsNothing()
    {
        Assert.Empty(ChunkStitcher.CrossFade([], []));
    }

    /// <summary>A "decoder" whose sample value depends only on the token and the sample's place in it, like a deterministic vocoder would.</summary>
    private static float[] Decode(long[] tokens, int from, int to)
    {
        var audio = new float[(to - from) * Spt];
        for (var t = from; t < to; t++)
        {
            for (var i = 0; i < Spt; i++)
            {
                audio[((t - from) * Spt) + i] = (tokens[t] * 0.001f) + (i * 1e-7f);
            }
        }

        return audio;
    }

    private static (List<float[]> Pieces, List<ChunkPlan> Plans) Stitch(long[] tokens, ChunkScheduleOptions options, int halfFade = 480, int arrival = 7)
    {
        var schedule = new ChunkSchedule(options);
        var stitcher = new ChunkStitcher(Spt, halfFade);
        var pieces = new List<float[]>();
        var plans = new List<ChunkPlan>();
        for (var available = arrival; ; available = Math.Min(tokens.Length, available + arrival))
        {
            var finished = available >= tokens.Length;
            available = Math.Min(available, tokens.Length);
            while (schedule.TryTake(available, finished) is { } plan)
            {
                plans.Add(plan);
                pieces.Add(stitcher.Add(plan, Decode(tokens, plan.WindowStart, plan.WindowEnd)));
            }

            if (finished)
            {
                return (pieces, plans);
            }
        }
    }

    [Theory]
    [InlineData(30)]
    [InlineData(43)]
    [InlineData(100)]
    [InlineData(255)]
    public void TheStitchedPieces_AddUpToExactlyTheWholeLine(int total)
    {
        var tokens = Enumerable.Range(0, total).Select(i => (long)(100 + (i % 50))).ToArray();
        var options = new ChunkScheduleOptions(8, 1.5, 40, 6, 4, 2);

        var (pieces, plans) = Stitch(tokens, options);

        Assert.Equal(total * Spt, pieces.Sum(p => p.Length));
        Assert.True(plans.Count > 1 || total <= 14);
    }

    [Fact]
    public void WithoutAnyFade_StitchingReproducesTheWholeDecodeExactly()
    {
        var tokens = Enumerable.Range(0, 120).Select(i => (long)(100 + (i % 37))).ToArray();
        var options = new ChunkScheduleOptions(10, 1.3, 30, 6, 4, 2);

        var (pieces, _) = Stitch(tokens, options, halfFade: 0);

        var whole = Decode(tokens, 0, tokens.Length);
        Assert.Equal(whole, pieces.SelectMany(p => p).ToArray());
    }

    [Fact]
    public void WithAFade_OnlyTheSamplesAroundTheBoundariesChange_AndStayWithinTheFadeBounds()
    {
        // Both windows hold the identical signal here (the worst case for an equal-power fade), so the mix may
        // rise by at most sqrt(2) over the shared value and never fall below it.
        var tokens = Enumerable.Range(0, 100).Select(i => (long)(200 + i)).ToArray();
        var options = new ChunkScheduleOptions(20, 1.0, 20, 6, 4, 2);

        var (pieces, plans) = Stitch(tokens, options);

        var whole = Decode(tokens, 0, tokens.Length);
        var stitched = pieces.SelectMany(p => p).ToArray();
        Assert.Equal(whole.Length, stitched.Length);
        var boundaries = plans.Take(plans.Count - 1).Select(p => p.End * Spt).ToArray();
        for (var i = 0; i < whole.Length; i++)
        {
            var nearBoundary = boundaries.Any(b => i >= b - 480 && i < b + 480);
            if (!nearBoundary)
            {
                Assert.Equal(whole[i], stitched[i]);
            }
            else
            {
                Assert.InRange(stitched[i], whole[i] - 1e-4f, (whole[i] * 1.415f) + 1e-4f);
            }
        }
    }

    [Fact]
    public void ThePiecesCome_InOrder_WithTheFirstOneAvailableBeforeTheLastWindowIsDecoded()
    {
        var tokens = Enumerable.Range(0, 100).Select(i => (long)(100 + i)).ToArray();
        var (pieces, plans) = Stitch(tokens, new ChunkScheduleOptions(20, 1.0, 20, 6, 4, 2));

        Assert.True(pieces.Count >= 4);
        Assert.Equal(plans.Count, pieces.Count);
        // The first piece is everything up to the first boundary's fade: audible right after one decoder call.
        Assert.Equal((20 * Spt) - 480, pieces[0].Length);
        // A middle piece is its chunk plus the fade into it, minus the fade out held for the next window.
        Assert.Equal(20 * Spt, pieces[1].Length);
    }

    [Fact]
    public void ALineThatFitsOneChunk_IsReturnedWhole()
    {
        var tokens = Enumerable.Range(0, 12).Select(i => (long)(100 + i)).ToArray();

        var (pieces, plans) = Stitch(tokens, new ChunkScheduleOptions(32, 1.5, 120, 16, 8, 4), arrival: 12);

        Assert.Single(plans);
        Assert.Equal(Decode(tokens, 0, 12), pieces[0]);
    }

    [Fact]
    public void AWindowOfTheWrongLength_IsFitted_AndCounted()
    {
        var stitcher = new ChunkStitcher(Spt, 480);
        var plan = new ChunkPlan(0, 0, 10, 0, 10, IsLast: true);

        var shortAudio = stitcher.Add(plan, new float[(10 * Spt) - 5]);

        Assert.Equal(10 * Spt, shortAudio.Length);
        Assert.Equal(1, stitcher.LengthMismatches);
    }

    [Fact]
    public void AWindowWithTooLittleContext_IsRefused()
    {
        var stitcher = new ChunkStitcher(Spt, 480);
        stitcher.Add(new ChunkPlan(0, 0, 10, 0, 12, IsLast: false), new float[12 * Spt]);

        // No left context at all: the fade into this chunk would have nothing to read.
        Assert.Throws<ArgumentException>(() => stitcher.Add(new ChunkPlan(1, 10, 20, 10, 20, IsLast: true), new float[10 * Spt]));
    }

    [Fact]
    public void AChunkShorterThanItsTwoFades_IsRefused()
    {
        var stitcher = new ChunkStitcher(Spt, 700); // two fades of 700 samples do not fit in one 960-sample token
        stitcher.Add(new ChunkPlan(0, 0, 10, 0, 12, IsLast: false), new float[12 * Spt]);

        Assert.Throws<ArgumentException>(() => stitcher.Add(new ChunkPlan(1, 10, 11, 8, 13, IsLast: false), new float[5 * Spt]));
    }
}
