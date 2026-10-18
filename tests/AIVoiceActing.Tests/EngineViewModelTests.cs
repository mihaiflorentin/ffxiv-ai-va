namespace AIVoiceActing.Tests;

using AIVoiceActing.Ports;
using AIVoiceActing.UI.State;
using Xunit;

public sealed class EngineViewModelTests
{
    private static EngineStatus Status(EngineState state, ActiveDevice device, string reason = "", bool retry = false) =>
        new(state, DeviceMode.Auto, device, reason, retry);

    private static EngineView Describe(EngineStatus? status, bool installed = true, bool warming = false) =>
        EngineViewModel.Describe(new EngineViewInput("Chatterbox Turbo", installed, warming, status));

    [Fact]
    public void NotInstalled_SaysSoAndPointsToDownloads()
    {
        var view = Describe(null, installed: false);

        Assert.Equal(Severity.Warning, view.Severity);
        Assert.Equal(EngineViewModel.NotInstalledWord, view.Word);
        Assert.Contains("Downloads", view.Detail);
    }

    [Fact]
    public void InstalledButNotRunning_IsAGoodState()
    {
        var view = Describe(null, installed: true);

        Assert.Equal(Severity.Success, view.Severity);
        Assert.Equal(EngineViewModel.InstalledWord, view.Word);
    }

    [Fact]
    public void StatusNotInstalled_UsesThePortsReasonVerbatim()
    {
        var view = Describe(Status(EngineState.NotInstalled, ActiveDevice.Cpu, "The speech encoder is missing."));

        Assert.Equal(Severity.Warning, view.Severity);
        Assert.Equal("The speech encoder is missing.", view.Detail);
    }

    [Fact]
    public void Loading_ComesFromStateOrFromAWarmUp()
    {
        var byState = Describe(Status(EngineState.Loading, ActiveDevice.Cpu));
        var byWarmUp = Describe(Status(EngineState.Ready, ActiveDevice.Cpu), warming: true);

        Assert.Equal(EngineViewModel.LoadingWord, byState.Word);
        Assert.Equal(Severity.Info, byState.Severity);
        Assert.Equal(EngineViewModel.LoadingWord, byWarmUp.Word);
    }

    [Fact]
    public void LoadingOnTheGraphicsCard_WarnsTheFirstLineIsSlow()
    {
        var view = Describe(Status(EngineState.Loading, new ActiveDevice(DeviceEp.WebGpu, "RTX 4070")));

        Assert.Contains("first line", view.Detail);
    }

    [Fact]
    public void ReadyOnTheGraphicsCard_NamesTheCard()
    {
        var view = Describe(Status(EngineState.Ready, new ActiveDevice(DeviceEp.WebGpu, "NVIDIA RTX 4070")));

        Assert.Equal(Severity.Success, view.Severity);
        Assert.Equal(EngineViewModel.ReadyWord, view.Word);
        Assert.Equal("Running on your graphics card (NVIDIA RTX 4070).", view.Detail);
    }

    [Fact]
    public void ReadyOnTheGraphicsCard_WithoutAName_StillSaysWhere()
    {
        var view = Describe(Status(EngineState.Ready, new ActiveDevice(DeviceEp.DirectML)));

        Assert.Equal("Running on your graphics card.", view.Detail);
    }

    [Fact]
    public void ReadyOnTheProcessor_SaysProcessor()
    {
        var view = Describe(Status(EngineState.Ready, ActiveDevice.Cpu));

        Assert.Equal(Severity.Success, view.Severity);
        Assert.Equal("Running on your processor.", view.Detail);
    }

    [Fact]
    public void FellBack_IsAWarningWithTheReasonVerbatim()
    {
        var reason = "The GPU could not be started, so Chatterbox Turbo runs on the CPU.";
        var view = Describe(Status(EngineState.FellBack, ActiveDevice.Cpu, reason, retry: true));

        Assert.Equal(Severity.Warning, view.Severity);
        Assert.Equal(EngineViewModel.FellBackWord, view.Word);
        Assert.Equal(reason, view.Detail);
    }

    [Fact]
    public void Failed_IsAnErrorWithTheReasonVerbatim()
    {
        var view = Describe(Status(EngineState.Failed, ActiveDevice.Cpu, "Out of memory."));

        Assert.Equal(Severity.Error, view.Severity);
        Assert.Equal(EngineViewModel.FailedWord, view.Word);
        Assert.Equal("Out of memory.", view.Detail);
    }

    [Theory]
    [InlineData(EngineState.FellBack)]
    [InlineData(EngineState.Failed)]
    public void ProblemWithNoReason_StillSaysSomething(EngineState state)
    {
        var view = Describe(Status(state, ActiveDevice.Cpu));

        Assert.False(string.IsNullOrWhiteSpace(view.Detail));
    }

    [Fact]
    public void EveryStateCarriesAWordNotJustAColour()
    {
        foreach (var state in Enum.GetValues<EngineState>())
        {
            var view = Describe(Status(state, ActiveDevice.Cpu));

            Assert.False(string.IsNullOrWhiteSpace(view.Word));
        }
    }

    [Fact]
    public void ReadyOnTheProcessor_KeepsThePortsReasonWhenTheGraphicsCardWasAskedFor()
    {
        var status = new EngineStatus(EngineState.Ready, DeviceMode.Gpu, ActiveDevice.Cpu, "No graphics card was found.", false);

        var view = Describe(status);

        Assert.Equal(EngineViewModel.ReadyWord, view.Word);
        Assert.Equal("Running on your processor. No graphics card was found.", view.Detail);
    }

    [Fact]
    public void ReadyOnTheProcessor_ByChoice_AddsNothing()
    {
        var status = new EngineStatus(EngineState.Ready, DeviceMode.Cpu, ActiveDevice.Cpu, "Running on the CPU, as you chose.", false);

        Assert.Equal("Running on your processor.", Describe(status).Detail);
    }

    [Fact]
    public void ReadyOnTheProcessor_DoesNotRepeatAReasonThatAlreadySaysSo()
    {
        var reason = "Running on your processor because Precision is set to full precision.";
        var status = new EngineStatus(EngineState.Ready, DeviceMode.Gpu, ActiveDevice.Cpu, reason, false);

        Assert.Equal(reason, Describe(status).Detail);
    }

    [Fact]
    public void ReadyOnTheGraphicsCard_ExplainsAnIgnoredFullSizePin()
    {
        var status = new EngineStatus(
            EngineState.Ready, DeviceMode.Gpu, new ActiveDevice(DeviceEp.WebGpu, "RTX 4070"), AIVoiceActing.Domain.PrecisionNotes.FullSizeIgnoredOnGpu, false);

        Assert.Equal(
            "Running on your graphics card (RTX 4070). " + AIVoiceActing.Domain.PrecisionNotes.FullSizeIgnoredOnGpu,
            Describe(status).Detail);
    }

    [Fact]
    public void ReadyOnTheProcessorForNow_IsNotAProblem_AndSaysWhyInThePortsWords()
    {
        const string reason = "Running on your processor right now: the game is keeping the graphics card busy, so the processor is faster. It switches back by itself.";
        var view = Describe(new EngineStatus(EngineState.Ready, DeviceMode.Auto, ActiveDevice.Cpu, reason, false, OnProcessorForNow: true));

        Assert.Equal(Severity.Info, view.Severity);
        Assert.Equal(EngineViewModel.ReadyWord, view.Word);
        Assert.Equal(reason, view.Detail);
        Assert.False(Labels.ContainsJargon(view.Detail), view.Detail);
    }
}
