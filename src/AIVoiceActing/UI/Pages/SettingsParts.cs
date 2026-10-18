namespace AIVoiceActing.UI.Pages;

using AIVoiceActing.Ports;
using AIVoiceActing.UI.Components;
using AIVoiceActing.UI.State;
using Dalamud.Bindings.ImGui;

/// <summary>Small drawing helpers shared by the Settings cards.</summary>
internal static class SettingsParts
{
    /// <summary>
    /// A checkbox with its explanation as visible muted text underneath (nothing important hides
    /// behind a hover). <paramref name="enabled"/> false greys it out; say why in <paramref name="help"/>.
    /// </summary>
    public static void Toggle(string label, string help, Func<bool> get, Action<bool> apply, bool enabled = true)
    {
        ImGui.BeginDisabled(!enabled);
        Controls.Checkbox(label, get, apply);
        ImGui.EndDisabled();
        ImGui.Indent(ImGui.GetFrameHeight() + Ui.Em(Ui.Space1));
        Ui.MutedText(help);
        ImGui.Unindent(ImGui.GetFrameHeight() + Ui.Em(Ui.Space1));
        Ui.Gap(Ui.Space1);
    }

    /// <summary>A label in the form column followed by a segmented choice; returns the picked index.</summary>
    public static int SegmentedRow(
        string label,
        string id,
        IReadOnlyList<string> labels,
        int selected,
        IReadOnlyList<string?>? disabledReasons = null)
    {
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted(label);
        ImGui.SameLine(Ui.Em(FormRow.LabelColumnEm));
        return SegmentedControl.Draw(id, labels, selected, disabledReasons);
    }

    /// <summary>Muted text indented under the label column, for the line that explains a row.</summary>
    public static void RowHelp(string text)
    {
        ImGui.Dummy(new System.Numerics.Vector2(Ui.Em(FormRow.LabelColumnEm), 0f));
        ImGui.SameLine();
        Ui.MutedText(text);
        Ui.Gap(Ui.Space1);
    }

    /// <summary>True when the downloaded files of every required asset of <paramref name="engine"/> are on disk (cached probe).</summary>
    public static bool EngineInstalled(UiContext ctx, EngineInfo engine) =>
        EngineParts.RequiredAssets(ctx, engine).All(ctx.Probes.IsDownloaded);

    /// <summary>The engine whose status describes the graphics card: the main one when it can use it, else the expressive one.</summary>
    public static (ISpeechSynthesizer Synth, EngineInfo Info)? GpuEngine(UiContext ctx)
    {
        var primary = ctx.ActiveEngine;
        if (primary.SupportsGpu)
        {
            return (ctx.Synth, primary);
        }

        var expressive = ctx.ExpressiveSynthesizer();
        var info = ctx.EngineCatalog.Find(ctx.Config.Engine.Expressive.EngineId);
        return expressive is not null && info is { SupportsGpu: true } ? (expressive, info) : null;
    }
}
