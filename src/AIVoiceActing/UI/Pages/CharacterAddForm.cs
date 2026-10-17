namespace AIVoiceActing.UI.Pages;

using AIVoiceActing.Domain;
using AIVoiceActing.Ports;
using AIVoiceActing.UI.Components;
using AIVoiceActing.UI.State;
using Dalamud.Bindings.ImGui;

/// <summary>The manual-entry form above a characters table: name (plus world for players) and a
/// New button; duplicates surface through the form's validation line.</summary>
internal sealed class CharacterAddForm(UiContext ctx, bool player)
{
    private readonly VoiceEntryForm form = new(player);

    public void Draw(IReadOnlyCollection<VoiceProfile> entries)
    {
        var existing = entries.Select(e => e.SpeakerKey).ToHashSet(StringComparer.Ordinal);
        ImGui.SetNextItemWidth(Ui.Em(12));
        var name = this.form.Name;
        if (ImGui.InputTextWithHint(player ? "##add-player-name" : "##add-npc-name", "Name", ref name, 100))
        {
            this.form.Name = name;
        }

        Controls.Tooltip("The character's name exactly as it appears in chat.");

        if (player)
        {
            ImGui.SameLine();
            this.DrawWorldPicker();
        }

        ImGui.SameLine();
        if (Controls.Button("New", enabled: true)
            && this.form.TryBuildKey(out var key, out _)
            && !existing.Contains(key)) // duplicates surface via the validation line below
        {
            ctx.Profiles.SetOverride(key, this.DefaultNewVoiceId(), 0f, 1f);
            this.form.Reset();
        }

        var error = this.form.Validate(existing);
        if (error is not null && (this.form.Name.Length > 0 || this.form.World.Length > 0))
        {
            using var colour = Theme.PushText(Theme.Error);
            ImGui.TextUnformatted(error);
        }
    }

    private void DrawWorldPicker()
    {
        var worldList = ctx.Worlds.Sorted();
        if (worldList.Count > 0)
        {
            ImGui.SetNextItemWidth(Ui.Em(9));
            var selected = uint.TryParse(this.form.World, out var selectedId) ? ctx.Worlds.Label(selectedId) : "World";
            if (ImGui.BeginCombo("##add-player-world", selected))
            {
                foreach (var (id, worldName) in worldList)
                {
                    if (ImGui.Selectable(worldName, this.form.World == id.ToString()))
                    {
                        this.form.World = id.ToString();
                    }
                }

                ImGui.EndCombo();
            }

            Controls.Tooltip("The character's home world, to tell same-named characters apart.");
        }
        else
        {
            ImGui.SetNextItemWidth(Ui.Em(5));
            var world = this.form.World;
            if (ImGui.InputTextWithHint("##add-player-world", "World id", ref world, 16))
            {
                this.form.World = world;
            }

            Controls.Tooltip("Numeric world id distinguishing same-named characters across worlds.");
        }
    }

    /// <summary>The voice a new override starts on: the first slot of the Unknown row
    /// (the same pool unlisted speakers pick from), or a safe fallback.</summary>
    private string DefaultNewVoiceId()
    {
        var slots = ctx.VoiceMap().SlotsFor(VoiceGroup.Ungendered, null);
        return slots.Length > 0 ? slots[0].Id : "default";
    }
}
