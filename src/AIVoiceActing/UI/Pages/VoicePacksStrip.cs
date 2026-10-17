namespace AIVoiceActing.UI.Pages;

using AIVoiceActing.Ports;
using AIVoiceActing.UI.Components;
using AIVoiceActing.UI.State;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;

/// <summary>
/// The voice packs under the library: one row per pack with an on/off switch, what it holds and
/// its licence, the credit its authors ask for, and any problems the scan found. Turning a pack
/// off saves the choice, rescans the library and rebuilds the voice map, so its voices stop being
/// offered at once (anything still cast to them shows as missing). The built-in voices cannot be
/// switched off.
/// </summary>
internal sealed class VoicePacksStrip(UiContext ctx)
{
    public void Draw()
    {
        SectionHeader.Draw("Voice packs");
        var packs = ctx.VoiceLibrary.Packs;
        var voices = ctx.VoiceLibrary.Voices;
        foreach (var pack in packs)
        {
            this.DrawPack(pack, voices);
        }

        Ui.Gap(Ui.Space1);
        if (ImGui.Button("Open packs folder##packs"))
        {
            ctx.TryOpen(ctx.PacksDir());
        }

        ImGui.SameLine();
        if (ImGui.Button("Rescan##packs"))
        {
            this.Rescan();
        }

        ImGui.SameLine();
        Ui.MutedTextInline("Drop a pack folder into the packs folder, then rescan.");
    }

    private void DrawPack(VoicePackInfo pack, IReadOnlyList<VoiceInfo> voices)
    {
        ImGui.PushID(pack.Id);
        var builtIn = IsBuiltIn(pack, voices);
        var enabled = pack.Enabled;
        ImGui.BeginDisabled(builtIn);
        if (ImGui.Checkbox(pack.Name + "##enabled", ref enabled))
        {
            this.SetEnabled(pack, enabled);
        }

        ImGui.EndDisabled();

        ImGui.SameLine();
        Ui.MutedTextInline(Describe(pack, builtIn));

        ImGui.Indent();
        foreach (var credit in voices
                     .Where(v => v.PackId == pack.Id && !string.IsNullOrWhiteSpace(v.Attribution))
                     .Select(v => v.Attribution!)
                     .Distinct(StringComparer.Ordinal)
                     .Take(2))
        {
            Ui.MutedText(credit);
        }

        foreach (var problem in pack.Problems)
        {
            StatusPill.Draw(Severity.Warning, problem);
        }

        ImGui.Unindent();
        ImGui.PopID();
    }

    private void SetEnabled(VoicePackInfo pack, bool enabled)
    {
        var disabled = ctx.Config.Voices.DisabledPacks;
        disabled.RemoveAll(id => id.Equals(pack.Id, StringComparison.OrdinalIgnoreCase));
        if (!enabled)
        {
            disabled.Add(pack.Id);
        }

        ctx.Save();
        ctx.VoiceLibrary.Reload();
        ctx.InvalidateVoiceMap();
        ctx.Status.Post(
            Severity.Info,
            enabled ? $"Turned on {pack.Name}" : $"Turned off {pack.Name}",
            enabled ? "Its voices are available again." : "Its voices are no longer offered.");
    }

    private void Rescan()
    {
        ctx.VoiceLibrary.Reload();
        ctx.InvalidateVoiceMap();
        var usable = ctx.VoiceLibrary.Voices.Count(v => v.Available);
        ctx.Status.Post(
            Severity.Success,
            "Rescanned voice packs",
            $"{usable} voices in {ctx.VoiceLibrary.Packs.Count} packs.");
    }

    private static bool IsBuiltIn(VoicePackInfo pack, IReadOnlyList<VoiceInfo> voices) =>
        voices.Any(v => v.PackId == pack.Id && v.Quality == VoiceQuality.Stock);

    private static string Describe(VoicePackInfo pack, bool builtIn)
    {
        var parts = new List<string>();
        if (builtIn)
        {
            parts.Add("Built in, always on");
        }
        else if (!pack.Enabled)
        {
            parts.Add("Off");
        }

        if (pack.Version.Length > 0)
        {
            parts.Add("version " + pack.Version);
        }

        parts.Add(pack.VoiceCount == 1 ? "1 voice" : $"{pack.VoiceCount} voices");
        if (pack.License.Length > 0)
        {
            parts.Add(pack.License);
        }

        if (pack.Bytes > 0)
        {
            parts.Add(Labels.FileSize(pack.Bytes));
        }

        return string.Join(" · ", parts);
    }
}
