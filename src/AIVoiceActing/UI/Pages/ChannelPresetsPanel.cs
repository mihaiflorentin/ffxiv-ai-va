namespace AIVoiceActing.UI.Pages;

using AIVoiceActing.UI.Components;
using AIVoiceActing.UI.State;
using Dalamud.Bindings.ImGui;

/// <summary>Chat-channel presets: pick/create/delete a preset, its hold-to-talk keybind, and the per-category channel checkboxes.</summary>
internal sealed class ChannelPresetsPanel(UiContext ctx)
{
    public void Draw()
    {
        var c = ctx.Config;
        var save = ctx.Save;
        var presets = c.EnabledChatTypesPresets;
        var current = c.CurrentPreset ?? presets.FirstOrDefault();
        if (current is not null && current.Id != c.CurrentPresetId)
        {
            c.CurrentPresetId = current.Id;
            save(); // auto-heal: persist the corrected pointer immediately
        }

        var names = presets
            .Select(p => p.Name ?? $"#{p.Id}")
            .ToArray();
        var index = Math.Max(0, presets.IndexOf(current!));
        if (names.Length > 0 && ImGui.Combo("Preset", ref index, names, names.Length))
        {
            c.CurrentPresetId = presets[index].Id;
            save();
            current = presets[index];
        }

        ImGui.SameLine();
        if (ImGui.Button("New Preset"))
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
        if (Controls.Button("Delete", presets.Count > 1, "At least one preset must exist.") && current is not null)
        {
            presets.Remove(current);
            c.CurrentPresetId = presets[0].Id;
            save();
            current = presets[0];
        }

        ImGui.TextDisabled($"Current preset: {current?.Name ?? "—"}");

        if (current is { } preset)
        {
            DrawPreset(preset);
        }
    }

    private void DrawPreset(EnabledChatTypesPreset preset)
    {
        var save = ctx.Save;
        var name = preset.Name ?? string.Empty;
        if (ImGui.InputText("##preset-name", ref name, 64))
        {
            preset.Name = name;
            if (ImGui.IsItemDeactivatedAfterEdit())
            {
                save();
            }
        }

        ImGui.SameLine();
        Controls.Checkbox("Enable all chat types", () => preset.EnableAllChatTypes, v =>
        {
            preset.EnableAllChatTypes = v;
            save();
        }, "Read every chat channel, ignoring the list below — quick setup without ticking dozens of boxes.");

        Controls.Checkbox("Use keybind for this preset", () => preset.UseKeybind, v =>
        {
            preset.UseKeybind = v;
            save();
        }, "Read channels only while holding the keys below — a hold-to-talk gate for this preset.");
        if (preset.UseKeybind)
        {
            ImGui.Indent();
            Controls.KeyCombo("Modifier", KeyOptions.Modifiers, () => preset.ModifierKey, v => { preset.ModifierKey = v; save(); }, "First key of this preset's hold-to-talk combination.");
            Controls.KeyCombo("Key", KeyOptions.MajorKeys, () => preset.MajorKey, v => { preset.MajorKey = v; save(); }, "Second key of this preset's hold-to-talk combination.");
            ImGui.Unindent();
        }

        if (preset.EnableAllChatTypes)
        {
            return;
        }

        preset.EnabledChatTypes ??= new List<int>();
        var enabled = preset.EnabledChatTypes;
        ImGui.TextWrapped("Channels this preset reads, grouped by category:");
        ImGui.Spacing();
        foreach (var (category, channels) in ChannelNames.ByCategory())
        {
            var enabledCount = channels.Count(ch => enabled.Contains(ch.Id));
            // The ##-suffix pins the ImGui ID: the displayed count changes with
            // every tick, and an ID derived from it would collapse the section.
            var header = $"{category} ({enabledCount}/{channels.Count})##cat-{category}";
            if (!ImGui.CollapsingHeader(header))
            {
                Controls.Tooltip(ChannelNames.CategoryDescription(category));
                continue;
            }

            Controls.Tooltip(ChannelNames.CategoryDescription(category));

            var all = enabledCount == channels.Count;
            if (Controls.Button(all ? "none" : "all", true, all ? "Uncheck every channel in this category." : "Check every channel in this category."))
            {
                if (all)
                {
                    preset.EnabledChatTypes = enabled
                        .Where(id => channels.All(ch => ch.Id != id))
                        .ToList();
                }
                else
                {
                    foreach (var ch in channels.Where(ch => !enabled.Contains(ch.Id)))
                    {
                        enabled.Add(ch.Id);
                    }
                }

                save();
            }

            ImGui.SameLine();
            for (var i = 0; i < channels.Count; i++)
            {
                var channel = channels[i];
                if (i % 3 != 0)
                {
                    ImGui.SameLine();
                }

                var on = enabled.Contains(channel.Id);
                if (ImGui.Checkbox(channel.Name, ref on))
                {
                    if (on)
                    {
                        enabled.Add(channel.Id);
                    }
                    else
                    {
                        enabled.Remove(channel.Id);
                    }

                    save();
                }
            }
        }
    }
}
