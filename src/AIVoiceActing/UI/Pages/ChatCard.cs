namespace AIVoiceActing.UI.Pages;

using AIVoiceActing.UI.Components;
using AIVoiceActing.UI.State;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;

/// <summary>
/// The Chat card: which chat channels are read. A preset picker, a "read every channel" shortcut,
/// the two from-you filters, and a searchable channel list grouped by category with a select-all
/// action per category and an "N of M on" count. The preset's name and hold-to-talk key are less
/// common and live under More options (<see cref="DrawPresetDetails"/>).
/// </summary>
internal sealed class ChatCard(UiContext ctx)
{
    private const float PresetComboEm = 12f;
    private const float ChannelColumnEm = 13f;

    private string search = string.Empty;
    private string openedFor = string.Empty;

    /// <summary>The active preset, healing the stored pointer when it dangles.</summary>
    private EnabledChatTypesPreset? Current()
    {
        var c = ctx.Config;
        var presets = c.EnabledChatTypesPresets;
        var current = c.CurrentPreset ?? presets.FirstOrDefault();
        if (current is not null && current.Id != c.CurrentPresetId)
        {
            c.CurrentPresetId = current.Id;
            ctx.Save(); // auto-heal: persist the corrected pointer immediately
        }

        return current;
    }

    public void Draw()
    {
        var c = ctx.Config;
        var save = ctx.Save;
        var preset = this.DrawPresetPicker();

        Ui.Gap(Ui.Space1);
        if (preset is not null)
        {
            CheckRow.Draw(
                "Read every channel",
                "Skips the list below. Handy for a quick start; turn it off to pick channels.",
                () => preset.EnableAllChatTypes,
                v => { preset.EnableAllChatTypes = v; save(); });
        }

        CheckRow.Draw(
            "Skip my own messages",
            "Never read what you type yourself, such as your emotes and party chat.",
            () => c.SkipMessagesFromYou,
            v => { c.SkipMessagesFromYou = v; save(); });
        CheckRow.Draw(
            "Only read my own messages",
            "Read nothing except what you type, to hear your own lines back.",
            () => c.OnlyMessagesFromYou,
            v => { c.OnlyMessagesFromYou = v; save(); });

        if (preset is null)
        {
            return;
        }

        SectionHeader.Draw("Channels");
        if (preset.EnableAllChatTypes)
        {
            Ui.MutedText("Every channel is read. Turn off \"Read every channel\" to choose.");
            return;
        }

        preset.EnabledChatTypes ??= new List<int>();
        this.DrawChannels(preset.EnabledChatTypes);
    }

    /// <summary>Name and hold-to-talk key of the active preset, for the More options section.</summary>
    public void DrawPresetDetails()
    {
        var preset = this.Current();
        if (preset is null)
        {
            return;
        }

        var save = ctx.Save;
        using (FormRow.Begin("Preset name", "Shown in the preset list and by the preset command."))
        {
            var name = preset.Name ?? string.Empty;
            if (ImGui.InputText("##preset-name", ref name, 64))
            {
                preset.Name = name;
            }

            if (ImGui.IsItemDeactivatedAfterEdit())
            {
                save();
            }
        }

        CheckRow.Draw(
            "Read this preset's channels only while I hold keys",
            "A hold-to-talk gate: nothing is read from these channels unless the keys are down.",
            () => preset.UseKeybind,
            v => { preset.UseKeybind = v; save(); });
        if (preset.UseKeybind)
        {
            ImGui.Indent(CheckRow.Offset());
            Controls.KeyCombo("Modifier key", KeyOptions.Modifiers, () => preset.ModifierKey, v => { preset.ModifierKey = v; save(); });
            Controls.KeyCombo("Key", KeyOptions.MajorKeys, () => preset.MajorKey, v => { preset.MajorKey = v; save(); });
            ImGui.Unindent(CheckRow.Offset());
        }
    }

    private EnabledChatTypesPreset? DrawPresetPicker()
    {
        var c = ctx.Config;
        var save = ctx.Save;
        var presets = c.EnabledChatTypesPresets;
        var current = this.Current();

        var names = presets.Select(p => p.Name ?? $"#{p.Id}").ToArray();
        var index = Math.Max(0, presets.IndexOf(current!));
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted("Preset");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(Ui.Em(PresetComboEm));
        if (names.Length > 0 && ImGui.Combo("##preset", ref index, names, names.Length))
        {
            c.CurrentPresetId = presets[index].Id;
            save();
            current = presets[index];
        }

        Ui.Tooltip("Presets are saved sets of channels. Switch between them here or with the preset command.");

        ImGui.SameLine();
        if (ImGui.Button("New preset"))
        {
            var created = new EnabledChatTypesPreset
            {
                Id = presets.Count == 0 ? 0 : presets.Max(p => p.Id) + 1,
                Name = $"Preset {presets.Count + 1}",
                EnabledChatTypes = [ChatChannels.NpcDialogue],
            };
            presets.Add(created);
            c.CurrentPresetId = created.Id;
            save();
            current = created;
        }

        ImGui.SameLine();
        if (Controls.Button("Delete preset", presets.Count > 1, "At least one preset must exist.") && current is not null)
        {
            presets.Remove(current);
            c.CurrentPresetId = presets[0].Id;
            save();
            current = presets[0];
        }

        return current;
    }

    private void DrawChannels(IList<int> enabled)
    {
        var save = ctx.Save;
        var (on, total) = ChannelSelection.Totals(enabled);
        var groups = ChannelNames.Search(this.search);
        var searching = this.search.Trim().Length > 0;

        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted(searching
            ? $"{on} of {total} on, {groups.Sum(g => g.Channels.Count)} shown"
            : $"{on} of {total} on");

        SearchBox.Draw("channels", ref this.search, "Search channels or categories", widthEm: 20f);

        if (groups.Count == 0)
        {
            if (EmptyState.Draw(FontAwesomeIcon.Search, "No channels match.", $"Nothing is named \"{this.search.Trim()}\". Try a shorter word.", "Clear search"))
            {
                this.search = string.Empty;
            }

            return;
        }

        // Open the matching groups once per change of the search, so the player can still close one.
        var reopen = searching && this.search != this.openedFor;
        this.openedFor = this.search;

        Ui.Gap(Ui.Space1);
        foreach (var (category, channels) in groups)
        {
            var shownOn = ChannelSelection.CountOn(channels, enabled);
            var all = ChannelSelection.AllOn([.. channels], enabled);

            // A search opens every matching group; otherwise only Speech starts open. The ##-suffix
            // pins the ImGui ID because the count in the caption changes with every tick.
            if (reopen)
            {
                ImGui.SetNextItemOpen(true, ImGuiCond.Always);
            }

            var flags = category == ChannelNames.CategoryOrder[0] ? ImGuiTreeNodeFlags.DefaultOpen : ImGuiTreeNodeFlags.None;
            var open = ImGui.CollapsingHeader($"{category}   {shownOn} of {channels.Count} on##cat-{category}", flags);
            Ui.Tooltip(ChannelNames.CategoryDescription(category));
            if (!open)
            {
                continue;
            }

            ImGui.PushID(category);
            Ui.MutedText(ChannelNames.CategoryDescription(category));
            var verb = all ? "Clear all" : "Select all";
            if (ImGui.Button(verb + (searching ? " shown" : string.Empty)))
            {
                ChannelSelection.SetAll(enabled, channels, !all);
                save();
            }

            Ui.Tooltip(all
                ? $"Turn off every {category} channel listed here."
                : $"Turn on every {category} channel listed here.");
            Ui.Gap(Ui.Space1);
            this.DrawChannelGrid(channels, enabled);
            Ui.Gap(Ui.Space1);
            ImGui.PopID();
        }
    }

    private void DrawChannelGrid(IReadOnlyList<ChannelNames.Channel> channels, IList<int> enabled)
    {
        var columns = Math.Clamp((int)(Ui.Fill() / Ui.Em(ChannelColumnEm)), 1, 4);
        if (!ImGui.BeginTable("##channels", columns))
        {
            return;
        }

        foreach (var channel in channels)
        {
            ImGui.TableNextColumn();
            var on = enabled.Contains(channel.Id);
            if (ImGui.Checkbox($"{channel.Name}##ch-{channel.Id}", ref on))
            {
                ChannelSelection.Set(enabled, channel.Id, on);
                ctx.Save();
            }
        }

        ImGui.EndTable();
    }
}
