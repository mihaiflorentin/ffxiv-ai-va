namespace AIVoiceActing.UI.Components;

using AIVoiceActing.Domain.Pipeline;
using Dalamud.Bindings.ImGui;

/// <summary>
/// Expandable trigger/exclusion list (TTT ExpandyList parity): per-row text input +
/// IsRegex checkbox + remove button, an Add row at the bottom, and one persist per
/// frame where anything was deactivated. Text edits replace the immutable record in the
/// config list; ImGui state is not retained between frames.
/// </summary>
public static class TriggerList
{
    public static void Draw(string listId, IList<TriggerSpec> list, Action save)
    {
        var persist = false;
        ImGui.PushID(listId);

        if (ImGui.CollapsingHeader($"{listId} ({list.Count})"))
        {
            for (var i = list.Count - 1; i >= 0; i--)
            {
                var item = list[i];
                ImGui.PushID(i);

                if (Controls.Button("Remove", enabled: true))
                {
                    list.RemoveAt(i);
                    save();
                    ImGui.PopID();
                    continue;
                }

                ImGui.SameLine();

                var text = item.Text;
                ImGui.SetNextItemWidth(320f);
                if (ImGui.InputTextWithHint("##text", "Text or regex…", ref text, 256))
                {
                    list[i] = item with { Text = text };
                    persist |= ImGui.IsItemDeactivatedAfterEdit();
                }

                ImGui.SameLine();

                var isRegex = item.IsRegex;
                if (ImGui.Checkbox("Regex", ref isRegex))
                {
                    list[i] = item with { IsRegex = isRegex };
                    save();
                }

                ImGui.PopID();
            }

            if (persist)
            {
                save();
            }

            if (ImGui.Button("Add"))
            {
                list.Add(new TriggerSpec(string.Empty, IsRegex: false));
                save();
            }
        }

        ImGui.PopID();
    }
}
