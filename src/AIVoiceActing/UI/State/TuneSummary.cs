namespace AIVoiceActing.UI.State;

using System.Globalization;
using AIVoiceActing.Ports;

/// <summary>The four knobs of the Tune popover. 1 is natural for pitch, speed and volume; expressiveness 0 adds nothing.</summary>
public sealed record TuneValues(float Pitch, float Speed, float Volume, float Expressiveness)
{
    public const float PitchMin = 0.5f;
    public const float PitchMax = 1.5f;
    public const float SpeedMin = 0.7f;
    public const float SpeedMax = 1.3f;
    public const float VolumeMin = 0f;
    public const float VolumeMax = 2f;
    public const float ExpressivenessMin = 0f;
    public const float ExpressivenessMax = 1f;

    public static readonly TuneValues Neutral = new(1f, 1f, 1f, 0f);

    public static TuneValues From(VoiceSlotDto slot) =>
        new(slot.Pitch, slot.Speed, slot.Volume, slot.ExaggerationBias);

    public static TuneValues From(CharacterEdit edit) =>
        new(edit.Pitch, edit.Speed, edit.Volume, edit.Bias);

    public VoiceSlotDto Apply(VoiceSlotDto slot) =>
        slot with { Pitch = this.Pitch, Speed = this.Speed, Volume = this.Volume, ExaggerationBias = this.Expressiveness };

    public CharacterEdit Apply(CharacterEdit edit) =>
        edit with { Pitch = this.Pitch, Speed = this.Speed, Volume = this.Volume, Bias = this.Expressiveness };

    /// <summary>The same values pulled back inside each knob's range.</summary>
    public TuneValues Clamped() => new(
        Math.Clamp(this.Pitch, PitchMin, PitchMax),
        Math.Clamp(this.Speed, SpeedMin, SpeedMax),
        Math.Clamp(this.Volume, VolumeMin, VolumeMax),
        Math.Clamp(this.Expressiveness, ExpressivenessMin, ExpressivenessMax));
}

/// <summary>
/// The chips shown next to a voice when it has been tuned: only the knobs that differ from the
/// default appear, so an untouched voice shows nothing at all.
/// </summary>
public static class TuneSummary
{
    /// <summary>Differences smaller than this are treated as "not changed" (slider rounding).</summary>
    public const float Tolerance = 0.005f;

    public static bool IsDefault(TuneValues values) => Chips(values).Count == 0;

    /// <summary>"Pitch +18%", "Speed -10%", "Volume 140%", "Expressiveness +20%" for each non-default knob.</summary>
    public static IReadOnlyList<string> Chips(TuneValues values)
    {
        var chips = new List<string>();
        if (Differs(values.Pitch, 1f))
        {
            chips.Add($"{Labels.Pitch} {Signed(values.Pitch - 1f)}");
        }

        if (Differs(values.Speed, 1f))
        {
            chips.Add($"{Labels.Speed} {Signed(values.Speed - 1f)}");
        }

        if (Differs(values.Volume, 1f))
        {
            chips.Add($"{Labels.Volume} {Percent(values.Volume)}");
        }

        if (Differs(values.Expressiveness, 0f))
        {
            chips.Add($"{Labels.Expressiveness} {Signed(values.Expressiveness)}");
        }

        return chips;
    }

    private static bool Differs(float value, float natural) => Math.Abs(value - natural) > Tolerance;

    private static string Percent(float fraction) =>
        (fraction * 100f).ToString("0", CultureInfo.InvariantCulture) + "%";

    private static string Signed(float delta)
    {
        var percent = (int)Math.Round(delta * 100f, MidpointRounding.AwayFromZero);
        return (percent >= 0 ? "+" : "-") + Math.Abs(percent).ToString(CultureInfo.InvariantCulture) + "%";
    }
}
