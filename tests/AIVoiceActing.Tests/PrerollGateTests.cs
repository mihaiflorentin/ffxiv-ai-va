namespace AIVoiceActing.Tests;

using AIVoiceActing.Domain;
using Xunit;

public sealed class PrerollGateTests
{
    private long now = 1000;

    private PrerollGate Gate(PrerollOptions? options = null, int chars = 98) =>
        new(options ?? PrerollOptions.Default, chars, () => this.now);

    /// <summary>The language model picks <paramref name="count"/> tokens, one every <paramref name="everyMs"/>.</summary>
    private void Tokens(PrerollGate gate, int count, int everyMs)
    {
        for (var i = 0; i < count; i++)
        {
            this.now += everyMs;
            gate.TokenProduced();
        }
    }

    [Fact]
    public void AFastLine_StartsAsSoonAsTheMinimumIsBuffered()
    {
        var gate = this.Gate();
        this.Tokens(gate, 38, 10); // 100 tokens per second, four times real time
        gate.WindowDecoded(400, 1200);

        Assert.False(gate.ShouldRelease(800)); // below the minimum
        Assert.True(gate.ShouldRelease(1200));
        Assert.Equal(380, gate.WaitedMs);
    }

    [Fact]
    public void ASlowLanguageModel_WaitsForMoreThanTheFirstWindow()
    {
        var gate = this.Gate();
        this.Tokens(gate, 38, 50); // 20 tokens per second: 0.8 of real time
        gate.WindowDecoded(500, 1200);

        Assert.False(gate.ShouldRelease(1200));
        Assert.True(gate.RequiredMs > 2000, $"asked for {gate.RequiredMs} ms");

        this.Tokens(gate, 48, 50);
        gate.WindowDecoded(700, 1900);
        Assert.True(gate.ShouldRelease(3100));
    }

    [Fact]
    public void ASlowDecoder_AlsoHoldsTheSoundBack()
    {
        var gate = this.Gate();
        this.Tokens(gate, 38, 10);
        gate.WindowDecoded(1800, 1200); // 1.5 s of work per second of speech

        Assert.False(gate.ShouldRelease(1200));
        Assert.True(gate.RequiredMs > 1200);
    }

    [Fact]
    public void TheCapReleasesWhateverIsBuffered()
    {
        var gate = this.Gate(new PrerollOptions(MaxMs: 3000));
        this.Tokens(gate, 38, 50); // 20 tokens per second
        gate.WindowDecoded(600, 1200);
        Assert.False(gate.ShouldRelease(1200));

        this.now += 3000;

        Assert.True(gate.ShouldRelease(1200));
        Assert.True(gate.WaitedMs >= 3000);
    }

    [Fact]
    public void ACapOfZero_TurnsThePrerollOff()
    {
        var gate = this.Gate(new PrerollOptions(MaxMs: 0));

        Assert.True(gate.Released);
        Assert.True(gate.ShouldRelease(10));
        Assert.Equal(0, gate.WaitedMs);
    }

    [Fact]
    public void WhenTheLanguageModelHasFinished_OnlyTheTailIsNeeded()
    {
        var gate = this.Gate();
        this.Tokens(gate, 60, 40); // slow, but done
        gate.LanguageModelFinished();
        gate.WindowDecoded(300, 1900); // the decoder keeps up with ease

        Assert.False(gate.ShouldRelease(100));
        Assert.True(gate.ShouldRelease(400)); // the minimum and margin no longer apply: tail 300 ms
    }

    [Fact]
    public void ALineThatIsDoneAndFastToDecode_IsNotHeld()
    {
        var gate = this.Gate(chars: 20);
        this.Tokens(gate, 30, 20);
        gate.LanguageModelFinished();
        gate.WindowDecoded(100, 1200);

        Assert.True(gate.ShouldRelease(1200));
    }

    [Fact]
    public void TheTokensExpectedFollowTheTextUntilTheModelIsDone()
    {
        var shortLine = this.Gate(chars: 40);
        var longLine = this.Gate(chars: 400);
        foreach (var gate in new[] { shortLine, longLine })
        {
            this.Tokens(gate, 38, 50); // 20 tokens per second
            gate.WindowDecoded(200, 1200);
            gate.ShouldRelease(2000);
        }

        Assert.True(shortLine.Released);
        Assert.False(longLine.Released);
        Assert.True(longLine.RequiredMs > shortLine.RequiredMs);
    }

    [Fact]
    public void AModelJustUnderRealTime_HoldsMoreThanThePlainDeficit()
    {
        var gate = this.Gate(chars: 200);
        this.Tokens(gate, 38, 45); // 22 tokens per second: 0.89 of real time
        gate.WindowDecoded(400, 1200);

        Assert.False(gate.ShouldRelease(1200));

        // The plain deficit of about 13 s still to make is 1.6 s; the estimate is noisy near real time, so more is asked for.
        Assert.InRange(gate.RequiredMs, 3500, 7000);
    }

    [Fact]
    public void AModelWellAboveRealTime_AddsNoProportionalMargin()
    {
        var gate = this.Gate(chars: 400);
        this.Tokens(gate, 38, 12); // 83 tokens per second
        gate.WindowDecoded(300, 1200);

        gate.ShouldRelease(100);

        Assert.Equal(PrerollOptions.Default.MinMs, gate.RequiredMs);
    }

    [Fact]
    public void ARateThatWasSlowOverTheLine_CountsEvenWhenTheLastTokensWereQuick()
    {
        var steady = this.Gate(chars: 200);
        this.Tokens(steady, 50, 20);
        steady.WindowDecoded(300, 1200);
        steady.ShouldRelease(1200);

        var mixed = this.Gate(chars: 200);
        this.Tokens(mixed, 30, 100); // a slow stretch, 10 tokens per second
        this.Tokens(mixed, 20, 20); // then a quick one
        mixed.WindowDecoded(300, 1200);
        mixed.ShouldRelease(1200);

        Assert.True(mixed.RequiredMs > steady.RequiredMs + 1000, $"mixed {mixed.RequiredMs} ms, steady {steady.RequiredMs} ms");
    }

    [Fact]
    public void ALineLongerThanTheTextSuggested_IsStillExpectedToGoOn()
    {
        var gate = this.Gate(chars: 10); // about 18 tokens expected
        this.Tokens(gate, 60, 50); // the model keeps going at 20 tokens per second
        gate.WindowDecoded(400, 1200);

        gate.ShouldRelease(1200);

        Assert.True(gate.RequiredMs > 1300, $"asked for {gate.RequiredMs} ms");
    }

    [Fact]
    public void ATokenStall_RaisesTheRequirement_EvenBeforeTheNextTokenArrives()
    {
        var gate = this.Gate(chars: 200);
        this.Tokens(gate, 38, 30); // 33 tokens per second
        gate.WindowDecoded(300, 1200);
        gate.ShouldRelease(500);
        var before = gate.RequiredMs;

        this.now += 1500; // the card is busy: nothing for 1.5 s
        gate.ShouldRelease(500);

        Assert.True(gate.RequiredMs > before);
    }

    [Fact]
    public void Released_StaysReleased()
    {
        var gate = this.Gate();
        this.Tokens(gate, 38, 10);
        gate.WindowDecoded(400, 1200);
        Assert.True(gate.ShouldRelease(1200));

        Assert.True(gate.ShouldRelease(0));
    }
}

public sealed class PrerollBufferTests
{
    private long now;

    private (PrerollBuffer Buffer, List<float[]> Out, PrerollGate Gate) Rig(PrerollOptions? options = null)
    {
        var gate = new PrerollGate(options ?? PrerollOptions.Default, 98, () => this.now);
        var output = new List<float[]>();
        return (new PrerollBuffer(gate, 1000, output.Add), output, gate);
    }

    [Fact]
    public void NothingGoesOutWhileTheGateIsClosed_AndEverythingGoesInOrderWhenItOpens()
    {
        var (buffer, output, gate) = this.Rig();
        for (var i = 0; i < 38; i++)
        {
            this.now += 50; // 20 tokens per second
            gate.TokenProduced();
        }

        gate.WindowDecoded(500, 1200);
        buffer.Add([.. Enumerable.Repeat(1f, 1200)]);
        Assert.Empty(output);

        gate.WindowDecoded(700, 1900);
        buffer.Add([.. Enumerable.Repeat(2f, 1900)]);

        Assert.Equal(2, output.Count);
        Assert.Equal(1f, output[0][0]);
        Assert.Equal(2f, output[1][0]);
    }

    [Fact]
    public void AfterTheGateOpens_AudioPassesStraightThrough()
    {
        var (buffer, output, _) = this.Rig(new PrerollOptions(MaxMs: 0));

        buffer.Add([0.5f, 0.5f]);
        buffer.Add([0.25f]);

        Assert.Equal(2, output.Count);
        Assert.Equal(0, buffer.HeldMs);
    }

    [Fact]
    public void ATickAfterTheCap_LetsHeldAudioThroughWithoutANewWindow()
    {
        var (buffer, output, _) = this.Rig(new PrerollOptions(MaxMs: 2000));
        buffer.Add([.. new float[500]]);
        Assert.Empty(output);

        this.now += 2500;
        buffer.Tick();

        Assert.Single(output);
    }

    [Fact]
    public void Flush_ReleasesWhatIsHeldAndRecordsTheWait()
    {
        var (buffer, output, gate) = this.Rig();
        buffer.Add([.. new float[100]]);
        this.now += 700;

        buffer.Flush();

        Assert.Single(output);
        Assert.Equal(700, gate.WaitedMs);
    }
}
