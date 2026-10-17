namespace AIVoiceActing.UI.Components;

using AIVoiceActing.UI.State;
using Dalamud.Bindings.ImGui;

/// <summary>
/// The Tune popover: pitch, speed, volume and expressiveness for one voice, tucked behind a
/// button instead of four sliders on every row. The caller shows <see cref="TuneSummary.Chips"/>
/// next to the button, so a tuned voice is visible without opening anything.
/// </summary>
public static class TunePopover
{
    private const float SliderWidthEm = 14f;
    private const string Format = "%.2f";

    /// <summary>What happened this frame: a live change, and whether a drag just ended.</summary>
    public readonly record struct Result(bool Changed, bool Released, TuneValues Values);

    /// <summary>
    /// Draws the button and, while it is open, the popover. Call it inside a pushed id that is
    /// unique to the row. <see cref="Result.Changed"/> fires on every edit; <see cref="Result.Released"/>
    /// once when a drag ends or Reset is pressed (the moment to persist).
    /// </summary>
    public static Result Draw(string id, TuneValues values)
    {
        var popup = "tune##" + id;
        if (ImGui.Button("Tune##" + id))
        {
            ImGui.OpenPopup(popup);
        }

        var result = new Result(false, false, values);
        if (!ImGui.BeginPopup(popup))
        {
            return result;
        }

        var pitch = values.Pitch;
        var speed = values.Speed;
        var volume = values.Volume;
        var feeling = values.Expressiveness;
        var changed = false;
        var released = false;

        changed |= Slider(Labels.Pitch, "How high or low the voice sounds. 1.00 is natural.", ref pitch, TuneValues.PitchMin, TuneValues.PitchMax, ref released);
        changed |= Slider(Labels.Speed, "How fast it speaks. 1.00 is natural.", ref speed, TuneValues.SpeedMin, TuneValues.SpeedMax, ref released);
        changed |= Slider(Labels.Volume, "How loud it is, up to double. 1.00 is natural.", ref volume, TuneValues.VolumeMin, TuneValues.VolumeMax, ref released);
        changed |= Slider(Labels.Expressiveness, "How much feeling it adds on top of your default. 0.00 adds none.", ref feeling, TuneValues.ExpressivenessMin, TuneValues.ExpressivenessMax, ref released);

        var next = new TuneValues(pitch, speed, volume, feeling).Clamped();
        Ui.Gap(Ui.Space1);
        if (ImGui.Button("Back to normal##" + id))
        {
            next = TuneValues.Neutral;
            changed = true;
            released = true;
        }

        ImGui.SameLine();
        if (ImGui.Button("Done##" + id))
        {
            ImGui.CloseCurrentPopup();
        }

        ImGui.EndPopup();
        return new Result(changed, released, next);
    }

    private static bool Slider(string label, string caption, ref float value, float min, float max, ref bool released)
    {
        ImGui.SetNextItemWidth(Ui.Em(SliderWidthEm));
        var changed = ImGui.SliderFloat(label, ref value, min, max, Format);
        if (ImGui.IsItemDeactivatedAfterEdit())
        {
            released = true;
        }

        // An auto-sizing popup has no width to wrap to yet, so pin the caption to the slider's width.
        ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + Ui.Em(SliderWidthEm));
        Ui.MutedText(caption);
        ImGui.PopTextWrapPos();
        return changed;
    }
}
