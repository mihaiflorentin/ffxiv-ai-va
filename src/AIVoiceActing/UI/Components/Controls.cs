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
    public static void Checkbox(string label, Func<bool> get, Action<bool> apply)
    {
        var value = get();
        if (ImGui.Checkbox(label, ref value))
        {
            apply(value);
        }
    }

    public static void Checkbox(string label, string help, Func<bool> get, Action<bool> apply)
    {
        Checkbox(label, get, apply);
        HelpMarker(help);
    }

    /// <summary>Slider 0..1 (fraction) bound to a float config property.</summary>
    public static void SliderFraction(string label, Func<float> get, Action<float> write, Action save)
    {
        var value = get();
        if (ImGui.SliderFloat(label, ref value, 0f, 1f))
        {
            write(value);
        }

        PersistOnRelease(save);
    }

    /// <summary>Slider over a scaled integer surface (e.g. volume 0–200% stored ÷100).</summary>
    public static void SliderScaled(string label, float scale, Func<float> get, Action<float> write, Action save)
    {
        var value = get() * scale;
        if (ImGui.SliderFloat(label, ref value, 0f, scale, "%.0f"))
        {
            write(value / scale);
        }

        PersistOnRelease(save);
    }

    /// <summary>DragFloat with hard bounds (rate limiter 0.1–30 msg/s).</summary>
    public static void DragFloat(string label, float min, float max, Func<float> get, Action<float> write, Action save)
    {
        var value = get();
        if (ImGui.DragFloat(label, ref value, (max - min) / 200f, min, max, "%.1f"))
        {
            write(Math.Clamp(value, min, max));
        }

        PersistOnRelease(save);
    }

    /// <summary>Integer combo bound to a config property (indexes into <paramref name="items"/>).</summary>
    public static void Combo(string label, IReadOnlyList<string> items, Func<int> get, Action<int> apply)
    {
        var index = Math.Clamp(get(), 0, items.Count - 1);
        if (ImGui.Combo(label, ref index, items))
        {
            apply(index);
        }
    }

    /// <summary>String combo by value (EP selector: auto/cpu/directml/coreml).</summary>
    public static void Combo(string label, IReadOnlyList<string> items, Func<string> get, Action<string> apply)
    {
        var index = Math.Max(0, items.ToList().IndexOf(get()));
        if (ImGui.Combo(label, ref index, items))
        {
            apply(items[index]);
        }
    }

    /// <summary>Keyboard-code combo (VirtualKey wire values: modifiers and keys).</summary>
    public static void KeyCombo(string label, IReadOnlyList<(int Code, string Name)> keys, Func<int> get, Action<int> apply)
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
    public static void IndentedCheckbox(string label, Func<bool> get, Action<bool> apply)
    {
        ImGui.Indent();
        Checkbox(label, get, apply);
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
