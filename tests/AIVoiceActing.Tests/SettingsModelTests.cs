namespace AIVoiceActing.Tests;

using AIVoiceActing.UI.State;
using Xunit;

public sealed class SettingsModelTests
{
    [Fact]
    public void Reset_PutsTheSettingsOnThisTabBackToTheirDefaults()
    {
        var config = new Configuration();
        config.Engine.Primary = "chatterbox-turbo";
        config.Engine.Expressive.Enabled = true;
        config.Engine.PerEngine["remote"] = new EngineOptions { Extra = { ["url"] = "http://10.0.0.5:8880" } };
        config.Device.Mode = DeviceSettings.ModeCpu;
        config.Device.GpuDisabledByCrashGuard = true;
        config.Device.CpuImpact = "high";
        config.DirectorEnabled = true;
        config.LevelVoiceLoudness = false;
        config.StaleLineSeconds = 0;
        config.KeepTempoWhenPitched = false;
        config.StreamSentences = false;
        config.ShowNowSpeaking = true;
        config.RemoveStutter = false;
        config.UseKeybind = true;
        config.ModifierKey = 18;
        config.MajorKey = 65;
        config.ShowAdvanced = true;
        config.ReduceMotion = true;

        SettingsReset.Apply(config);

        var defaults = new Configuration();
        Assert.Equal(defaults.Engine.Primary, config.Engine.Primary);
        Assert.False(config.Engine.Expressive.Enabled);
        Assert.Empty(config.Engine.PerEngine);
        Assert.Equal(DeviceSettings.ModeAuto, config.Device.Mode);
        Assert.False(config.Device.GpuDisabledByCrashGuard);
        Assert.Equal(defaults.Device.CpuImpact, config.Device.CpuImpact);
        Assert.Equal(defaults.DirectorEnabled, config.DirectorEnabled);
        Assert.Equal(defaults.LevelVoiceLoudness, config.LevelVoiceLoudness);
        Assert.Equal(defaults.StaleLineSeconds, config.StaleLineSeconds);
        Assert.Equal(defaults.KeepTempoWhenPitched, config.KeepTempoWhenPitched);
        Assert.Equal(defaults.StreamSentences, config.StreamSentences);
        Assert.Equal(defaults.ShowNowSpeaking, config.ShowNowSpeaking);
        Assert.Equal(defaults.RemoveStutter, config.RemoveStutter);
        Assert.Equal(defaults.UseKeybind, config.UseKeybind);
        Assert.Equal(defaults.ModifierKey, config.ModifierKey);
        Assert.Equal(defaults.MajorKey, config.MajorKey);
        Assert.False(config.ShowAdvanced);
        Assert.False(config.ReduceMotion);
    }

    [Fact]
    public void Reset_LeavesEverythingElseAlone()
    {
        var config = new Configuration
        {
            Enabled = false,
            GlobalVolume = 0.4f,
            ReadFromBattleTalkAddon = false,
            UseRaceVoicePresets = false,
            OnboardingCompletedVersion = 2,
            OnboardingStep = 4,
            LastPage = "Voices",
        };
        config.Voices.DisabledPacks.Add("accents-extra");
        config.Lexicons.Add("lexicon.json");

        SettingsReset.Apply(config);

        Assert.False(config.Enabled);
        Assert.Equal(0.4f, config.GlobalVolume);
        Assert.False(config.ReadFromBattleTalkAddon);
        Assert.False(config.UseRaceVoicePresets);
        Assert.Equal(2, config.OnboardingCompletedVersion);
        Assert.Equal(4, config.OnboardingStep);
        Assert.Equal("Voices", config.LastPage);
        Assert.Equal(["accents-extra"], config.Voices.DisabledPacks);
        Assert.Equal(["lexicon.json"], config.Lexicons);
    }

    [Fact]
    public void Reset_ReturnsTheVoiceQualityToFast()
    {
        var config = new Configuration();
        QualityTierModel.Apply(config.Engine, QualityTier.MostExpressive, hasUsableGpu: true);

        SettingsReset.Apply(config);

        Assert.Equal(QualityTier.Fast, QualityTierModel.FromConfig(config.Engine, new AIVoiceActing.Infrastructure.Engines.EngineCatalog()));
        Assert.Contains("Fast", SettingsReset.ConfirmText);
    }

    [Theory]
    [InlineData("advanced", true)]
    [InlineData("casting-presets", true)]
    [InlineData("engine", true)]
    [InlineData("test", true)]
    [InlineData("diagnostics", true)]
    [InlineData("experimental", true)]
    [InlineData("quality", false)]
    [InlineData("processor", false)]
    [InlineData("downloads", false)]
    [InlineData("keybinds", false)]
    [InlineData(null, false)]
    public void Anchors_KnowWhichOnesLiveInsideAdvanced(string? anchor, bool inside)
    {
        Assert.Equal(inside, SettingsAnchors.InsideAdvanced(anchor));
    }

    [Fact]
    public void TheVoicesTabLink_StillLandsOnThePresets()
    {
        Assert.Equal("casting-presets", SettingsAnchors.Presets);
    }

    [Fact]
    public void RerunSignal_FiresOncePerRequest()
    {
        var signal = new SetupRerunSignal();

        Assert.False(signal.Take());
        signal.Request();
        signal.Request();
        Assert.True(signal.Take());
        Assert.False(signal.Take());
    }

    [Fact]
    public void SetupFlow_Restart_ShowsTheChoiceAgainEvenWhenEverythingWorks()
    {
        var steps = new List<SetupStep>();
        var flow = new SetupFlowModel(0, completed: true, steps.Add);
        Assert.False(flow.Visible(engineReady: true));

        flow.Restart();

        Assert.Equal(SetupStep.Recommend, flow.Step);
        Assert.True(flow.Visible(engineReady: true));
        Assert.Equal([SetupStep.Recommend], steps);
    }

    [Fact]
    public void SetupFlow_Restart_DropsAnInterruptedRun()
    {
        var flow = new SetupFlowModel((int)SetupStep.Download, completed: false);
        Assert.True(flow.AwaitingResume);

        flow.Restart();

        Assert.False(flow.AwaitingResume);
        Assert.Equal(SetupStep.Recommend, flow.Step);
        Assert.False(flow.TakeStartDownload());
        Assert.Null(flow.Failure);
    }

    [Fact]
    public void Labels_UsePlainWordsForTheOldNames()
    {
        Assert.Equal("Processor", Labels.Plain("Execution provider"));
        Assert.Equal("Background load", Labels.Plain("CPU impact"));
        Assert.Equal("Duplicate", Labels.Plain("Fork"));
    }
}
