namespace AIVoiceActing.UI;

using System.Numerics;
using AIVoiceActing.UI.Components;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

/// <summary>
/// Ad-hoc style tags (TTT's /tttstyles Configure-ad-hoc-tags header): enable toggle, the
/// tag delimiter (whose derived regex lives in <see cref="Configuration.StyleRegex"/> and
/// is never edited), a live preview, and copy-to-clipboard of the wrapped line.
/// </summary>
public sealed class StylesWindow : Window
{
    private readonly Configuration config;
    private readonly Action save;
    private string preview = string.Empty;

    public StylesWindow(Configuration config, Action save)
        : base("AI Voice Acting Styles###AIVAStyles")
    {
        this.config = config;
        this.save = save;
    }

    public override void Draw()
    {
        var c = this.config;
        Controls.Checkbox(
            "Enable ad-hoc style tags",
            "Lines may carry [delimiter]tag[delimiter] directions, e.g. |laughs| — the tag " +
            "is stripped from the spoken text and fed to the synthesizer as a direction.",
            () => c.AdHocStyleTagsEnabled,
            v => { c.AdHocStyleTagsEnabled = v; this.save(); });

        Controls.Section("Tag delimiter");
        var tag = c.StyleTag;
        ImGui.SetNextItemWidth(60f);
        if (ImGui.InputText("##delimiter", ref tag, 8))
        {
            c.StyleTag = tag.Trim();
            if (ImGui.IsItemDeactivatedAfterEdit())
            {
                this.save();
            }
        }


        if (string.IsNullOrWhiteSpace(c.StyleTag))
        {
            ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(1f, 0.4f, 0.4f, 1f));
            ImGui.TextUnformatted("Delimiter empty — tag matching disabled.");
            ImGui.PopStyleColor();
        }
        else
        {
            ImGui.SameLine();
            ImGui.TextDisabled($"regex: {c.StyleRegex}");
        }

        Controls.HelpMarker("The match regex is always derived from the delimiter.");

        Controls.Section("Preview");
        ImGui.InputText("##style-preview", ref this.preview, 256);
        if (Controls.Button("Copy wrapped to clipboard", !string.IsNullOrWhiteSpace(this.preview), null))
        {
            ImGui.SetClipboardText(Wrap(c.StyleTag, this.preview));
        }

        ImGui.SameLine();
        ImGui.TextDisabled(Wrap(c.StyleTag, this.preview));
    }

    /// <summary>TTT's CopyStyleToClipboard: wraps the line with the delimiter pair.</summary>
    private static string Wrap(string tag, string text) => $"{tag}{text}{tag}";
}
