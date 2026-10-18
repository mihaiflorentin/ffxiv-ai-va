namespace AIVoiceActing.Tests;

using AIVoiceActing.Domain;
using Xunit;

public sealed class PrerollGateTests
{
    private long now = 1000;

    private PrerollGate Gate(PrerollOptions? options = null, int chars = 231) =>
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
            gate.ShouldRelease(1200);
        }

        Assert.True(shortLine.Released);
        Assert.False(longLine.Released);
        Assert.True(longLine.RequiredMs > shortLine.RequiredMs);
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
        var gate = new PrerollGate(options ?? PrerollOptions.Default, 231, () => this.now);
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
