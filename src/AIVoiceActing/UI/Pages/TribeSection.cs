namespace AIVoiceActing.UI.Pages;

using System.Globalization;
using AIVoiceActing.Domain;
using AIVoiceActing.Ports;
using AIVoiceActing.UI.Components;
using AIVoiceActing.UI.State;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;

/// <summary>
/// Beast tribes: one summary row per tribe (fingerprints of its voices, Edit), the inbox of creature
/// models heard this session that no tribe claims, and in the editor the three voice lists
/// (any gender, male, female) plus the creature models bound to the tribe. All edits go to the
/// shared unsaved casting edits.
/// </summary>
internal sealed class TribeSection(UiContext ctx, SlotEditor editor)
{
    private const float NameColumnEm = 14f;
    private const float EditColumnEm = 5f;
    private const float AssignWidthEm = 11f;
    private const float ModelIdWidthEm = 7f;
    private const float InboxModelEm = 5f;
    private const float InboxLinesEm = 4f;
    private const float InboxActionEm = 17f;
    private const int ModelIdMax = 12;

    private string bindBuffer = string.Empty;

    public void Draw()
    {
        var model = ctx.Casting.Model;
        Ui.MutedText(
            "Creatures such as moogles and sahagin share a voice per tribe. Models you meet that no tribe claims yet collect below, so you can assign them in one click.");
        this.DrawInbox(model);

        var rows = CastingSummary.TribeRows(model.EditBuffer);
        if (!ImGui.BeginTable("##tribes", 3, ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH))
        {
            return;
        }

        ImGui.TableSetupColumn("Tribe", ImGuiTableColumnFlags.WidthFixed, Ui.Em(NameColumnEm));
        ImGui.TableSetupColumn("Voices", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("##edit", ImGuiTableColumnFlags.WidthFixed, Ui.Em(EditColumnEm));
        ImGui.TableHeadersRow();
        foreach (var row in rows)
        {
            ImGui.PushID(row.Tribe.Key);
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.AlignTextToFramePadding();
            ImGui.TextUnformatted(row.Tribe.Name);
            ImGui.TableNextColumn();
            VoiceStrip.Draw(ctx, row.VoiceIds);
            ImGui.TableNextColumn();
            if (ImGui.Button("Edit##tribe"))
            {
                editor.Open(this.TargetFor(row.Tribe.Key, row.Tribe.Name));
            }

            ImGui.PopID();
        }

        ImGui.EndTable();
    }

    private EditTarget TargetFor(string tribeKey, string name)
    {
        var model = ctx.Casting.Model;
        SlotGroup Group(string heading, BeastVoiceList list, Func<BeastTribeCast, IReadOnlyList<VoiceSlotDto>> pick) => new(
            heading,
            () => model.EditBuffer.BeastTribes.FirstOrDefault(t => t.Key == tribeKey) is { } tribe ? pick(tribe) : [],
            (index, slot) => model.SetBeastVoice(tribeKey, list, index, slot),
            index => model.RemoveBeastVoice(tribeKey, list, index),
            voiceId => model.AddBeastVoice(tribeKey, list, voiceId));

        return new EditTarget(
            $"Voices for {name}",
            [
                Group("Any gender", BeastVoiceList.Pool, t => t.Voices),
                Group("Male voices", BeastVoiceList.Male, t => t.MaleVoices),
                Group("Female voices", BeastVoiceList.Female, t => t.FemaleVoices),
            ],
            () => this.DrawCreatureModels(tribeKey));
    }

    /// <summary>The creature models bound to a tribe: ids with remove buttons, plus a box to bind another.</summary>
    private void DrawCreatureModels(string tribeKey)
    {
        var model = ctx.Casting.Model;
        var tribe = model.EditBuffer.BeastTribes.FirstOrDefault(t => t.Key == tribeKey);
        if (tribe is null)
        {
            return;
        }

        Ui.Gap(Ui.Space2);
        using (Theme.PushText(Theme.Accent))
        {
            ImGui.TextUnformatted(Labels.CreatureModels);
        }

        if (tribe.ModelIds.Count == 0)
        {
            Ui.MutedTextInline("None yet. Models you meet are listed on the Voices page, ready to assign.");
        }

        foreach (var modelId in tribe.ModelIds)
        {
            ImGui.PushID(modelId);
            ImGui.AlignTextToFramePadding();
            ImGui.TextUnformatted(modelId.ToString(CultureInfo.InvariantCulture));
            ImGui.SameLine();
            if (IconButton.Draw("##unbind", FontAwesomeIcon.Trash, "Stop using this model for the tribe"))
            {
                model.UnbindModelId(tribeKey, modelId);
            }

            ImGui.PopID();
        }

        ImGui.SetNextItemWidth(Ui.Em(ModelIdWidthEm));
        var buffer = this.bindBuffer;
        if (ImGui.InputTextWithHint("##bind-model", "Model number", ref buffer, ModelIdMax))
        {
            this.bindBuffer = buffer;
        }

        ImGui.SameLine();
        if (ImGui.Button("Add model##bind"))
        {
            if (!int.TryParse(this.bindBuffer.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var modelId))
            {
                ctx.Report($"\"{this.bindBuffer}\" is not a model number (a whole number).", isError: true);
            }
            else
            {
                model.BindModelId(tribeKey, modelId);
                this.bindBuffer = string.Empty;
            }
        }
    }

    /// <summary>Creature models heard this session that no tribe claims: assign to a tribe, or ignore.</summary>
    private void DrawInbox(CastingTabModel model)
    {
        var heard = ctx.UnboundModels?.Entries ?? [];
        if (heard.Count == 0)
        {
            Ui.MutedTextInline("No unassigned creatures heard yet this session.");
            return;
        }

        ImGui.TextUnformatted($"Heard this session, not assigned to a tribe ({heard.Count})");
        if (!ImGui.BeginTable("##unbound", 4, ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH))
        {
            return;
        }

        ImGui.TableSetupColumn("Model", ImGuiTableColumnFlags.WidthFixed, Ui.Em(InboxModelEm));
        ImGui.TableSetupColumn("Last speaker", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("Lines", ImGuiTableColumnFlags.WidthFixed, Ui.Em(InboxLinesEm));
        ImGui.TableSetupColumn("##assign", ImGuiTableColumnFlags.WidthFixed, Ui.Em(InboxActionEm));
        ImGui.TableHeadersRow();
        foreach (var heardModel in heard)
        {
            ImGui.PushID(heardModel.ModelId);
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(heardModel.ModelId.ToString(CultureInfo.InvariantCulture));
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(heardModel.LastSpeaker);
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(heardModel.Lines.ToString(CultureInfo.InvariantCulture));
            ImGui.TableNextColumn();
            this.DrawAssign(model, heardModel.ModelId);
            ImGui.PopID();
        }

        ImGui.EndTable();
    }

    private void DrawAssign(CastingTabModel model, int modelId)
    {
        ImGui.SetNextItemWidth(Ui.Em(AssignWidthEm));
        if (ImGui.BeginCombo("##assign-tribe", "Assign to…"))
        {
            foreach (var tribe in model.EditBuffer.BeastTribes)
            {
                if (ImGui.Selectable(tribe.Name))
                {
                    model.BindModelId(tribe.Key, modelId);
                    ctx.UnboundModels!.Dismiss(modelId);
                    ctx.Status.Post(Severity.Info, $"Assigned model {modelId} to {tribe.Name}", "Not saved yet. Save it on the Voices page.");
                }
            }

            ImGui.EndCombo();
        }

        ImGui.SameLine();
        if (ImGui.Button("Ignore##unbound"))
        {
            ctx.UnboundModels!.Dismiss(modelId);
        }
    }
}
