namespace AIVoiceActing.UI.Pages;

using AIVoiceActing.Domain;
using AIVoiceActing.Ports;
using AIVoiceActing.UI.Components;
using AIVoiceActing.UI.State;
using Dalamud.Bindings.ImGui;

/// <summary>
/// "Use for...": the target chooser behind a library row. Pick where the voice goes (a race and
/// gender row, a beast tribe, a named NPC or a named player) and it is added there. Casting rows
/// and tribes take it into the unsaved casting edits (the Voices page shows Save); a named
/// character is stored at once.
/// </summary>
internal sealed class UseForChooser
{
    private const string ModalId = "use-voice-for";
    private const float ComboWidthEm = 24f;
    private const float NameWidthEm = 16f;
    private const float BodyWidthEm = 34f;
    private const int MaxNameLength = 100;

    private static readonly string[] Kinds = ["A race and gender", "A beast tribe", "An NPC", "A player"];

    private readonly UiContext ctx;
    private readonly VoiceEntryForm npcForm = new(player: false);
    private readonly VoiceEntryForm playerForm = new(player: true);
    private VoiceInfo? voice;
    private int kind;
    private int rowIndex;
    private int tribeIndex;

    public UseForChooser(UiContext ctx)
    {
        this.ctx = ctx;
        ctx.Modals.Register(ModalId, "Use this voice for", this.DrawBody);
    }

    /// <summary>Opens the chooser for <paramref name="chosen"/>.</summary>
    public void Open(VoiceInfo chosen)
    {
        this.voice = chosen;
        this.npcForm.Reset();
        this.playerForm.Reset();
        this.ctx.Modals.Open(ModalId);
    }

    private void DrawBody()
    {
        if (this.voice is not { } chosen)
        {
            ImGui.CloseCurrentPopup();
            return;
        }

        VoiceFingerprint.Draw(chosen.Id, playing: false, this.ctx.Config.ReduceMotion);
        ImGui.SameLine();
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted($"Use {chosen.DisplayName} for…");
        Ui.Gap(Ui.Space2);

        this.kind = SegmentedControl.Draw("use-for-kind", Kinds, this.kind);
        Ui.Gap(Ui.Space2);

        this.ctx.Casting.EnsureLoaded();
        var applied = this.kind switch
        {
            0 => this.DrawRaceRow(chosen),
            1 => this.DrawTribe(chosen),
            2 => this.DrawCharacter(chosen, this.npcForm, player: false),
            _ => this.DrawCharacter(chosen, this.playerForm, player: true),
        };

        Ui.Gap(Ui.Space2);
        if (ImGui.Button("Cancel##use-for") || applied)
        {
            this.ctx.Modals.Close(ModalId);
        }
    }

    private bool DrawRaceRow(VoiceInfo chosen)
    {
        var rows = CastingSummary.RaceRows(this.ctx.Casting.Model.EditBuffer);
        this.rowIndex = Math.Clamp(this.rowIndex, 0, rows.Count - 1);
        ImGui.SetNextItemWidth(Ui.Em(ComboWidthEm));
        if (ImGui.BeginCombo("##use-for-row", rows[this.rowIndex].Label))
        {
            for (var i = 0; i < rows.Count; i++)
            {
                if (ImGui.Selectable(rows[i].Label, i == this.rowIndex))
                {
                    this.rowIndex = i;
                }
            }

            ImGui.EndCombo();
        }

        Ui.MutedTextAt("New characters of this race and gender can pick it. Save on the Voices page keeps the change.", BodyWidthEm);
        if (!ImGui.Button("Add to this row##use-for"))
        {
            return false;
        }

        var row = rows[this.rowIndex];
        this.ctx.Casting.Model.AddVoice(row.Key, chosen.Id);
        this.ctx.Status.Post(Severity.Info, $"Added {chosen.DisplayName} to {row.Label}", "Not saved yet. Save it on the Voices page.");
        return true;
    }

    private bool DrawTribe(VoiceInfo chosen)
    {
        var tribes = this.ctx.Casting.Model.EditBuffer.BeastTribes;
        if (tribes.Count == 0)
        {
            Ui.MutedTextAt("No beast tribes are listed in this casting.", BodyWidthEm);
            return false;
        }

        this.tribeIndex = Math.Clamp(this.tribeIndex, 0, tribes.Count - 1);
        ImGui.SetNextItemWidth(Ui.Em(ComboWidthEm));
        if (ImGui.BeginCombo("##use-for-tribe", tribes[this.tribeIndex].Name))
        {
            for (var i = 0; i < tribes.Count; i++)
            {
                if (ImGui.Selectable(tribes[i].Name, i == this.tribeIndex))
                {
                    this.tribeIndex = i;
                }
            }

            ImGui.EndCombo();
        }

        Ui.MutedTextAt("Joins the voices every member of this tribe can have. Save on the Voices page keeps the change.", BodyWidthEm);
        if (!ImGui.Button("Add to this tribe##use-for"))
        {
            return false;
        }

        var tribe = tribes[this.tribeIndex];
        this.ctx.Casting.Model.AddBeastVoice(tribe.Key, BeastVoiceList.Pool, chosen.Id);
        this.ctx.Status.Post(Severity.Info, $"Added {chosen.DisplayName} to {tribe.Name}", "Not saved yet. Save it on the Voices page.");
        return true;
    }

    private bool DrawCharacter(VoiceInfo chosen, VoiceEntryForm form, bool player)
    {
        ImGui.SetNextItemWidth(Ui.Em(NameWidthEm));
        var name = form.Name;
        if (ImGui.InputTextWithHint("##use-for-name" + player, "Character name, as shown in chat", ref name, MaxNameLength))
        {
            form.Name = name;
        }

        if (player)
        {
            ImGui.SameLine();
            var world = form.World;
            WorldPicker.Draw(this.ctx.Worlds, "use-for", ref world);
            form.World = world;
        }

        var valid = form.TryBuildKey(out var key, out var error);
        var replaces = valid && this.ctx.Profiles.Entries.Any(p => p.SpeakerKey == key);
        if (!valid && (form.Name.Length > 0 || form.World.Length > 0) && error is not null)
        {
            using var colour = Theme.PushText(Theme.Error);
            ImGui.TextUnformatted(error);
        }
        else if (replaces)
        {
            Ui.MutedTextAt("This character already has a voice. It will be replaced.", BodyWidthEm);
        }

        if (!ActionButton.Primary("Use for this character##use-for", valid, "Type the character's name first."))
        {
            return false;
        }

        this.ctx.Profiles.SetOverride(key, chosen.Id, 0f, 1f);
        this.ctx.Status.Post(Severity.Success, $"{form.Name.Trim()} now speaks with {chosen.DisplayName}");
        form.Reset();
        return true;
    }
}
