namespace AIVoiceActing.UI.Components;

using AIVoiceActing.UI.State;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;

/// <summary>
/// Inline message with a severity stripe, a title, optional detail and action buttons. Used for
/// "engine not installed", "download failed" and similar, on every page that needs them.
/// </summary>
public static class Banner
{
    /// <summary>Draws a banner; returns the index of the clicked action, or -1.</summary>
    public static int Draw(string id, Severity severity, string title, string? detail, IReadOnlyList<string>? actionLabels = null)
    {
        var clicked = -1;
        var colour = Theme.Colour(severity);
        using (ImRaii.PushColor(ImGuiCol.Border, Theme.WithAlpha(colour, 0.6f)))
        {
            using var card = Card.Begin("banner-" + id, Theme.WithAlpha(colour, 0.14f));
            if (card.Visible)
            {
                Theme.IconText(Theme.Icon(severity), colour);
                ImGui.SameLine(0f, Ui.Em(Ui.Space2));
                ImGui.TextWrapped(title);
                if (!string.IsNullOrEmpty(detail))
                {
                    Ui.MutedText(detail);
                }

                if (actionLabels is { Count: > 0 })
                {
                    Ui.Gap(Ui.Space1);
                    for (var i = 0; i < actionLabels.Count; i++)
                    {
                        if (i > 0)
                        {
                            ImGui.SameLine();
                        }

                        var pressed = i == 0
                            ? Theme.PrimaryButton(actionLabels[i] + "##" + id + i)
                            : ImGui.Button(actionLabels[i] + "##" + id + i);
                        if (pressed)
                        {
                            clicked = i;
                        }
                    }
                }
            }
        }

        return clicked;
    }

    /// <summary>Draws a status-centre message, runs the clicked action, and adds a dismiss button for non-error entries.</summary>
    public static void Draw(StatusCenter center, StatusMessage message)
    {
        var labels = message.Actions.Select(a => a.Label).ToList();
        var title = message.Count > 1 ? $"{message.Title} (x{message.Count})" : message.Title;
        var clicked = Draw("m" + message.Id, message.Severity, title, message.Detail, labels);
        if (clicked >= 0)
        {
            center.Invoke(message.Id, clicked);
            return;
        }

        if (message.Severity == Severity.Error)
        {
            ImGui.SameLine();
            if (IconButton.Draw("##dismiss" + message.Id, FontAwesomeIcon.Times, "Dismiss"))
            {
                center.Dismiss(message.Id);
            }
        }
    }
}
