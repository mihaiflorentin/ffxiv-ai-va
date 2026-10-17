namespace AIVoiceActing.UI.Components;

using AIVoiceActing.UI.State;
using Dalamud.Bindings.ImGui;

/// <summary>
/// Search box, accent / sex / age filters and the "N of M shown" line above a voice list. A group
/// with up to <see cref="InlineMax"/> values shows as chips; a bigger one folds into a button
/// that opens a checklist, so the bar never grows into a wall of pills. Filters are view state:
/// they change what is listed and never touch a stored selection.
/// </summary>
public static class VoiceFilterBar
{
    public const int InlineMax = 3;
    private const float SearchWidthEm = 22f;
    private const float WrapAtEm = 10f;

    private static readonly (VoiceFilterGroup Group, string Name)[] Groups =
    [
        (VoiceFilterGroup.Accent, "Accent"),
        (VoiceFilterGroup.Sex, "Sex"),
        (VoiceFilterGroup.Age, "Age"),
    ];

    /// <summary>Draws the bar for <paramref name="result"/>, the model's current list.</summary>
    public static void Draw(string id, VoiceLibraryModel model, VoiceListResult result)
    {
        ImGui.PushID(id);
        var search = model.Search;
        if (SearchBox.Draw("voices-" + id, ref search, "Search by name, accent or description", SearchWidthEm, 64))
        {
            model.Search = search;
        }

        ImGui.SameLine();
        ImGui.AlignTextToFramePadding();
        Ui.MutedTextInline(result.CountText);
        if (model.HasFilters)
        {
            ImGui.SameLine();
            if (ImGui.Button("Clear filters##" + id))
            {
                model.Clear();
            }
        }

        var first = true;
        foreach (var (group, name) in Groups)
        {
            var options = result.Options(group);
            if (options.Count < 2 && model.ActiveCount(group) == 0)
            {
                continue; // nothing to choose between
            }

            if (!first && ImGui.GetContentRegionAvail().X > Ui.Em(WrapAtEm))
            {
                ImGui.SameLine(0f, Ui.Em(Ui.Space4));
            }

            first = false;
            if (options.Count <= InlineMax)
            {
                DrawChips(model, group, name, options);
            }
            else
            {
                DrawChecklist(model, group, name, options);
            }
        }

        ImGui.PopID();
    }

    private static void DrawChips(VoiceLibraryModel model, VoiceFilterGroup group, string name, IReadOnlyList<FilterOption> options)
    {
        ImGui.AlignTextToFramePadding();
        Ui.MutedTextInline(name);
        foreach (var option in options)
        {
            ImGui.SameLine();
            if (Chip.Toggle($"{option.Label}##{group}-{option.Value}", model.IsOn(group, option.Value)))
            {
                model.Toggle(group, option.Value);
            }
        }
    }

    private static void DrawChecklist(VoiceLibraryModel model, VoiceFilterGroup group, string name, IReadOnlyList<FilterOption> options)
    {
        var popup = "choose-" + group;
        var chosen = options.Where(o => model.IsOn(group, o.Value)).ToList();
        var summary = chosen.Count switch
        {
            0 => "any",
            1 => chosen[0].Label,
            _ => $"{chosen.Count} chosen",
        };

        ImGui.AlignTextToFramePadding();
        Ui.MutedTextInline(name);
        ImGui.SameLine();
        var open = chosen.Count > 0
            ? Theme.PrimaryButton($"{summary}##{popup}")
            : ImGui.Button($"{summary}##{popup}");
        if (open)
        {
            ImGui.OpenPopup(popup);
        }

        if (!ImGui.BeginPopup(popup))
        {
            return;
        }

        foreach (var option in options)
        {
            var on = model.IsOn(group, option.Value);
            if (ImGui.Checkbox($"{option.Label} ({option.Count})##{group}-{option.Value}", ref on))
            {
                model.Toggle(group, option.Value);
            }
        }

        if (chosen.Count > 0 && ImGui.Button("Show all##" + group))
        {
            foreach (var option in chosen)
            {
                model.Toggle(group, option.Value);
            }
        }

        ImGui.EndPopup();
    }
}
