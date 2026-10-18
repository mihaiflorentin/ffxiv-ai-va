namespace AIVoiceActing.Tests;

using AIVoiceActing.Domain;
using AIVoiceActing.Ports;
using Xunit;

/// <summary>The live PCM buffer between the synthesizer and the speakers, and its streaming polish.</summary>
public sealed class SpeechAudioStreamTests
{
    [Fact]
    public void SamplesComeOut_InOrder_AcrossAppends()
    {
        var stream = new SpeechAudioStream(24000);
        stream.Append([1f, 2f, 3f]);
        stream.Append([4f, 5f]);

        var buffer = new float[4];
        Assert.Equal(4, stream.Read(buffer));
        Assert.Equal([1f, 2f, 3f, 4f], buffer);
        Assert.Equal(1, stream.Read(buffer));
        Assert.Equal(5f, buffer[0]);
        Assert.Equal(0, stream.Read(buffer));
    }

    [Fact]
    public void ReadNeverBlocks_AndAnUnfinishedStreamIsNotFinished()
    {
        var stream = new SpeechAudioStream(24000);

        Assert.Equal(0, stream.Read(new float[10]));
        Assert.False(stream.IsFinished);
        Assert.False(stream.WaitForData(20));
    }

    [Fact]
    public void ACompletedStream_IsFinishedOnlyOnceDrained()
    {
        var stream = new SpeechAudioStream(24000);
        stream.Append([1f, 2f]);
        stream.Complete();

        Assert.True(stream.IsCompleted);
        Assert.False(stream.IsFinished);
        stream.Read(new float[2]);
        Assert.True(stream.IsFinished);
        Assert.True(stream.WaitForData(0)); // completion wakes waiters even with nothing to read
    }

    [Fact]
    public void Cancel_DropsWhatIsPending_SignalsTheProducer_AndRefusesMoreSamples()
    {
        var stream = new SpeechAudioStream(24000);
        stream.Append([1f, 2f, 3f]);

        stream.Cancel();

        Assert.True(stream.IsCancelled);
        Assert.True(stream.Cancelled.IsCancellationRequested);
        Assert.True(stream.IsFinished);
        Assert.Equal(0, stream.Buffered);
        Assert.False(stream.Append([4f]));
        Assert.Equal(0, stream.Read(new float[4]));
    }

    [Fact]
    public void Append_AfterComplete_IsRefused()
    {
        var stream = new SpeechAudioStream(24000);
        stream.Complete();

        Assert.False(stream.Append([1f]));
    }

    [Fact]
    public void WaitForData_WakesWhenTheProducerAppends()
    {
        var stream = new SpeechAudioStream(24000);
        var waiter = Task.Run(() => stream.WaitForData(5000));

        Thread.Sleep(50);
        stream.Append([0.5f]);

        Assert.True(waiter.Wait(2000));
        Assert.True(waiter.Result);
    }

    [Fact]
    public void Peek_DoesNotConsume()
    {
        var stream = new SpeechAudioStream(24000);
        stream.Append([1f, 2f, 3f]);

        var peeked = new float[2];
        Assert.Equal(2, stream.Peek(peeked));

        Assert.Equal([1f, 2f], peeked);
        Assert.Equal(3, stream.Buffered);
    }

    [Fact]
    public void ABufferThatGrowsPastItsStart_KeepsEverySample()
    {
        var stream = new SpeechAudioStream(24000);
        var total = 200_000;
        var written = 0;
        var read = new List<float>();
        var chunk = new float[3000];
        while (written < total)
        {
            var n = Math.Min(7000, total - written);
            stream.Append(Enumerable.Range(written, n).Select(i => (float)i).ToArray());
            written += n;
            var got = stream.Read(chunk);
            read.AddRange(chunk.AsSpan(0, got).ToArray());
        }

        stream.Complete();
        read.AddRange(stream.ReadAllBlocking());

        Assert.Equal(total, read.Count);
        Assert.Equal(Enumerable.Range(0, total).Select(i => (float)i), read);
        Assert.Equal(total, stream.TotalAppended);
    }

    [Fact]
    public void ReadAllBlocking_WaitsForTheProducerToFinish()
    {
        var stream = new SpeechAudioStream(24000);
        var reader = Task.Run(() => stream.ReadAllBlocking());

        stream.Append([1f, 2f]);
        Thread.Sleep(30);
        stream.Append([3f]);
        stream.Complete();

        Assert.True(reader.Wait(2000));
        Assert.Equal([1f, 2f, 3f], reader.Result);
    }

    // ---- PolishedPcmSource ----------------------------------------------------------------------------

    private static float[] Drain(IPcmSource source)
    {
        var all = new List<float>();
        var buffer = new float[257];
        while (!source.IsFinished)
        {
            var n = source.Read(buffer);
            if (n == 0)
            {
                break;
            }

            all.AddRange(buffer.AsSpan(0, n).ToArray());
        }

        return [.. all];
    }

    [Fact]
    public void Polish_MatchesTheWholeLinePolish_SampleForSample()
    {
        // The streaming shaping (gain, soft limit, 6 ms fades) must equal AudioPolish.Finish on the same line.
        var random = new Random(3);
        var line = Enumerable.Range(0, 24000).Select(_ => (float)((random.NextDouble() * 1.6) - 0.8)).ToArray();
        var stream = new SpeechAudioStream(24000);
        var source = new PolishedPcmSource(stream, gain: 1.3f, levelLoudness: false);
        for (var offset = 0; offset < line.Length; offset += 5000)
        {
            stream.Append(line.AsSpan(offset, Math.Min(5000, line.Length - offset)));
        }

        stream.Complete();

        var streamed = Drain(source);

        Assert.Equal(AudioPolish.Finish(line, 24000, 1.3f, levelLoudness: false), streamed);
    }

    [Fact]
    public void AfterRunningDryMidLine_ItWaitsForACushion_InsteadOfPlayingScraps()
    {
        var stream = new SpeechAudioStream(24000);
        var source = new PolishedPcmSource(stream, 1f, false);
        stream.Append(Enumerable.Repeat(0.5f, 5000).ToArray());
        var buffer = new float[512];
        while (source.Read(buffer) > 0)
        {
        }

        // A word's worth arrives; it is less than the cushion, so nothing plays yet.
        stream.Append(Enumerable.Repeat(0.5f, 4000).ToArray());
        Assert.Equal(0, source.Read(buffer));

        // More arrives until the cushion (800 ms) is there: now it plays, with a short ramp up.
        stream.Append(Enumerable.Repeat(0.5f, 20000).ToArray());
        var read = source.Read(buffer);

        Assert.True(read > 0);
        Assert.True(buffer[0] < 0.1f);
        var later = new float[512];
        source.Read(later); // the ramp (25 ms) is over by the end of this read
        Assert.Equal(0.5f, later[^1], 2);
    }

    [Fact]
    public void ARebufferingLine_StillEndsWhenTheProducerCompletes()
    {
        var stream = new SpeechAudioStream(24000);
        var source = new PolishedPcmSource(stream, 1f, false);
        stream.Append(Enumerable.Repeat(0.5f, 5000).ToArray());
        var buffer = new float[512];
        while (source.Read(buffer) > 0)
        {
        }

        stream.Append(Enumerable.Repeat(0.5f, 2000).ToArray());
        Assert.Equal(0, source.Read(buffer));
        stream.Complete();

        var rest = Drain(source);

        Assert.Equal(2000 + (24000 * AudioPolish.FadeMs / 1000), rest.Length); // the held-back fade plus the new samples
        Assert.True(source.IsFinished);
    }

    [Fact]
    public void WhenEverythingMadeIsPlayed_TheSoundRampsDownInsteadOfStoppingDead()
    {
        var stream = new SpeechAudioStream(24000);
        var source = new PolishedPcmSource(stream, 1f, false);
        stream.Append(Enumerable.Repeat(0.5f, 5000).ToArray());
        var heard = new List<float>();
        var buffer = new float[512];
        int n;
        while ((n = source.Read(buffer)) > 0)
        {
            heard.AddRange(buffer.AsSpan(0, n).ToArray());
        }

        var ramp = 24000 * PolishedPcmSource.StallFadeMs / 1000;
        Assert.Equal(0.5f, heard[^(ramp + 20)], 3); // full level right before the ramp
        Assert.True(heard[^1] < 0.01f, $"ended at {heard[^1]}");
        for (var i = heard.Count - ramp; i < heard.Count - 1; i++)
        {
            Assert.True(heard[i + 1] <= heard[i] + 1e-6f, "the ramp only goes down");
        }
    }

    [Fact]
    public void WhileTheReaderIsAheadOfTheEnd_NothingIsFaded()
    {
        var stream = new SpeechAudioStream(24000);
        var source = new PolishedPcmSource(stream, 1f, false);
        stream.Append(Enumerable.Repeat(0.5f, 20000).ToArray());
        var buffer = new float[512];
        source.Read(buffer); // the line's own start fade
        source.Read(buffer);

        Assert.All(buffer, v => Assert.Equal(0.5f, v, 3));
    }

    [Fact]
    public void IfTheProducerFinishesWhileTheSoundIsStopped_TheRestStillComesInOnARamp()
    {
        var stream = new SpeechAudioStream(24000);
        var source = new PolishedPcmSource(stream, 1f, false);
        stream.Append(Enumerable.Repeat(0.5f, 5000).ToArray());
        var buffer = new float[512];
        while (source.Read(buffer) > 0)
        {
        }

        stream.Append(Enumerable.Repeat(0.5f, 2000).ToArray());
        Assert.Equal(0, source.Read(buffer));
        stream.Complete();

        var rest = Drain(source);

        Assert.True(rest[0] < 0.05f, $"started at {rest[0]}");
    }

    [Fact]
    public void TheDryWait_IsBounded_SoASlowLineStillCarriesOn()
    {
        var stream = new SpeechAudioStream(24000);
        var source = new PolishedPcmSource(stream, 1f, false);
        stream.Append(Enumerable.Repeat(0.5f, 5000).ToArray());
        var buffer = new float[2400];
        while (source.Read(buffer) > 0)
        {
        }

        stream.Append(Enumerable.Repeat(0.5f, 4000).ToArray());
        var silent = 0;
        while (source.Read(buffer) == 0 && silent < 100)
        {
            silent++;
        }

        Assert.InRange(silent, 1, 60); // about 3 s of device reads at 100 ms each, never forever
    }

    [Fact]
    public void Polish_HoldsBackTheLastFade_UntilTheLineIsComplete()
    {
        var stream = new SpeechAudioStream(24000);
        var source = new PolishedPcmSource(stream, 1f, false);
        stream.Append(Enumerable.Repeat(0.5f, 1000).ToArray());
        var fade = 24000 * AudioPolish.FadeMs / 1000;

        var read = source.Read(new float[5000]);

        Assert.Equal(1000 - fade, read); // the closing fade needs the real end of the line
        Assert.False(source.IsFinished);
        stream.Complete();
        var rest = new float[5000];
        var tail = source.Read(rest);
        Assert.Equal(fade, tail);
        Assert.True(rest[tail - 1] < 0.01f); // faded to silence at the very end
        Assert.True(source.IsFinished);
    }

    [Fact]
    public void Polish_LevelsTheLineByItsFirstHalfSecond_AndKeepsThatGain()
    {
        var quiet = Enumerable.Repeat(0.02f, 24000).ToArray(); // rms 0.02: about five times too quiet
        var stream = new SpeechAudioStream(24000);
        var source = new PolishedPcmSource(stream, 1f, levelLoudness: true);
        stream.Append(quiet);
        stream.Complete();

        var polished = Drain(source);

        var middle = polished[8000];
        Assert.InRange(middle, 0.02f * 3.9f, 0.02f * 4.1f); // MaxLevelGain caps the boost at 4x
    }

    [Fact]
    public void Polish_OfASilentLine_DoesNotBlowUp()
    {
        var stream = new SpeechAudioStream(24000);
        var source = new PolishedPcmSource(stream, 1f, levelLoudness: true);
        stream.Append(new float[3000]);
        stream.Complete();

        Assert.All(Drain(source), v => Assert.Equal(0f, v));
    }

    [Fact]
    public void ACancelledStream_ReadsNothing_AndIsFinished()
    {
        var stream = new SpeechAudioStream(24000);
        var source = new PolishedPcmSource(stream, 1f, false);
        stream.Append(new float[5000]);
        stream.Cancel();

        Assert.Equal(0, source.Read(new float[100]));
        Assert.True(source.IsFinished);
    }
}
