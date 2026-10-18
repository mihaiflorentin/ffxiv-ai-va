namespace AIVoiceActing.UI.Pages;

using AIVoiceActing.Domain;
using AIVoiceActing.Ports;
using AIVoiceActing.UI.Components;
using AIVoiceActing.UI.State;
using Dalamud.Bindings.ImGui;

/// <summary>The manual-entry form above a characters table: name (plus world for players) and an
/// Add button; duplicates surface through the form's validation line.</summary>
internal sealed class CharacterAddForm(UiContext ctx, bool player)
{
    private const float NameWidthEm = 12f;
    private const int MaxNameLength = 100;

    private readonly VoiceEntryForm form = new(player);

    /// <summary>Draws the form; returns true after it added a character.</summary>
    public bool Draw(IReadOnlyCollection<VoiceProfile> entries)
    {
        var added = false;
        var existing = entries.Select(e => e.SpeakerKey).ToHashSet(StringComparer.Ordinal);
        ImGui.SetNextItemWidth(Ui.Em(NameWidthEm));
        var name = this.form.Name;
        if (ImGui.InputTextWithHint(player ? "##add-player-name" : "##add-npc-name", "Add by name", ref name, MaxNameLength))
        {
            this.form.Name = name;
        }

        Ui.Tooltip("The character's name exactly as it appears in chat.");

        if (player)
        {
            ImGui.SameLine();
            var world = this.form.World;
            WorldPicker.Draw(ctx.Worlds, "add-player", ref world);
            this.form.World = world;
        }

        ImGui.SameLine();
        if (ImGui.Button("Add##add-character")
            && this.form.TryBuildKey(out var key, out _)
            && !existing.Contains(key)) // duplicates surface via the validation line below
        {
            ctx.Profiles.SetOverride(key, this.DefaultNewVoiceId(), 0f, 1f);
            this.form.Reset();
            added = true;
        }

        var error = this.form.Validate(existing);
        if (error is not null && (this.form.Name.Length > 0 || this.form.World.Length > 0))
        {
            using var colour = Theme.PushText(Theme.Error);
            ImGui.TextUnformatted(error);
        }

        return added;
    }

    /// <summary>The voice a new override starts on: the first slot of the Unknown row
    /// (the same pool unlisted speakers pick from), or a safe fallback.</summary>
    private string DefaultNewVoiceId()
    {
        var slots = ctx.VoiceMap().SlotsFor(VoiceGroup.Ungendered, null);
        return slots.Length > 0 ? slots[0].Id : "default";
    }
}
