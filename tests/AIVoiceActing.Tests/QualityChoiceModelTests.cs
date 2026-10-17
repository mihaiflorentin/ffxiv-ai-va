namespace AIVoiceActing.Tests;

using AIVoiceActing.Domain;
using AIVoiceActing.UI.State;
using Xunit;

public sealed class QualityChoiceModelTests
{
    private static readonly EngineDownload Turbo = new("chatterbox-turbo", "Chatterbox Turbo", 3_000, 3_000);

    private static SetupPlan Installed(QualityTier tier) => new(tier, [], 0, null);

    private static SetupPlan NeedsTurbo(QualityTier tier) => new(tier, [Turbo], Turbo.MissingBytes, null);

    private static SetupPlan Blocked(QualityTier tier, string why) => new(tier, [], 0, why);

    [Fact]
    public void ATierOnDisk_AppliesAtOnce()
    {
        var model = new QualityChoiceModel();

        Assert.Equal(ChoiceOutcome.Applied, model.Choose(QualityTier.Fast, Installed(QualityTier.Fast)));
        Assert.Null(model.Pending);
    }

    [Fact]
    public void ATierThatNeedsADownload_WaitsInsteadOfSwitchingTheVoices()
    {
        var model = new QualityChoiceModel();

        Assert.Equal(ChoiceOutcome.NeedsDownload, model.Choose(QualityTier.Balanced, NeedsTurbo(QualityTier.Balanced)));
        Assert.Equal(QualityTier.Balanced, model.Pending);
    }

    [Fact]
    public void ABlockedTier_KeepsNothingPendingAndSaysWhy()
    {
        var model = new QualityChoiceModel();
        model.Choose(QualityTier.Balanced, NeedsTurbo(QualityTier.Balanced));

        var outcome = model.Choose(QualityTier.MostExpressive, Blocked(QualityTier.MostExpressive, "No graphics card."));

        Assert.Equal(ChoiceOutcome.Blocked, outcome);
        Assert.Null(model.Pending);
        Assert.Equal("No graphics card.", model.Notice);
    }

    [Fact]
    public void WhileTheDownloadRuns_NothingIsApplied()
    {
        var model = new QualityChoiceModel();
        model.Choose(QualityTier.Balanced, NeedsTurbo(QualityTier.Balanced));

        Assert.Null(model.TakeReady(downloadActive: true, installed: false));
        Assert.Equal(QualityTier.Balanced, model.Pending);
    }

    [Fact]
    public void WhenTheFilesArrive_TheWaitingTierIsHandedBackOnce()
    {
        var model = new QualityChoiceModel();
        model.Choose(QualityTier.Balanced, NeedsTurbo(QualityTier.Balanced));
        model.TakeReady(downloadActive: true, installed: false);

        Assert.Equal(QualityTier.Balanced, model.TakeReady(downloadActive: false, installed: true));
        Assert.Null(model.Pending);
        Assert.Null(model.TakeReady(downloadActive: false, installed: true));
    }

    [Fact]
    public void FilesThatArriveByAnotherRoute_AlsoApplyTheWaitingTier()
    {
        var model = new QualityChoiceModel();
        model.Choose(QualityTier.Balanced, NeedsTurbo(QualityTier.Balanced));

        Assert.Equal(QualityTier.Balanced, model.TakeReady(downloadActive: false, installed: true));
    }

    [Fact]
    public void ADownloadThatStopsEarly_KeepsTheTierPendingWithANotice()
    {
        var model = new QualityChoiceModel();
        model.Choose(QualityTier.Balanced, NeedsTurbo(QualityTier.Balanced));
        model.TakeReady(downloadActive: true, installed: false);

        Assert.Null(model.TakeReady(downloadActive: false, installed: false));

        Assert.Equal(QualityTier.Balanced, model.Pending);
        Assert.Contains("stopped", model.Notice);
        Assert.Contains("unchanged", model.Notice);
    }

    [Fact]
    public void ARetriedDownload_ClearsTheNoticeAndFinishesTheSwitch()
    {
        var model = new QualityChoiceModel();
        model.Choose(QualityTier.Balanced, NeedsTurbo(QualityTier.Balanced));
        model.TakeReady(true, false);
        model.TakeReady(false, false);

        model.TakeReady(true, false);

        Assert.Null(model.Notice);
        Assert.Equal(QualityTier.Balanced, model.TakeReady(false, true));
    }

    [Fact]
    public void Cancel_ForgetsTheTier()
    {
        var model = new QualityChoiceModel();
        model.Choose(QualityTier.Balanced, NeedsTurbo(QualityTier.Balanced));

        model.Cancel();

        Assert.Null(model.Pending);
        Assert.Null(model.TakeReady(false, true));
    }

    [Fact]
    public void WithNothingPending_TakeReadyDoesNothing()
    {
        Assert.Null(new QualityChoiceModel().TakeReady(false, true));
    }
}
