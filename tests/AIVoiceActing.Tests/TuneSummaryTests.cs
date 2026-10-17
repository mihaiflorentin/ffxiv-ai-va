namespace AIVoiceActing.Tests;

using AIVoiceActing.Ports;
using AIVoiceActing.UI.State;
using Xunit;

public sealed class TuneSummaryTests
{
    [Fact]
    public void AnUntouchedVoiceShowsNoChips()
    {
        Assert.Empty(TuneSummary.Chips(TuneValues.Neutral));
        Assert.True(TuneSummary.IsDefault(TuneValues.Neutral));
    }

    [Fact]
    public void OnlyNonDefaultKnobsAppear()
    {
        var chips = TuneSummary.Chips(new TuneValues(1.18f, 1f, 1.4f, 0f));

        Assert.Equal(["Pitch +18%", "Volume 140%"], chips);
    }

    [Fact]
    public void SpeedAndExpressivenessUsePlainWords()
    {
        var chips = TuneSummary.Chips(new TuneValues(1f, 0.9f, 1f, 0.2f));

        Assert.Equal(["Speed -10%", "Expressiveness +20%"], chips);
        Assert.DoesNotContain(chips, chip => Labels.ContainsJargon(chip));
    }

    [Fact]
    public void SliderRoundingNoiseIsNotAChange()
    {
        Assert.Empty(TuneSummary.Chips(new TuneValues(1.004f, 0.996f, 1.003f, 0.004f)));
    }

    [Fact]
    public void ApplyWritesTheKnobsBackWithoutTouchingTheVoice()
    {
        var slot = new VoiceSlotDto("af_heart", 0.1f, 1f, 1f, 1f);
        var tuned = new TuneValues(1.2f, 0.9f, 1.5f, 0.3f).Apply(slot);

        Assert.Equal("af_heart", tuned.Id);
        Assert.Equal(new TuneValues(1.2f, 0.9f, 1.5f, 0.3f), TuneValues.From(tuned));

        var edit = new TuneValues(1.2f, 0.9f, 1.5f, 0.3f).Apply(new CharacterEdit("am_michael", 0f, 1f, 1f, 1f));
        Assert.Equal("am_michael", edit.VoiceId);
        Assert.Equal(0.3f, edit.Bias);
    }

    [Fact]
    public void ClampedKeepsEveryKnobInRange()
    {
        var clamped = new TuneValues(9f, 0f, -1f, 4f).Clamped();

        Assert.Equal(new TuneValues(1.5f, 0.7f, 0f, 1f), clamped);
    }
}
