namespace AIVoiceActing.UI.Components;

using AIVoiceActing.Domain.Pipeline;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;

/// <summary>Plain-language copy for one <see cref="TriggerList"/>.</summary>
/// <param name="EmptyTitle">First line of the empty state.</param>
/// <param name="EmptyHint">What adding a rule does.</param>
/// <param name="AddLabel">Button that adds a rule.</param>
/// <param name="InputHint">Placeholder of an empty rule row.</param>
/// <param name="RemoveTip">Tooltip of the remove button.</param>
public sealed record TriggerListText(string EmptyTitle, string EmptyHint, string AddLabel, string InputHint, string RemoveTip);

/// <summary>
/// An editable list of text rules: one row per rule (text box, remove button) with an Add button
/// below, or an empty state with the same action when there are none. The "match as a pattern"
/// choice is advanced detail: it is a checkbox only when <c>showAdvanced</c> is on, and otherwise a
/// read-only "Pattern" tag so a rule written that way is never invisible. Text edits replace the
/// immutable record in the config list and persist when the box loses focus; ImGui state is not
/// retained between frames.
/// </summary>
public static class TriggerList
{
    private const string PatternLabel = "Pattern";

    private const string PatternTag = "[Pattern]";

    public static void Draw(string listId, IList<TriggerSpec> list, Action save, TriggerListText text, bool showAdvanced)
    {
        ImGui.PushID(listId);

        if (list.Count == 0)
        {
            if (EmptyState.Draw(FontAwesomeIcon.CheckCircle, text.EmptyTitle, text.EmptyHint, text.AddLabel))
            {
                Add(list, save);
            }

            ImGui.PopID();
            return;
        }

        var persist = false;
        for (var i = 0; i < list.Count; i++)
        {
            var item = list[i];
            ImGui.PushID(i);

            var style = ImGui.GetStyle();
            var patternWidth = 0f;
            if (showAdvanced)
            {
                patternWidth = CheckRow.Offset() + ImGui.CalcTextSize(PatternLabel).X + style.ItemSpacing.X;
            }
            else if (item.IsRegex)
            {
                patternWidth = ImGui.CalcTextSize(PatternTag).X + style.ItemSpacing.X;
            }

            var removeWidth = ImGui.GetFrameHeight() + style.ItemSpacing.X;
            var edit = item.Text;
            ImGui.SetNextItemWidth(Math.Max(Ui.Em(6f), Ui.Fill() - patternWidth - removeWidth));
            if (ImGui.InputTextWithHint("##text", text.InputHint, ref edit, 256))
            {
                list[i] = item with { Text = edit };
            }

            if (ImGui.IsItemDeactivatedAfterEdit())
            {
                persist = true;
            }

            if (showAdvanced)
            {
                ImGui.SameLine();
                var isPattern = item.IsRegex;
                if (ImGui.Checkbox(PatternLabel, ref isPattern))
                {
                    list[i] = list[i] with { IsRegex = isPattern };
                    save();
                }

                Ui.Tooltip("Treat the text as a regular expression instead of a plain word or phrase.");
            }
            else if (item.IsRegex)
            {
                ImGui.SameLine();
                Chip.Tag("Pattern");
            }

            ImGui.SameLine();
            if (IconButton.Draw("##remove", FontAwesomeIcon.Trash, text.RemoveTip))
            {
                list.RemoveAt(i--);
                save();
            }

            ImGui.PopID();
        }

        if (persist)
        {
            save();
        }

        Ui.Gap(Ui.Space1);
        if (ImGui.Button(text.AddLabel))
        {
            Add(list, save);
        }

        ImGui.PopID();
    }

    private static void Add(IList<TriggerSpec> list, Action save)
    {
        list.Add(new TriggerSpec(string.Empty, IsRegex: false));
        save();
    }
}
