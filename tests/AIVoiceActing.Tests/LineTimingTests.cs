namespace AIVoiceActing.Tests;

using AIVoiceActing.Domain;
using AIVoiceActing.Infrastructure.Diagnostics;
using AIVoiceActing.Infrastructure.Onnx;
using AIVoiceActing.UI.State;
using AIVoiceActing.Ports;
using AIVoiceActing.Tests.Mock;
using Xunit;

public sealed class LatencyHistogramTests
{
    [Fact]
    public void Empty_AnswersZero()
    {
        var histogram = new LatencyHistogram();

        Assert.Equal(0, histogram.P50);
        Assert.Equal(0, histogram.P95);
        Assert.Equal(0, histogram.Max);
    }

    [Fact]
    public void Percentiles_AreNearestRank()
    {
        var histogram = new LatencyHistogram();
        for (var ms = 1; ms <= 100; ms++)
        {
            histogram.Add(ms);
        }

        Assert.Equal(100, histogram.Count);
        Assert.Equal(50, histogram.P50);
        Assert.Equal(95, histogram.P95);
        Assert.Equal(100, histogram.Max);
    }

    [Fact]
    public void ASlowTail_ShowsInP95_NotInP50()
    {
        var histogram = new LatencyHistogram();
        for (var i = 0; i < 18; i++)
        {
            histogram.Add(10);
        }

        histogram.Add(200);
        histogram.Add(250);

        Assert.Equal(10, histogram.P50);
        Assert.Equal(200, histogram.P95);
    }
}

public sealed class PlaybackMeterTests
{
    [Fact]
    public void RunsOfSilenceWhileTheLineIsStillMade_AreOneUnderrunEach()
    {
        var meter = new PlaybackMeter(1000, () => 5000);

        meter.OnRead(100, 100, sourceFinished: false); // sound
        meter.OnRead(100, 0, sourceFinished: false);   // dry
        meter.OnRead(100, 0, sourceFinished: false);   // still dry: same underrun
        meter.OnRead(100, 100, sourceFinished: false); // back
        meter.OnRead(100, 40, sourceFinished: false);  // dry again, partly

        Assert.Equal(2, meter.UnderrunCount);
        Assert.Equal(260, meter.SilenceMs);
        Assert.Equal(5000, meter.FirstSoundAtTicks);
    }

    [Fact]
    public void BeforeTheFirstSound_AndAfterTheEnd_NothingCounts()
    {
        var meter = new PlaybackMeter(1000);

        meter.OnRead(100, 0, sourceFinished: false); // nothing to run dry of yet
        meter.OnRead(100, 100, sourceFinished: false);
        meter.OnRead(100, 30, sourceFinished: true);  // tail of a finished line

        Assert.Equal(0, meter.UnderrunCount);
        Assert.Equal(0, meter.SilenceMs);
    }
}

public sealed class LineTimerTests
{
    private long clock = 10_000;

    private LineTimer NewTimer(ILineTimingLog? log = null, long arrivedAgoMs = 0)
    {
        var arrived = this.clock - arrivedAgoMs;
        return new LineTimer(231, arrived, log, () => this.clock);
    }

    [Fact]
    public void QueueWait_IsArrivalToTheStartOfTheWork()
    {
        var timer = this.NewTimer(arrivedAgoMs: 2500);

        Assert.Equal(2500, timer.Snapshot().QueueWaitMs);
    }

    [Fact]
    public void Snapshot_CarriesTheStagesAndSumsChunks()
    {
        var timer = this.NewTimer(arrivedAgoMs: 100);
        timer.SetChunks(2);
        timer.SetReference("rendered", 3100);
        timer.AddEncoder(800);
        timer.AddGateWait(5);
        timer.AddPrefill(200);
        timer.AddLanguageModel(100, 2000);
        timer.AddLanguageModel(56, 1000);
        timer.StepLatency.Add(20);
        timer.StepLatency.Add(60);
        timer.AddDecoder(900, 3);
        timer.AddDecoder(300, 1);
        timer.AddAudio(48_000, 24_000);
        timer.AddAudio(24_000, 24_000);
        timer.SetEngine("turbo gpu q4f16");
        timer.SetStreamed();
        this.clock += 6700;
        timer.MarkFirstAudio();
        this.clock += 100;
        timer.MarkFirstAudio();
        timer.MarkRenderDone();

        var t = timer.Snapshot();

        Assert.Equal(231, t.LineChars);
        Assert.Equal(2, t.Chunks);
        Assert.Equal("rendered", t.ReferenceKind);
        Assert.Equal(3100, t.ReferencePrepMs);
        Assert.Equal(800, t.EncoderMs);
        Assert.Equal(156, t.LmTokens);
        Assert.Equal(3000, t.LmMs);
        Assert.Equal(20, t.LmMsPerTokenP50);
        Assert.Equal(60, t.LmMsPerTokenP95);
        Assert.Equal(1200, t.DecoderMsTotal);
        Assert.Equal(4, t.Windows);
        Assert.Equal(6800, t.FirstAudioMs); // arrival to the first call only
        Assert.Equal(6800, t.TotalRenderMs);
        Assert.Equal(3000, t.AudioMs);
        Assert.True(t.Streamed);
        Assert.Equal("turbo gpu q4f16", t.Engine);
    }

    [Fact]
    public void PlaybackReports_AddUpAndTheFirstSoundMeasuresFromArrival()
    {
        var timer = this.NewTimer(arrivedAgoMs: 1000);

        timer.AddPlayback(new PlaybackReport(300, 1, 2400, this.clock + 5000));
        timer.AddPlayback(new PlaybackReport(250, 0, 0, this.clock + 9000));
        var t = timer.Snapshot();

        Assert.Equal(550, t.DeviceOpenMs);
        Assert.Equal(1, t.UnderrunCount);
        Assert.Equal(2400, t.SilenceMs);
        Assert.Equal(6000, t.StartToSoundMs);
    }

    [Fact]
    public void NotMeasured_StaysMinusOne()
    {
        var t = this.NewTimer().Snapshot();

        Assert.Equal(-1, t.FirstAudioMs);
        Assert.Equal(-1, t.DeviceOpenMs);
        Assert.Equal(-1, t.StartToSoundMs);
    }

    [Fact]
    public void Commit_RecordsAgainAsPlaybackAdds_AndADiscardedLineIsNeverRecorded()
    {
        var log = new FakeLineTimingLog();
        var timer = this.NewTimer(log);
        timer.Commit();
        timer.AddPlayback(new PlaybackReport(100, 2, 500, 0));
        timer.Commit();

        Assert.Equal(2, log.Recorded.Count);
        Assert.Equal(log.Recorded[0].Id, log.Recorded[1].Id);
        Assert.Equal(2, log.Recorded[1].UnderrunCount);

        var dropped = this.NewTimer(log);
        dropped.Discard();
        dropped.Commit();
        Assert.Equal(2, log.Recorded.Count);
    }

    [Fact]
    public async Task Current_FlowsIntoWorkStartedFromTheLine()
    {
        var timer = this.NewTimer();
        LineTimer.Current = timer;
        try
        {
            var seen = await Task.Run(() => LineTimer.Current);

            Assert.Same(timer, seen);
        }
        finally
        {
            LineTimer.Current = null;
        }
    }

    [Fact]
    public void Ids_AreDistinct()
    {
        Assert.NotEqual(this.NewTimer().Id, this.NewTimer().Id);
    }
}

public sealed class RingLineTimingLogTests
{
    private static LineTiming Line(long id, int underruns = 0) =>
        new(id, 10, 1, 0, 0, 0, string.Empty, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, -1, -1, -1, underruns, 0, 0, 0, string.Empty, false);

    [Fact]
    public void KeepsTheLastTwentyLines_NewestFirst()
    {
        ILineTimingLog log = new RingLineTimingLog();
        for (var id = 1; id <= 25; id++)
        {
            log.Record(Line(id));
        }

        var recent = log.Recent(100);

        Assert.Equal(20, recent.Count);
        Assert.Equal(25, recent[0].Id);
        Assert.Equal(6, recent[^1].Id);
        Assert.Equal([25L, 24L, 23L], log.Recent(3).Select(l => l.Id));
    }

    [Fact]
    public void ARecordedLineIsReplacedInPlace()
    {
        var log = new RingLineTimingLog(3);
        log.Record(Line(1));
        log.Record(Line(2));
        log.Record(Line(1, underruns: 4));

        var recent = log.Recent(10);

        Assert.Equal([2L, 1L], recent.Select(l => l.Id));
        Assert.Equal(4, recent[1].UnderrunCount);
    }
}

public sealed class TurboModelSetEffectiveTests
{
    [Theory]
    [InlineData(TurboPrecisionRequest.Fp32, DeviceMode.Gpu, TurboPrecisionRequest.Auto)]
    [InlineData(TurboPrecisionRequest.Fp32, DeviceMode.Auto, TurboPrecisionRequest.Auto)]
    [InlineData(TurboPrecisionRequest.Fp32, DeviceMode.Cpu, TurboPrecisionRequest.Fp32)]
    [InlineData(TurboPrecisionRequest.Fp16, DeviceMode.Gpu, TurboPrecisionRequest.Fp16)]
    [InlineData(TurboPrecisionRequest.Q4F16, DeviceMode.Cpu, TurboPrecisionRequest.Q4F16)]
    [InlineData(TurboPrecisionRequest.Auto, DeviceMode.Cpu, TurboPrecisionRequest.Auto)]
    public void OnlyTheProcessorChoiceLetsAFullSizePinApply(TurboPrecisionRequest request, DeviceMode mode, TurboPrecisionRequest expected)
    {
        Assert.Equal(expected, TurboModelSet.Effective(request, mode));
    }
}

public sealed class DiagnosticsTimingTests
{
    private static LineTiming Line() => new(
        12, 231, 3, 0, 10, 20, "rendered", 3100, 800, 0, 200, 156, 4900, 21, 63, 900, 4, 6400, 400, 6800, 1, 2400, 6100, 7200,
        "turbo gpu q4f16", true);

    private static DiagnosticsInput Input(IReadOnlyList<LineTiming>? lines, DiagnosticsFlags? flags = null) => new(
        "0.0.36", 2, "Most expressive", new DeviceSettings(), "Chatterbox Turbo", [], null, false, 0, 0, 0, 0, flags, lines);

    [Fact]
    public void RecentLines_ListEveryRequiredStage_WithoutTextOrNames()
    {
        var report = DiagnosticsReport.Build(Input([Line()]));

        Assert.Contains("Recent lines (newest first", report);
        Assert.Contains("#12 3 chunks, turbo gpu q4f16, streamed:", report);
        Assert.Contains("first audio 6.4", report);
        Assert.Contains("sound 6.8", report);
        Assert.Contains("queue 0.0", report);
        Assert.Contains("ref 3.1 (rendered)", report);
        Assert.Contains("enc 0.8", report);
        Assert.Contains("decoder 0.9 over 4 calls", report);
        Assert.Contains("device 0.4", report);
        Assert.Contains("LM 156 tok 4.9 (p50 21 ms, p95 63 ms)", report);
        Assert.Contains("underruns 1 (2.4 silence)", report);
    }

    [Fact]
    public void NoRecentLines_NoSection()
    {
        Assert.DoesNotContain("Recent lines", DiagnosticsReport.Build(Input([])));
        Assert.DoesNotContain("Recent lines", DiagnosticsReport.Build(Input(null)));
    }

    [Fact]
    public void Flags_AreReported()
    {
        var report = DiagnosticsReport.Build(Input(null, new DiagnosticsFlags("fp32", "q4f16/gpu", true, false, true, true, 8)));

        Assert.Contains("asked for fp32, in use q4f16/gpu", report);
        Assert.Contains("Background load threads: 8", report);
        Assert.Contains("Read sentence by sentence: yes", report);
        Assert.Contains("Even out loudness: no", report);
        Assert.Contains("Emotion detection: yes", report);
        Assert.Contains("Unique voice per character: yes", report);
    }
}
