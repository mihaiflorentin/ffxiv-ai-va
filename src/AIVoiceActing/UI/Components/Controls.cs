namespace AIVoiceActing.UI.Components;

using System.Numerics;
using Dalamud.Bindings.ImGui;

/// <summary>
/// Config-bound ImGui widgets: every mutation applies immediately and calls the injected
/// save delegate (TTT's checkbox+Save pattern, hand-written). Sliders/DragFloats write
/// through on every change but persist once per interaction (IsItemDeactivatedAfterEdit),
/// so dragging never spams the config file. Draw-layer only — no state lives here.
/// </summary>
public static class Controls
{
    /// <summary>Section heading (SeparatorText is absent from these bindings).</summary>
    public static void Section(string label)
    {
        ImGui.Separator();
        ImGui.TextDisabled(label);
    }

    /// <summary>Checkbox bound to a config property: <c>Controls.Checkbox("Label", () => c.X, v => { c.X = v; save(); })</c>.</summary>
    public static void Checkbox(string label, Func<bool> get, Action<bool> apply, string? tooltip = null)
    {
        var value = get();
        if (ImGui.Checkbox(label, ref value))
        {
            apply(value);
        }

        if (tooltip is not null)
        {
            Tooltip(tooltip);
        }
    }

    public static void Checkbox(string label, string help, Func<bool> get, Action<bool> apply)
    {
        Checkbox(label, get, apply);
        HelpMarker(help);
    }

    /// <summary>Slider 0..1 (fraction) bound to a float config property.</summary>
    public static void SliderFraction(string label, Func<float> get, Action<float> write, Action save, string? tooltip = null)
    {
        var value = get();
        if (ImGui.SliderFloat(label, ref value, 0f, 1f))
        {
            write(value);
        }

        if (tooltip is not null)
        {
            Tooltip(tooltip);
        }

        PersistOnRelease(save);
    }

    /// <summary>Slider over a scaled integer surface (e.g. volume 0–200% stored ÷100).</summary>
    public static void SliderScaled(string label, float scale, Func<float> get, Action<float> write, Action save, string? tooltip = null)
    {
        var value = get() * scale;
        if (ImGui.SliderFloat(label, ref value, 0f, scale, "%.0f"))
        {
            write(value / scale);
        }

        if (tooltip is not null)
        {
            Tooltip(tooltip);
        }

        PersistOnRelease(save);
    }

    /// <summary>DragFloat with hard bounds (rate limiter 0.1–30 msg/s).</summary>
    public static void DragFloat(string label, float min, float max, Func<float> get, Action<float> write, Action save, string? tooltip = null)
    {
        var value = get();
        if (ImGui.DragFloat(label, ref value, (max - min) / 200f, min, max, "%.1f"))
        {
            write(Math.Clamp(value, min, max));
        }

        if (tooltip is not null)
        {
            Tooltip(tooltip);
        }

        PersistOnRelease(save);
    }

    /// <summary>Integer combo bound to a config property (indexes into <paramref name="items"/>).</summary>
    public static void Combo(string label, IReadOnlyList<string> items, Func<int> get, Action<int> apply, string? tooltip = null)
    {
        var index = Math.Clamp(get(), 0, items.Count - 1);
        // Array + count on purpose: the raw span overload is the pattern every working
        // combo here uses (KeyCombo, preset switcher); the generated IReadOnlyList
        // binding path rendered selections that never committed.
        var array = items as string[] ?? items.ToArray();
        if (ImGui.Combo(label, ref index, array, array.Length))
        {
            apply(index);
        }

        if (tooltip is not null)
        {
            Tooltip(tooltip);
        }
    }

    /// <summary>String combo by value (EP selector: auto/cpu/directml/coreml).</summary>
    public static void Combo(string label, IReadOnlyList<string> items, Func<string> get, Action<string> apply, string? tooltip = null)
    {
        var index = Math.Max(0, items.ToList().IndexOf(get()));
        var array = items as string[] ?? items.ToArray();
        if (ImGui.Combo(label, ref index, array, array.Length))
        {
            apply(array[index]);
        }

        if (tooltip is not null)
        {
            Tooltip(tooltip);
        }
    }

    /// <summary>Keyboard-code combo (VirtualKey wire values: modifiers and keys).</summary>
    public static void KeyCombo(string label, IReadOnlyList<(int Code, string Name)> keys, Func<int> get, Action<int> apply, string? tooltip = null)
    {
        var names = keys.Select(k => k.Name).ToArray();
        var index = keys.ToList().FindIndex(k => k.Code == get());
        if (index < 0)
        {
            index = 0;
        }

        if (ImGui.Combo(label, ref index, names, names.Length))
        {
            apply(keys[index].Code);
        }

        if (tooltip is not null)
        {
            Tooltip(tooltip);
        }
    }

    /// <summary>A button that is visibly disabled with a hover reason when <paramref name="enabled"/> is false.</summary>
    public static bool Button(string label, bool enabled, string? disabledReason = null)
    {
        if (!enabled)
        {
            ImGui.BeginDisabled();
            ImGui.Button(label);
            ImGui.EndDisabled();
            if (disabledReason is not null && ImGui.IsItemHovered())
            {
                ImGui.SetTooltip(disabledReason);
            }

            return false;
        }

        return ImGui.Button(label);
    }

    /// <summary>Hover tooltip on the previously drawn item (use right after a control).</summary>
    public static void Tooltip(string text)
    {
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(text);
        }
    }

    public static void HelpMarker(string text)
    {
        ImGui.SameLine();
        ImGui.TextDisabled("(?)");
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(text);
        }
    }

    /// <summary>Indented sub-option (TTT's courtesy toggles sit under their capture source).</summary>
    public static void IndentedCheckbox(string label, Func<bool> get, Action<bool> apply, string? tooltip = null)
    {
        ImGui.Indent();
        Checkbox(label, get, apply, tooltip);
        ImGui.Unindent();
    }

    private static void PersistOnRelease(Action save)
    {
        if (ImGui.IsItemDeactivatedAfterEdit())
        {
            save();
        }
    }
}
