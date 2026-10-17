namespace AIVoiceActing.UI.Pages;

using AIVoiceActing.Ports;
using AIVoiceActing.UI.Components;
using AIVoiceActing.UI.State;
using Dalamud.Bindings.ImGui;

/// <summary>
/// "Who sounds like who": how voices are handed out. The auto-casting switches, then the race and
/// gender rows summarised with their fingerprints and an Edit button, the beast tribes, and the
/// characters who have a voice of their own. Edits to rows and tribes are held as unsaved changes
/// behind a Save bar; character edits apply at once (with Undo on delete).
/// </summary>
internal sealed class WhoSoundsSection
{
    private const float WhoColumnEm = 12f;
    private const float EditColumnEm = 5f;
    private const string PresetsAnchor = "casting-presets";

    private readonly UiContext ctx;
    private readonly SlotEditor editor;
    private readonly TribeSection tribes;
    private readonly CharactersSection characters;
    private CastingPreset? rowsFor;
    private IReadOnlyList<CastingRowInfo> rows = [];

    public WhoSoundsSection(UiContext ctx)
    {
        this.ctx = ctx;
        this.editor = new SlotEditor(ctx);
        this.tribes = new TribeSection(ctx, this.editor);
        this.characters = new CharactersSection(ctx);
    }

    public void Draw()
    {
        SectionHeader.Draw("Who sounds like who");
        this.ctx.Casting.EnsureLoaded();
        this.DrawAutoCasting();
        this.DrawDraftBar();

        if (ImGui.CollapsingHeader("Race and gender##who-race", ImGuiTreeNodeFlags.DefaultOpen))
        {
            this.DrawRaceRows();
        }

        if (ImGui.CollapsingHeader("Beast tribes##who-tribes"))
        {
            this.tribes.Draw();
        }

        if (ImGui.CollapsingHeader("Characters with their own voice##who-characters", ImGuiTreeNodeFlags.DefaultOpen))
        {
            this.characters.Draw();
        }

        Ui.Gap(Ui.Space2);
        if (ImGui.Button("Manage casting presets in Settings##who-presets"))
        {
            this.ctx.Nav.OpenAt(NavTab.Settings, PresetsAnchor);
        }

        Ui.Gap(Ui.Space3);
    }

    private void DrawAutoCasting()
    {
        var c = this.ctx.Config;
        Ui.MutedText(
            "The first time someone speaks, the plugin gives them a voice that fits their race and gender and remembers it. "
            + "Change the voices for a whole group below, or give one character a voice of their own.");
        Ui.Gap(Ui.Space1);

        Controls.Checkbox(
            "Choose voices automatically by race and gender",
            () => c.UseRaceVoicePresets,
            v =>
            {
                c.UseRaceVoicePresets = v;
                this.ctx.Save();
            });
        Ui.MutedText("Off: everyone without a voice of their own gets the \"Anyone else\" voices.");

        Controls.Checkbox(
            "Make children and elders sound their age",
            () => c.AgeAwareCasting,
            v =>
            {
                c.AgeAwareCasting = v;
                this.ctx.Save();
            });
        Ui.MutedText("Children speak a little higher and quicker, elders a little lower and slower. Voices you set by hand never change.");
    }

    /// <summary>The unsaved-changes bar: Save keeps the edits, Discard drops them. Visible whenever there are edits.</summary>
    private void DrawDraftBar()
    {
        var draft = this.ctx.Casting;
        if (!draft.IsActive && !draft.Dirty)
        {
            Ui.MutedText($"You are editing \"{draft.Name}\", which is not the casting in use (\"{draft.ActiveName}\"). Switch in Settings.");
        }

        if (!draft.Dirty)
        {
            return;
        }

        using var bar = Card.Begin("casting-unsaved", Theme.WithAlpha(Theme.Warn, 0.14f));
        if (!bar.Visible)
        {
            return;
        }

        StatusPill.Draw(Severity.Warning, "You have unsaved voice changes.");
        if (draft.Name == CastingPreset.DefaultPresetName)
        {
            Ui.MutedText($"The built-in casting cannot be changed, so saving keeps your changes as a new casting called \"{CastingDraft.UniqueName(this.ctx.PresetStore().PresetNames)}\".");
        }

        Ui.Gap(Ui.Space1);
        if (Theme.PrimaryButton("Save##casting"))
        {
            this.Save();
        }

        ImGui.SameLine();
        if (ImGui.Button("Discard##casting"))
        {
            draft.Discard();
            this.ctx.Status.Post(Severity.Info, "Discarded your unsaved voice changes");
        }
    }

    private void Save()
    {
        try
        {
            var outcome = this.ctx.Casting.Save();
            this.ctx.Report(
                outcome.CopiedFromDefault
                    ? $"Saved as \"{outcome.Name}\" and switched to it."
                    : $"Saved \"{outcome.Name}\".",
                isError: false);
        }
        catch (CastingPresetException e)
        {
            this.ctx.Report(e.Message, isError: true);
        }
    }

    private void DrawRaceRows()
    {
        var buffer = this.ctx.Casting.Model.EditBuffer;
        if (!ReferenceEquals(buffer, this.rowsFor))
        {
            this.rows = CastingSummary.RaceRows(buffer);
            this.rowsFor = buffer;
        }

        if (!ImGui.BeginTable("##who-rows", 3, ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH))
        {
            return;
        }

        ImGui.TableSetupColumn("Who", ImGuiTableColumnFlags.WidthFixed, Ui.Em(WhoColumnEm));
        ImGui.TableSetupColumn("Voices", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("##edit", ImGuiTableColumnFlags.WidthFixed, Ui.Em(EditColumnEm));
        ImGui.TableHeadersRow();
        foreach (var row in this.rows)
        {
            ImGui.PushID(row.Key);
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.AlignTextToFramePadding();
            ImGui.TextUnformatted(row.Label);
            ImGui.TableNextColumn();
            VoiceStrip.Draw(this.ctx, row.VoiceIds);
            ImGui.TableNextColumn();
            if (ImGui.Button("Edit##row"))
            {
                this.editor.Open(this.TargetFor(row));
            }

            ImGui.PopID();
        }

        ImGui.EndTable();
        Ui.MutedText("A row with no voices uses the \"Anyone else\" voices.");
    }

    private EditTarget TargetFor(CastingRowInfo row)
    {
        var model = this.ctx.Casting.Model;
        var key = row.Key;
        var group = new SlotGroup(
            string.Empty,
            () => model.EditBuffer.Buckets.TryGetValue(key, out var slots) ? slots : [],
            (index, slot) => model.SetSlot(key, index, slot),
            index => model.RemoveVoice(key, index),
            voiceId => model.AddVoice(key, voiceId));
        return new EditTarget($"Voices for {row.Label}", [group]);
    }
}
