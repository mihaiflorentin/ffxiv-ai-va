namespace AIVoiceActing.Tests;

using AIVoiceActing.Domain;
using Xunit;

public sealed class LmDeviceChooserTests
{
    private long now = 1_000_000;

    private LmDeviceChooser Chooser(LmDeviceOptions? options = null) => new(options, () => this.now);

    private static void Line(LmDeviceChooser chooser, LmDevice device, double ms) => chooser.Record(device, ms, 100);

    [Fact]
    public void ItStartsOnTheGraphicsCard_AndNeverAsksForTheProcessorWhileTheCardKeepsUp()
    {
        var chooser = this.Chooser();

        Assert.Equal(LmDevice.Gpu, chooser.Choose(80, cpuReady: false));
        Line(chooser, LmDevice.Gpu, 8);

        Assert.False(chooser.WantsCpu);
        Assert.Equal(LmDevice.Gpu, chooser.Choose(80, cpuReady: false));
        Assert.Equal(LmDevice.Gpu, chooser.Preferred);
    }

    [Fact]
    public void ASlowCard_AsksForAProcessorSession_AndKeepsTheLinesUntilItExists()
    {
        var chooser = this.Chooser();
        chooser.Choose(80, cpuReady: false);
        Line(chooser, LmDevice.Gpu, 45);

        Assert.True(chooser.WantsCpu);
        Assert.Equal(LmDevice.Gpu, chooser.Choose(80, cpuReady: false));
    }

    [Fact]
    public void WithAProcessorSession_ASlowCardMovesTheNextLineThere_AndItStaysWhenItIsFaster()
    {
        var chooser = this.Chooser();
        chooser.Choose(80, cpuReady: true);
        Line(chooser, LmDevice.Gpu, 45);

        Assert.Equal(LmDevice.Cpu, chooser.Choose(80, cpuReady: true)); // never measured: tried once
        Line(chooser, LmDevice.Cpu, 12);

        Assert.Equal(LmDevice.Cpu, chooser.Choose(80, cpuReady: true));
        Assert.Equal(LmDevice.Cpu, chooser.Preferred);
    }

    [Fact]
    public void ASlowCard_IsKept_WhenTheProcessorIsNoFaster()
    {
        var chooser = this.Chooser();
        chooser.Choose(80, cpuReady: true);
        Line(chooser, LmDevice.Gpu, 45);
        chooser.Choose(80, cpuReady: true);
        Line(chooser, LmDevice.Cpu, 44); // the box is loaded as well

        Assert.Equal(LmDevice.Gpu, chooser.Choose(80, cpuReady: true));
    }

    [Fact]
    public void ACardThatKeepsUpWithRoomToSpare_IsNotLeftForAFasterProcessor()
    {
        var chooser = this.Chooser();
        chooser.Choose(80, cpuReady: true);
        Line(chooser, LmDevice.Gpu, 28);
        Line(chooser, LmDevice.Cpu, 10);

        Assert.Equal(LmDevice.Gpu, chooser.Choose(80, cpuReady: true));
    }

    [Fact]
    public void ACardJustOverTheLimit_IsLeft_OnceTheProcessorIsClearlyFaster()
    {
        var chooser = this.Chooser();
        chooser.Choose(80, cpuReady: true);
        Line(chooser, LmDevice.Gpu, 38);
        Line(chooser, LmDevice.Cpu, 12);

        Assert.Equal(LmDevice.Cpu, chooser.Choose(80, cpuReady: true));
    }

    [Fact]
    public void TheBandBetweenTheTwoRatios_KeepsTheChoiceWhereItIs()
    {
        var onCard = this.Chooser();
        onCard.Choose(80, cpuReady: true);
        Line(onCard, LmDevice.Cpu, 30);
        Line(onCard, LmDevice.Gpu, 36); // 1.2 times the processor: not enough to leave
        Assert.Equal(LmDevice.Gpu, onCard.Choose(80, cpuReady: true));

        var onCpu = this.Chooser();
        Line(onCpu, LmDevice.Gpu, 45);
        Line(onCpu, LmDevice.Cpu, 12);
        Assert.Equal(LmDevice.Cpu, onCpu.Choose(80, cpuReady: true));
        this.now += 6 * 60 * 1000;
        Assert.Equal(LmDevice.Gpu, onCpu.Choose(80, cpuReady: true)); // a try of the card
        Line(onCpu, LmDevice.Gpu, 13); // says 1.08 times the processor: back to the card
        Assert.Equal(LmDevice.Gpu, onCpu.Choose(80, cpuReady: true));
        Assert.Equal(LmDevice.Gpu, onCpu.Preferred);
    }

    [Fact]
    public void OnTheProcessor_TheCardIsTriedAgainAfterSomeLines_ByAShortLine()
    {
        var chooser = this.Chooser();
        Line(chooser, LmDevice.Gpu, 45);
        Line(chooser, LmDevice.Cpu, 12);

        for (var i = 0; i < 6; i++)
        {
            Assert.Equal(LmDevice.Cpu, chooser.Choose(60, cpuReady: true));
        }

        Assert.Equal(LmDevice.Gpu, chooser.Choose(60, cpuReady: true)); // the try
        Assert.Equal(LmDevice.Cpu, chooser.Preferred); // but the choice stays until it is measured
    }

    [Fact]
    public void ATry_WaitsForAShortLine_UnlessItIsLongOverdue()
    {
        var chooser = this.Chooser();
        Line(chooser, LmDevice.Gpu, 45);
        Line(chooser, LmDevice.Cpu, 12);
        for (var i = 0; i < 6; i++)
        {
            chooser.Choose(60, cpuReady: true);
        }

        Assert.Equal(LmDevice.Cpu, chooser.Choose(300, cpuReady: true)); // long: not now
        for (var i = 0; i < 5; i++)
        {
            Assert.Equal(LmDevice.Cpu, chooser.Choose(300, cpuReady: true));
        }

        Assert.Equal(LmDevice.Gpu, chooser.Choose(300, cpuReady: true)); // twelve lines: whatever the length
    }

    [Fact]
    public void ATry_AlsoComesAfterSomeTime()
    {
        var chooser = this.Chooser();
        Line(chooser, LmDevice.Gpu, 45);
        Line(chooser, LmDevice.Cpu, 12);
        Assert.Equal(LmDevice.Cpu, chooser.Choose(60, cpuReady: true));

        this.now += 5 * 60 * 1000;

        Assert.Equal(LmDevice.Gpu, chooser.Choose(60, cpuReady: true));
    }

    [Fact]
    public void AFastTry_SwitchesBackAtOnce_ASlowOneDoesNot()
    {
        var chooser = this.Chooser();
        Line(chooser, LmDevice.Gpu, 45);
        Line(chooser, LmDevice.Cpu, 12);
        this.now += 6 * 60 * 1000;

        Assert.Equal(LmDevice.Gpu, chooser.Choose(60, cpuReady: true));
        Line(chooser, LmDevice.Gpu, 44); // still busy
        Assert.Equal(LmDevice.Cpu, chooser.Choose(60, cpuReady: true));
        Assert.Equal(44, chooser.GpuMsPerToken, 3);

        this.now += 6 * 60 * 1000;
        Assert.Equal(LmDevice.Gpu, chooser.Choose(60, cpuReady: true));
        Line(chooser, LmDevice.Gpu, 9); // the game stopped loading the card
        Assert.Equal(9, chooser.GpuMsPerToken, 3);
        Assert.Equal(LmDevice.Gpu, chooser.Choose(60, cpuReady: true));
        Assert.Equal(LmDevice.Gpu, chooser.Preferred);
    }

    [Fact]
    public void ALineWithTooFewSteps_SaysNothing()
    {
        var chooser = this.Chooser();
        chooser.Record(LmDevice.Gpu, 90, steps: 3);

        Assert.Equal(0, chooser.GpuMsPerToken);
        Assert.False(chooser.WantsCpu);
    }

    [Fact]
    public void TheAverage_FollowsTheLatestLinesWithoutJumping()
    {
        var chooser = this.Chooser();
        Line(chooser, LmDevice.Cpu, 10);
        Line(chooser, LmDevice.Cpu, 20);

        Assert.Equal(14, chooser.CpuMsPerToken, 3); // 0.4 * 20 + 0.6 * 10
    }

    [Fact]
    public void WhenTheProcessorSessionIsGone_EveryLineIsTheCards()
    {
        var chooser = this.Chooser();
        Line(chooser, LmDevice.Gpu, 45);
        Line(chooser, LmDevice.Cpu, 12);
        Assert.Equal(LmDevice.Cpu, chooser.Choose(60, cpuReady: true));

        Assert.Equal(LmDevice.Gpu, chooser.Choose(60, cpuReady: false));
        Assert.Equal(LmDevice.Gpu, chooser.Preferred);
    }

    [Fact]
    public void AProbe_IsCountedWhenIssued_SoACutShortLineDoesNotCauseAnotherOne()
    {
        var chooser = this.Chooser();
        chooser.Choose(80, cpuReady: true);
        Line(chooser, LmDevice.Gpu, 45);
        chooser.Choose(80, cpuReady: true);
        Line(chooser, LmDevice.Cpu, 12);

        var probe = false;
        for (var i = 0; i < 6 && !probe; i++)
        {
            probe = chooser.Choose(40, cpuReady: true) == LmDevice.Gpu; // the probe's line is cancelled: nothing is recorded
        }

        Assert.True(probe);
        var probes = 0;
        for (var i = 0; i < 4; i++)
        {
            probes += chooser.Choose(40, cpuReady: true) == LmDevice.Gpu ? 1 : 0;
        }

        Assert.Equal(0, probes);
    }

    [Fact]
    public void AfterDisableCpu_TheCardKeepsEveryLine_AndNoProcessorSessionIsWanted()
    {
        var chooser = this.Chooser();
        chooser.Choose(80, cpuReady: true);
        Line(chooser, LmDevice.Gpu, 45);
        Assert.True(chooser.WantsCpu);
        Assert.Equal(LmDevice.Cpu, chooser.Choose(80, cpuReady: true));

        chooser.DisableCpu();

        Assert.False(chooser.WantsCpu);
        Assert.Equal(LmDevice.Gpu, chooser.Choose(80, cpuReady: true));
        Assert.Equal(LmDevice.Gpu, chooser.Preferred);
    }
}
