namespace AIVoiceActing.UI.Pages;

using AIVoiceActing.UI.Components;
using AIVoiceActing.UI.State;
using Dalamud.Bindings.ImGui;

/// <summary>
/// Style tags (formerly the separate Styles window): an on/off switch, the marker character that
/// wraps a tag, and a small tester that copies a line wrapped in markers. The match pattern behind
/// the marker is derived from it (<see cref="Configuration.StyleRegex"/>) and is only shown, never
/// edited, and only when advanced details are on.
/// </summary>
internal sealed class StyleTagsPanel(UiContext ctx)
{
    private const float MarkerWidthEm = 5f;

    private string preview = string.Empty;

    public void Draw()
    {
        var c = ctx.Config;
        var save = ctx.Save;
        CheckRow.Draw(
            "Allow style tags in text",
            "A line can carry a direction between markers, for example |laughs|. The tag is removed " +
            "from the spoken text and sent to the voice as a direction.",
            () => c.AdHocStyleTagsEnabled,
            v => { c.AdHocStyleTagsEnabled = v; save(); });

        using (FormRow.Begin("Marker", "The character placed before and after a tag."))
        {
            var tag = c.StyleTag;
            ImGui.SetNextItemWidth(Ui.Em(MarkerWidthEm));
            if (ImGui.InputText("##marker", ref tag, 8))
            {
                c.StyleTag = tag.Trim();
            }

            if (ImGui.IsItemDeactivatedAfterEdit())
            {
                save();
            }
        }

        if (string.IsNullOrWhiteSpace(c.StyleTag))
        {
            Theme.IconText(Theme.Icon(Severity.Warning), Theme.Warn);
            ImGui.SameLine(0f, Ui.Em(Ui.Space2));
            ImGui.TextUnformatted("The marker is empty, so style tags are off.");
        }
        else if (c.ShowAdvanced)
        {
            Ui.MutedText($"Match pattern: {c.StyleRegex}");
        }

        using (FormRow.Begin("Try a line", "Type a line to see it wrapped in markers, then copy it."))
        {
            ImGui.InputText("##style-preview", ref this.preview, 256);
        }

        ImGui.Indent(Ui.Em(FormRow.LabelColumnEm));
        var hasText = !string.IsNullOrWhiteSpace(this.preview);
        if (Controls.Button("Copy with markers", hasText, "Type a line first."))
        {
            ImGui.SetClipboardText(Wrap(c.StyleTag, this.preview));
        }

        if (hasText)
        {
            Ui.MutedText(Wrap(c.StyleTag, this.preview));
        }

        ImGui.Unindent(Ui.Em(FormRow.LabelColumnEm));
    }

    /// <summary>Wraps the line with the marker pair, as TextToTalk's CopyStyleToClipboard does.</summary>
    private static string Wrap(string tag, string text) => $"{tag}{text}{tag}";
}
