namespace AIVoiceActing.UI.Pages;

using System.Numerics;
using AIVoiceActing.Domain;
using AIVoiceActing.Ports;
using AIVoiceActing.UI.Components;
using AIVoiceActing.UI.State;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;

/// <summary>
/// The characters who have their own voice: NPCs and players in one sortable, searchable table
/// with named headers. Each row shows who, which voice (a click opens the voice picker), the Tune
/// summary and preview / delete buttons. Deleting offers Undo; tuning commits once when the
/// slider is released; clearing a whole list asks first.
/// </summary>
internal sealed class CharactersSection
{
    private const float TableHeightEm = 18f;
    private const float SearchWidthEm = 16f;
    private const float NameWeight = 3f;
    private const float WorldWeight = 2f;
    private const float VoiceWeight = 3f;
    private const float TuneWeight = 3f;
    private const float ActionsColumnEm = 5f;
    private const float VoiceButtonEm = 9f;

    private static readonly string[] Kinds = ["NPCs", "Players"];

    private readonly UiContext ctx;
    private readonly CharacterTableModel[] models;
    private readonly CharacterAddForm[] addForms;
    private readonly string[] importModals = ["import-assignments-npc", "import-assignments-pc"];
    private readonly string[] importBuffers = [string.Empty, string.Empty];
    private readonly VoicePicker picker;
    private int kind;

    public CharactersSection(UiContext ctx)
    {
        this.ctx = ctx;
        this.picker = new VoicePicker(ctx, "characters");
        this.models =
        [
            this.NewModel(player: false),
            this.NewModel(player: true),
        ];
        this.addForms = [new CharacterAddForm(ctx, player: false), new CharacterAddForm(ctx, player: true)];
        for (var i = 0; i < this.importModals.Length; i++)
        {
            var index = i;
            ctx.Modals.Register(this.importModals[i], "Import voice assignments", () => this.DrawImportBody(index));
        }
    }

    public void Draw()
    {
        this.kind = SegmentedControl.Draw("characters-kind", Kinds, this.kind);
        var model = this.models[this.kind];
        Ui.Gap(Ui.Space1);

        this.DrawToolbar(model);
        if (this.addForms[this.kind].Draw(model.Stored))
        {
            model.Invalidate();
        }

        Ui.Gap(Ui.Space1);
        if (model.Empty is { } empty)
        {
            var icon = empty.Kind == ListEmptyKind.NoMatches ? FontAwesomeIcon.Search : FontAwesomeIcon.Users;
            if (EmptyState.Draw(icon, empty.Title, empty.Hint, empty.ActionLabel))
            {
                model.Filter = string.Empty;
            }
        }
        else
        {
            this.DrawTable(model);
        }

        this.picker.Draw();
    }

    private CharacterTableModel NewModel(bool player) => new(
        player,
        this.ctx.Profiles,
        this.ctx.Status,
        world => this.ctx.Worlds.Label(world),
        voiceId => VoiceNames.Display(this.ctx.VoiceLibrary, voiceId));

    private void DrawToolbar(CharacterTableModel model)
    {
        var filter = model.Filter;
        if (SearchBox.Draw("characters-" + this.kind, ref filter, "Search name, world or voice", SearchWidthEm))
        {
            model.Filter = filter;
        }

        ImGui.SameLine();
        ImGui.AlignTextToFramePadding();
        Ui.MutedTextInline(model.CountText);

        var stored = model.Stored;
        ImGui.SameLine();
        if (ActionButton.Secondary("Export##characters", stored.Count > 0, "Nobody to export yet."))
        {
            ImGui.SetClipboardText(CharacterVoicesModel.ShareText(stored));
            this.ctx.Report($"Copied {stored.Count} voice assignment(s) to the clipboard.", isError: false);
        }

        ImGui.SameLine();
        if (ImGui.Button("Import##characters"))
        {
            this.importBuffers[this.kind] = string.Empty;
            this.ctx.Modals.Open(this.importModals[this.kind]);
        }

        ImGui.SameLine();
        if (ActionButton.Secondary("Clear all##characters", stored.Count > 0, "Nobody to clear yet."))
        {
            var who = model.IsPlayer ? "players" : "NPCs";
            this.ctx.Confirm.Ask(
                $"Forget the voices of all {stored.Count} {who}? Each one picks a new voice the next time they speak.",
                "Clear all",
                () =>
                {
                    var count = model.RemoveAll();
                    this.ctx.Report($"Cleared {count} voice assignment(s). They pick new voices as they speak.", isError: false);
                });
        }
    }

    private void DrawTable(CharacterTableModel model)
    {
        var rows = model.Rows;
        var player = model.IsPlayer;
        var columns = player ? 5 : 4;
        var flags = ImGuiTableFlags.Sortable | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY | ImGuiTableFlags.BordersInnerH;
        if (!ImGui.BeginTable("##characters-" + this.kind, columns, flags, new Vector2(Ui.Fill(), Ui.Em(TableHeightEm))))
        {
            return;
        }

        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableSetupColumn("Name", ImGuiTableColumnFlags.WidthStretch | ImGuiTableColumnFlags.DefaultSort, NameWeight, (uint)CharacterColumn.Name + 1);
        if (player)
        {
            ImGui.TableSetupColumn("World", ImGuiTableColumnFlags.WidthStretch, WorldWeight, (uint)CharacterColumn.World + 1);
        }

        ImGui.TableSetupColumn("Voice", ImGuiTableColumnFlags.WidthStretch, VoiceWeight, (uint)CharacterColumn.Voice + 1);
        ImGui.TableSetupColumn("Tuning", ImGuiTableColumnFlags.WidthStretch | ImGuiTableColumnFlags.NoSort, TuneWeight);
        ImGui.TableSetupColumn("##actions", ImGuiTableColumnFlags.WidthFixed | ImGuiTableColumnFlags.NoSort, Ui.Em(ActionsColumnEm));
        ImGui.TableHeadersRow();
        this.ApplySort(model);

        string? removeKey = null;
        var clipper = ImGui.ImGuiListClipper();
        clipper.Begin(rows.Count);
        while (clipper.Step())
        {
            for (var i = clipper.DisplayStart; i < clipper.DisplayEnd; i++)
            {
                if (this.DrawRow(model, rows[i]))
                {
                    removeKey = rows[i].Profile.SpeakerKey;
                }
            }
        }

        clipper.End();
        clipper.Destroy();
        ImGui.EndTable();

        if (removeKey is not null)
        {
            model.Delete(removeKey);
        }
    }

    private void ApplySort(CharacterTableModel model)
    {
        var specs = ImGui.TableGetSortSpecs();
        if (specs.IsNull || !specs.SpecsDirty || specs.SpecsCount == 0)
        {
            return;
        }

        var first = specs.Specs;
        model.SortBy((CharacterColumn)(first.ColumnUserID - 1), first.SortDirection == ImGuiSortDirection.Ascending);
        specs.SpecsDirty = false;
    }

    /// <summary>Draws one row; returns true when its delete button was pressed.</summary>
    private bool DrawRow(CharacterTableModel model, CharacterRow row)
    {
        var profile = row.Profile;
        var edit = model.Effective(profile);
        ImGui.PushID(profile.SpeakerKey);
        ImGui.TableNextRow();

        ImGui.TableNextColumn();
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted(row.Key.Name);

        if (model.IsPlayer)
        {
            ImGui.TableNextColumn();
            ImGui.AlignTextToFramePadding();
            ImGui.TextUnformatted(row.WorldLabel);
        }

        ImGui.TableNextColumn();
        this.DrawVoiceCell(model, row, edit);

        ImGui.TableNextColumn();
        var tune = TunePopover.Draw("tune", TuneValues.From(edit));
        if (tune.Changed)
        {
            model.SetPending(profile.SpeakerKey, tune.Values.Apply(edit));
        }

        if (tune.Released)
        {
            model.CommitPending(profile.SpeakerKey);
        }

        ImGui.SameLine();
        var chips = TuneSummary.Chips(TuneValues.From(edit));
        Ui.MutedTextInline(Ui.Ellipsize(chips.Count == 0 ? "Natural" : string.Join(" · ", chips), ImGui.GetContentRegionAvail().X));

        ImGui.TableNextColumn();
        PreviewButton.Draw(
            "##preview",
            this.ctx.Preview,
            edit.VoiceId,
            this.ctx.VoicesNotReadyReason,
            () => this.ctx.PreviewVoice(edit.VoiceId, TuneValues.From(edit)));
        ImGui.SameLine();
        var remove = IconButton.Draw("##delete", FontAwesomeIcon.Trash, "Remove this character's voice. You can undo it for a few seconds.");

        ImGui.PopID();
        return remove;
    }

    private void DrawVoiceCell(CharacterTableModel model, CharacterRow row, CharacterEdit edit)
    {
        var missing = VoiceNames.IsMissing(this.ctx.VoiceLibrary, edit.VoiceId);
        VoiceFingerprint.Draw(edit.VoiceId, PreviewButton.IsActive(this.ctx.Preview, edit.VoiceId), this.ctx.Config.ReduceMotion, dim: missing);
        ImGui.SameLine();
        var label = row.VoiceName + (missing ? " (missing)" : string.Empty);
        if (ImGui.Button(Ui.Ellipsize(label, Ui.Em(VoiceButtonEm)) + "##voice"))
        {
            var key = row.Profile.SpeakerKey;
            this.picker.Open(
                $"Choose a voice for {row.Key.Name}",
                edit.VoiceId,
                id => model.Commit(key, model.Effective(row.Profile) with { VoiceId = id }));
        }

        Ui.Tooltip("Change the voice");
    }

    private void DrawImportBody(int index)
    {
        var prefix = this.models[index].KeyPrefix;
        ImGui.TextUnformatted("Paste an assignment share string (copied with Export):");
        ImGui.InputTextMultiline("##import-assignments-text", ref this.importBuffers[index], 64_000, Ui.Em(30f, 8f));
        if (ImGui.Button("Import##assignments"))
        {
            try
            {
                var summary = this.Import(prefix, CharacterVoicesModel.ParseShare(this.importBuffers[index]));
                this.models[index].Invalidate();
                this.ctx.Report(summary, isError: false);
                this.ctx.Modals.Close(this.importModals[index]);
            }
            catch (ShareCodecException e)
            {
                this.ctx.Report(e.Message, isError: true);
            }
        }

        ImGui.SameLine();
        if (ImGui.Button("Cancel##assignments"))
        {
            this.ctx.Modals.Close(this.importModals[index]);
        }
    }

    private string Import(string prefix, IReadOnlyList<AssignmentShare> shares)
    {
        var store = this.ctx.Profiles;
        var known = store.Entries.Select(e => e.SpeakerKey).ToHashSet(StringComparer.Ordinal);
        int added = 0, updated = 0, skipped = 0;
        foreach (var share in shares.Where(s => !string.IsNullOrEmpty(s.SpeakerKey)))
        {
            if (!share.SpeakerKey.StartsWith(prefix, StringComparison.Ordinal))
            {
                skipped++;
                continue;
            }

            if (known.Add(share.SpeakerKey))
            {
                added++;
            }
            else
            {
                updated++;
            }

            store.SetOverride(share.SpeakerKey, share.ReferenceVoiceId, share.ExaggerationBias, share.Volume, share.Pitch, share.Speed);
        }

        return $"Imported {added} new and {updated} updated voice assignment(s)"
            + (skipped > 0 ? $" ({skipped} skipped: wrong type for this list)." : ".");
    }
}
