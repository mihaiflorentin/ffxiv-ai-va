namespace AIVoiceActing.Tests;

using AIVoiceActing.Domain;
using Xunit;

/// <summary>The chunk scheduler: segment sizes, context, tail handling, ordering and exact coverage of the token stream.</summary>
public sealed class ChunkScheduleTests
{
    private static ChunkSchedule Schedule(int first = 32, double growth = 1.5, int max = 120, int tail = 16, int left = 8, int ahead = 4) =>
        new(new ChunkScheduleOptions(first, growth, max, tail, left, ahead));

    /// <summary>Feeds a stream of <paramref name="total"/> tokens that arrives <paramref name="step"/> at a time and collects every plan.</summary>
    private static List<ChunkPlan> Run(ChunkSchedule schedule, int total, int step)
    {
        var plans = new List<ChunkPlan>();
        var available = 0;
        while (true)
        {
            available = Math.Min(total, available + step);
            var finished = available == total;
            while (schedule.TryTake(available, finished) is { } plan)
            {
                plans.Add(plan);
            }

            if (finished)
            {
                return plans;
            }
        }
    }

    [Fact]
    public void FirstChunkIsSmall_ThenGrowsToTheCap()
    {
        var schedule = Schedule();

        Assert.Equal([32, 48, 72, 108, 120, 120], Enumerable.Range(0, 6).Select(schedule.SizeFor));
    }

    [Fact]
    public void TheFirstChunk_WaitsForItsLookahead_ButNotForMore()
    {
        var schedule = Schedule(first: 32, ahead: 4);

        Assert.Null(schedule.TryTake(35, finished: false)); // 32 + 4 needed
        var plan = schedule.TryTake(36, finished: false);

        Assert.NotNull(plan);
        Assert.Equal(0, plan.Start);
        Assert.Equal(32, plan.End);
        Assert.Equal(0, plan.WindowStart);
        Assert.Equal(36, plan.WindowEnd);
        Assert.True(plan.IsFirst);
        Assert.False(plan.IsLast);
    }

    [Fact]
    public void LaterChunks_CarryLeftContext_AndLookahead()
    {
        var schedule = Schedule();
        schedule.TryTake(100, finished: false);

        var second = schedule.TryTake(100, finished: false)!;

        Assert.Equal(32, second.Start);
        Assert.Equal(80, second.End);
        Assert.Equal(24, second.WindowStart); // 8 tokens of context before the chunk
        Assert.Equal(84, second.WindowEnd); // 4 tokens after it
        Assert.Equal(8, second.LeftContext);
        Assert.Equal(4, second.Lookahead);
    }

    [Fact]
    public void AShortTail_JoinsTheLastChunk_InsteadOfCostingACallOfItsOwn()
    {
        var schedule = Schedule();

        var plan = schedule.TryTake(40, finished: true)!; // 8 tokens past the planned 32 is less than the 16-token minimum

        Assert.Equal(0, plan.Start);
        Assert.Equal(40, plan.End);
        Assert.True(plan.IsLast);
        Assert.Equal(40, plan.WindowEnd);
        Assert.Null(schedule.TryTake(40, finished: true));
    }

    [Fact]
    public void ALongEnoughTail_BecomesItsOwnLastChunk_WithoutLookaheadPastTheEnd()
    {
        var schedule = Schedule();

        var first = schedule.TryTake(60, finished: true)!;
        var last = schedule.TryTake(60, finished: true)!;

        Assert.Equal(32, first.End);
        Assert.False(first.IsLast);
        Assert.Equal(36, first.WindowEnd);
        Assert.Equal(32, last.Start);
        Assert.Equal(60, last.End);
        Assert.True(last.IsLast);
        Assert.Equal(60, last.WindowEnd);
    }

    [Fact]
    public void AnEmptyFinishedStream_HasNothingToDecode()
    {
        Assert.Null(Schedule().TryTake(0, finished: true));
    }

    [Fact]
    public void WhileTheStreamIsStillGrowing_NothingIsReleasedEarly()
    {
        var schedule = Schedule();

        Assert.Null(schedule.TryTake(0, finished: false));
        Assert.Null(schedule.TryTake(20, finished: false));
        Assert.Equal(0, schedule.Consumed);
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(7, 3)]
    [InlineData(43, 1)]
    [InlineData(43, 43)]
    [InlineData(100, 7)]
    [InlineData(255, 1)]
    [InlineData(258, 13)]
    [InlineData(500, 50)]
    public void PlansCoverTheStreamExactlyOnce_InOrder_WhateverTheArrivalRhythm(int total, int step)
    {
        var plans = Run(Schedule(), total, step);

        Assert.NotEmpty(plans);
        Assert.Equal(Enumerable.Range(0, plans.Count), plans.Select(p => p.Index));
        Assert.Equal(0, plans[0].Start);
        Assert.Equal(total, plans[^1].End);
        for (var i = 1; i < plans.Count; i++)
        {
            Assert.Equal(plans[i - 1].End, plans[i].Start); // no gap, no overlap between the kept parts
        }

        Assert.All(plans.Take(plans.Count - 1), p => Assert.False(p.IsLast));
        Assert.True(plans[^1].IsLast);
        foreach (var plan in plans)
        {
            Assert.InRange(plan.WindowStart, 0, plan.Start);
            Assert.InRange(plan.WindowEnd, plan.End, total);
            Assert.True(plan.LeftContext <= 8 && plan.Lookahead <= 4);
            Assert.Equal(plan.IsFirst ? 0 : Math.Min(8, plan.Start), plan.LeftContext);
        }
    }

    [Fact]
    public void WhenTheModelOutrunsTheDecoder_ChunksStillGrowOnTheFixedSchedule()
    {
        // Everything is available at once: sizes follow the schedule instead of one huge chunk.
        var plans = Run(Schedule(), total: 400, step: 400);

        Assert.Equal([32, 48, 72, 108, 120, 20], plans.Select(p => p.Count).Take(5).Append(plans[^1].Count));
        Assert.True(plans.All(p => p.Count <= 120));
    }

    [Fact]
    public void SmallestUsefulSchedule_StillWorks()
    {
        var plans = Run(new ChunkSchedule(new ChunkScheduleOptions(1, 1, 1, 0, 0, 0)), total: 5, step: 1);

        Assert.Equal(5, plans.Count);
        Assert.All(plans, p => Assert.Equal(1, p.Count));
    }

    [Theory]
    [InlineData(0, 1.5, 10, 4, 4, 2)]
    [InlineData(8, 0.5, 10, 4, 4, 2)]
    [InlineData(8, 1.5, 4, 4, 4, 2)]
    [InlineData(8, 1.5, 10, 4, -1, 2)]
    [InlineData(8, 1.5, 10, 1, 4, 2)] // the minimum tail must cover the lookahead
    public void NonsenseOptions_AreRejected(int first, double growth, int max, int tail, int left, int ahead)
    {
        Assert.Throws<ArgumentException>(() => new ChunkSchedule(new ChunkScheduleOptions(first, growth, max, tail, left, ahead)));
    }
}
