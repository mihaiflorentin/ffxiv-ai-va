namespace AIVoiceActing.Tests;

using AIVoiceActing.Container;
using Xunit;

/// <summary>
/// The pure command core: each command dispatches the right action through the injected
/// delegates (TextToTalk MainCommandModule parity), volume math clamps 0–200 with relative
/// +N/-N, and unknown input yields usage. Message strings are the contract.
/// </summary>
public sealed class CommandRouterTests
{
    private sealed class Recorder
    {
        public bool EnabledValue = true;
        public bool Cancelled;
        public int PresetIdValue;
        public List<int> SwitchedTo { get; } = [];
        public float VolumeValue = 1.0f;
        public List<float> VolumesSet { get; } = [];
        public Action? ConfigUi { get; set; }
        public Action? StylesUi { get; set; }
        public int ConfigUiInvocations;
        public int StylesUiInvocations;

        public CommandRouter Router() => new(
            enabled: () => this.EnabledValue,
            setEnabled: v => this.EnabledValue = v,
            cancelSpeech: () => this.Cancelled = true,
            currentPresetId: () => this.PresetIdValue,
            presets: () => [new PresetSummary(0, "Default"), new PresetSummary(1, "Battle")],
            switchPreset: id =>
            {
                this.SwitchedTo.Add(id);
                this.PresetIdValue = id;
            },
            volume: () => this.VolumeValue,
            setVolume: v =>
            {
                this.VolumesSet.Add(v);
                this.VolumeValue = v;
            },
            openConfig: () =>
            {
                this.ConfigUiInvocations++;
                return this.ConfigUi;
            },
            openStyles: () =>
            {
                this.StylesUiInvocations++;
                return this.StylesUi;
            });
    }

    [Fact]
    public void ToggleTts_DisablesAndCancels_ThenReenables()
    {
        var rec = new Recorder();
        var router = rec.Router();

        var off = router.Execute("/toggletts", "");
        Assert.False(rec.EnabledValue);
        Assert.True(rec.Cancelled);
        Assert.Equal(AivaCommand.DisableTts, off.Action);
        Assert.Equal(["TTS disabled."], off.Output);

        var on = router.Execute("/toggletts", "");
        Assert.True(rec.EnabledValue);
        Assert.Equal(AivaCommand.EnableTts, on.Action);
        Assert.Equal(["TTS enabled."], on.Output);
    }

    [Fact]
    public void EnableDisableTts_IndependentOfCurrentState()
    {
        var rec = new Recorder { EnabledValue = true };
        var router = rec.Router();

        Assert.Equal(["TTS disabled."], router.Execute("/disabletts", "").Output);
        Assert.False(rec.EnabledValue);
        Assert.True(rec.Cancelled);

        Assert.Equal(["TTS enabled."], router.Execute("/enabletts", "").Output);
        Assert.True(rec.EnabledValue);
    }

    [Fact]
    public void CancelSpeech_IsSilent()
    {
        var rec = new Recorder();
        var result = rec.Router().Execute("/cancelspeech", "");

        Assert.True(rec.Cancelled);
        Assert.Empty(result.Output);
        Assert.Equal(AivaCommand.CancelSpeech, result.Action);
    }

    [Fact]
    public void ConfigCommand_WithWindow_InvokesIt()
    {
        var rec = new Recorder { ConfigUi = () => { } };
        var router = rec.Router();

        var result = router.Execute("/aivaconfig", "");
        Assert.Equal(AivaCommand.OpenConfig, result.Action);
        Assert.Equal(1, rec.ConfigUiInvocations);

        var alias = router.Execute("/aiva", "");
        Assert.Equal(AivaCommand.OpenConfig, alias.Action);
        Assert.Equal(2, rec.ConfigUiInvocations);
    }

    [Fact]
    public void ConfigCommand_Headless_LogsInstead()
    {
        var rec = new Recorder { ConfigUi = null };
        var result = rec.Router().Execute("/aivaconfig", "");

        Assert.Equal(AivaCommand.OpenConfig, result.Action);
        Assert.Contains("not available", Assert.Single(result.Output));
    }

    [Fact]
    public void StylesCommand_Headless_LogsInstead()
    {
        var rec = new Recorder { StylesUi = null };
        var result = rec.Router().Execute("/aivastyles", "");

        Assert.Equal(AivaCommand.OpenStyles, result.Action);
        Assert.Contains("not available", Assert.Single(result.Output));
    }

    [Fact]
    public void Preset_NoArgs_ListsCurrentAndAvailable()
    {
        var rec = new Recorder();
        var result = rec.Router().Execute("/aivapreset", "");

        Assert.Equal(AivaCommand.SwitchPreset, result.Action);
        Assert.Equal(
            ["Current preset: Default", "Available presets: Default, Battle"],
            result.Output);
        Assert.Empty(rec.SwitchedTo);
    }

    [Fact]
    public void Preset_ByName_SwitchesCaseInsensitively()
    {
        var rec = new Recorder();
        var result = rec.Router().Execute("/aivapreset", " BATTLE ");

        Assert.Equal([1], rec.SwitchedTo);
        Assert.Equal(["AIVoiceActing preset -> Battle"], result.Output);
    }

    [Fact]
    public void Preset_UnknownName_Errors()
    {
        var rec = new Recorder();
        var result = rec.Router().Execute("/aivapreset", "nope");

        Assert.Empty(rec.SwitchedTo);
        Assert.Equal(["No preset named \"nope\" exists."], result.Output);
    }

    [Theory]
    [InlineData("0", 0f)]
    [InlineData("50", 0.5f)]
    [InlineData("200", 2f)]
    [InlineData("+25", 1.25f)]
    [InlineData("-150", 0f)] // 100-150 clamps at 0
    [InlineData("999", 2f)]
    [InlineData("-1", 0.99f)] // 100-1
    public void Volume_AbsoluteAndRelative_AreClampedTo0Through200(string args, float stored)
    {
        var rec = new Recorder();
        var result = rec.Router().Execute("/aivavolume", args);

        Assert.Equal(AivaCommand.AdjustVolume, result.Action);
        Assert.Equal([stored], rec.VolumesSet);
    }

    [Fact]
    public void Volume_NoArgs_ShowsCurrent()
    {
        var rec = new Recorder { VolumeValue = 0.5f };
        var result = rec.Router().Execute("/aivavolume", "");

        Assert.Equal(["Current volume: 50%"], result.Output);
        Assert.Empty(rec.VolumesSet);
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("+x")]
    public void Volume_InvalidArgs_Errors(string args)
    {
        var result = new Recorder().Router().Execute("/aivavolume", args);
        Assert.Contains("Invalid volume", Assert.Single(result.Output));
    }

    [Fact]
    public void UnknownCommand_ReturnsUsage()
    {
        var result = new Recorder().Router().Execute("/nope", "");

        Assert.Equal(AivaCommand.Unknown, result.Action);
        Assert.Contains("Usage", result.Output[0]);
        Assert.Contains("/aivapreset", result.Output[0]);
    }

    [Fact]
    public void Commands_AreCaseInsensitive()
    {
        var rec = new Recorder();
        var router = rec.Router();

        Assert.Equal(AivaCommand.CancelSpeech, router.Execute("/CancelSpeech", "").Action);
        Assert.Equal(AivaCommand.EnableTts, router.Execute("/ENABLETTS", "").Action);
    }
}
