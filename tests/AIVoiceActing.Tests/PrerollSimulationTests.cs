namespace AIVoiceActing.Tests;

using AIVoiceActing.Domain;
using Xunit;

/// <summary>
/// The pre-roll gate, the chunk schedule and the decoder against a playback that never waits, on a virtual clock:
/// the language model makes a token every few ms, the decoder takes its time per chunk, and the sound starts when the
/// gate says. What counts is how long the line is held and how long it then sits silent.
/// </summary>
public sealed class PrerollSimulationTests(Xunit.Abstractions.ITestOutputHelper output)
{
    private const int StepMs = 5;

    private sealed record Outcome(long HoldMs, long SilentMs, int Dry, int Chunks, int LargestChunk);

    /// <summary>
    /// One line of <paramref name="tokens"/> speech tokens. <paramref name="smartChunks"/> turns on what the plugin does
    /// (the gate knows the chunk schedule, chunk sizes follow the model's pace); off is the older behaviour.
    /// </summary>
    private static Outcome Simulate(double msPerToken, int tokens, bool smartChunks = true, double prefillMs = 900, int? chars = null, double decodeCost = 0.35, double decodeFixedMs = 150, int maxPrerollMs = 6000)
    {
        long clock = 1000;
        var options = ChunkScheduleOptions.Default;
        var gate = new PrerollGate(new PrerollOptions(MaxMs: maxPrerollMs), chars ?? (int)(tokens / 1.6), () => clock, smartChunks ? options : null);
        long produced = 0;
        long first = -1;
        var buffer = new PrerollBuffer(gate, 1000, pcm =>
        {
            produced += pcm.Length;
            if (first < 0)
            {
                first = clock - 1000;
            }
        });
        var schedule = new ChunkSchedule(options, smartChunks ? () => ChunkSchedule.CapFor(options, gate.LanguageModelRate) : null);

        double nextToken = prefillMs;
        var made = 0;
        var available = 0;
        var finished = false;
        double decoderBusyUntil = 0;
        ChunkPlan? decoding = null;
        long played = 0;
        long silent = 0;
        var dry = 0;
        var inDry = false;
        var chunks = 0;
        var largest = 0;
        long total = 0;

        for (var t = 0L; t < 120_000; t += StepMs)
        {
            clock = 1000 + t;
            while (made < tokens && t >= nextToken)
            {
                made++;
                available++;
                gate.TokenProduced();
                nextToken += msPerToken;
            }

            if (made == tokens && !finished)
            {
                finished = true;
                gate.LanguageModelFinished();
                available += 3;
            }

            if (decoding is not null && t >= decoderBusyUntil)
            {
                var audioMs = decoding.Count * 40L;
                var pcm = new float[audioMs];
                var tookMs = (long)(decodeFixedMs + (decodeCost * audioMs));
                gate.WindowDecoded(tookMs, audioMs);
                total += audioMs;
                buffer.Add(pcm);
                decoding = null;
            }

            if (decoding is null && schedule.TryTake(available, finished) is { } plan)
            {
                decoding = plan;
                chunks++;
                largest = Math.Max(largest, plan.Count);
                var audioMs = plan.Count * 40L;
                decoderBusyUntil = t + decodeFixedMs + (decodeCost * audioMs);
                if (plan.IsLast && finished)
                {
                    // Nothing more can follow this one.
                }
            }

            if (decoding is null && finished && schedule.Consumed >= available)
            {
                buffer.Flush();
            }
            else
            {
                buffer.Tick();
            }

            if (first >= 0)
            {
                if (played < produced)
                {
                    played = Math.Min(produced, played + StepMs);
                    inDry = false;
                }
                else if (!(finished && decoding is null && schedule.Consumed >= available))
                {
                    silent += StepMs;
                    if (!inDry)
                    {
                        dry++;
                        inDry = true;
                    }
                }
            }

            if (finished && decoding is null && schedule.Consumed >= available && played >= produced)
            {
                break;
            }
        }

        return new Outcome(first, silent, dry, chunks, largest);
    }

    [Theory]
    [InlineData(44.0)] // the game keeps the card busy: 0.9 of real time
    [InlineData(37.0)] // just above real time
    [InlineData(27.0)]
    [InlineData(13.0)] // the processor
    public void ALongLine_PlaysThroughWithoutGaps_AtAnyPaceTheHoldCanCover(double msPerToken)
    {
        var outcome = Simulate(msPerToken, tokens: 335);
        output.WriteLine($"{msPerToken} ms/token: {outcome}; old: {Simulate(msPerToken, tokens: 335, smartChunks: false)}");

        Assert.True(outcome.SilentMs <= 150, $"{outcome}");
        Assert.True(outcome.HoldMs <= 6000, $"{outcome}");
    }

    [Fact]
    public void WithoutTheChunkModel_ALongLineJustAboveRealTime_RunsDryMidLine()
    {
        // The behaviour of 0.0.37: the same pace, the hold of the plain buffer rule and chunks of up to 120 tokens.
        var before = Simulate(37.0, tokens: 335, smartChunks: false);
        var after = Simulate(37.0, tokens: 335);

        Assert.True(before.SilentMs >= 500, $"before: {before}");
        Assert.True(after.SilentMs < before.SilentMs, $"before: {before}, after: {after}");
    }

    [Fact]
    public void AModelFarAboveRealTime_StartsWithTheFirstChunk_AndKeepsBigChunks()
    {
        var outcome = Simulate(5.0, tokens: 335, prefillMs: 300);

        Assert.True(outcome.HoldMs <= 1500, $"{outcome}");
        Assert.Equal(0, outcome.SilentMs);
        Assert.True(outcome.LargestChunk >= 100, $"{outcome}"); // the cap does not bite
    }

    [Fact]
    public void AModelNearRealTime_GetsSmallChunks_SoTheHoldStaysShort()
    {
        var near = Simulate(37.0, tokens: 335);

        Assert.True(near.LargestChunk <= 48, $"{near}");
        Assert.True(near.HoldMs < 5000, $"{near}");
    }

    [Fact]
    public void AModelClearlyBelowRealTime_HoldsUpToTheCapAndNoLonger()
    {
        var outcome = Simulate(70.0, tokens: 335); // 0.57 of real time: no hold can cover it

        Assert.InRange(outcome.HoldMs, 5500, 6200);
    }
}
