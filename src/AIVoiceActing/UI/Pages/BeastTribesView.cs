namespace AIVoiceActing.UI.Pages;

using System.Globalization;
using AIVoiceActing.Domain;
using AIVoiceActing.Ports;
using AIVoiceActing.UI.Components;
using AIVoiceActing.UI.State;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;

/// <summary>
/// Beast tribes of the casting editor: the inbox of creature models heard this session with no
/// tribe, one collapsing section per tribe (pool, male and female voice lists), and the
/// model-id bindings. Edits go to the shared casting edit buffer; Save persists them.
/// </summary>
internal sealed class BeastTribesView(UiContext ctx, CastingTabModel casting, VoiceSlotList slotList)
{
    private string bindModelIdBuffer = string.Empty;

    public void Draw(IReadOnlyList<VoiceCatalogEntry> catalog, string[] rawIds)
    {
        Controls.Section("Beast tribes");
        ImGui.TextWrapped(
            "Tribe voices serve every member; male/female lists override the pool once filled. "
            + "Models you hear that no tribe claims collect just below; assign them in one click.");
        this.DrawUnboundModels();
        foreach (var tribe in casting.EditBuffer.BeastTribes)
        {
            if (!ImGui.CollapsingHeader($"{tribe.Name}###tribe-{tribe.Key}"))
            {
                continue;
            }

            ImGui.PushID($"tribe-{tribe.Key}");
            this.DrawTribeList("Pool (unknown gender)", tribe, BeastVoiceList.Pool, tribe.Voices, catalog, rawIds);
            this.DrawTribeList("Male voices", tribe, BeastVoiceList.Male, tribe.MaleVoices, catalog, rawIds);
            this.DrawTribeList("Female voices", tribe, BeastVoiceList.Female, tribe.FemaleVoices, catalog, rawIds);
            this.DrawTribeBindings(tribe);
            ImGui.PopID();
        }
    }

    /// <summary>One beast-tribe voice list (pool or gendered), edited through the
    /// tribe mutators so the list type rides along.</summary>
    private void DrawTribeList(string label, BeastTribeCast tribe, BeastVoiceList list, IReadOnlyList<VoiceSlotDto> slots, IReadOnlyList<VoiceCatalogEntry> catalog, string[] rawIds)
    {
        if (!ImGui.CollapsingHeader($"{label} ({slots.Count} voices)"))
        {
            return;
        }

        slotList.Draw(
            CastingDefaults.BeastBucketKey(tribe.Key, list),
            slots,
            catalog,
            rawIds,
            (key, index, slot) => casting.SetBeastVoice(tribe.Key, list, index, slot),
            (key, index) => casting.RemoveBeastVoice(tribe.Key, list, index),
            (key, voiceId) => casting.AddBeastVoice(tribe.Key, list, voiceId));
    }

    /// <summary>
    /// Inbox of non-human models heard this session with no tribe binding: each row names
    /// the last speaker so the model is recognisable, and Assign binds it to the chosen
    /// tribe in the edit buffer (Save persists it, as with typed bindings).
    /// </summary>
    private void DrawUnboundModels()
    {
        var heard = ctx.UnboundModels?.Entries ?? [];
        if (heard.Count == 0)
        {
            ImGui.TextDisabled("No unassigned creature models heard yet this session.");
            return;
        }

        ImGui.TextUnformatted($"Heard this session, not assigned to a tribe ({heard.Count}):");
        var tribes = casting.EditBuffer.BeastTribes;
        if (!ImGui.BeginTable("##unbound-models", 4, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg))
        {
            return;
        }

        ImGui.TableSetupColumn("Model", ImGuiTableColumnFlags.WidthFixed, Ui.Em(4));
        ImGui.TableSetupColumn("Last speaker", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("Lines", ImGuiTableColumnFlags.WidthFixed, Ui.Em(3));
        ImGui.TableSetupColumn("##assign", ImGuiTableColumnFlags.WidthFixed, Ui.Em(15));
        ImGui.TableHeadersRow();
        foreach (var model in heard)
        {
            ImGui.PushID($"unbound-{model.ModelId}");
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(model.ModelId.ToString(CultureInfo.InvariantCulture));
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(model.LastSpeaker);
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(model.Lines.ToString(CultureInfo.InvariantCulture));
            ImGui.TableNextColumn();
            ImGui.SetNextItemWidth(Ui.Em(10));
            if (ImGui.BeginCombo("##tribe", "Assign to…"))
            {
                foreach (var tribe in tribes)
                {
                    if (ImGui.Selectable(tribe.Name))
                    {
                        casting.BindModelId(tribe.Key, model.ModelId);
                        ctx.UnboundModels!.Dismiss(model.ModelId);
                        ctx.Report($"Model {model.ModelId} assigned to {tribe.Name}; Save the preset to keep it.", isError: false);
                    }
                }

                ImGui.EndCombo();
            }

            ImGui.SameLine();
            if (ImGui.SmallButton("Ignore"))
            {
                ctx.UnboundModels!.Dismiss(model.ModelId);
            }

            ImGui.PopID();
        }

        ImGui.EndTable();
    }

    /// <summary>Model-id binding row for one tribe: bound ids as trash chips plus the
    /// id input + Bind button. Bindings live on the preset and persist with Save.</summary>
    private void DrawTribeBindings(BeastTribeCast tribe)
    {
        ImGui.TextDisabled("Model-id bindings:");
        foreach (var modelId in tribe.ModelIds)
        {
            ImGui.PushID($"bind-{modelId}");
            ImGui.TextUnformatted($"{modelId}");
            ImGui.SameLine();
            if (Controls.IconButton(FontAwesomeIcon.Trash, "unbind", "Remove this model-id binding."))
            {
                casting.UnbindModelId(tribe.Key, modelId);
            }

            ImGui.PopID();
            ImGui.SameLine();
        }

        ImGui.NewLine();
        ImGui.SetNextItemWidth(Ui.Em(6));
        var buffer = this.bindModelIdBuffer;
        if (ImGui.InputTextWithHint("##bind-model", "Model id", ref buffer, 12))
        {
            this.bindModelIdBuffer = buffer;
        }

        ImGui.SameLine();
        if (ImGui.SmallButton("Bind"))
        {
            if (!int.TryParse(this.bindModelIdBuffer.Trim(), out var modelId))
            {
                ctx.Report($"\"{this.bindModelIdBuffer}\" is not a model id (whole number).", isError: true);
            }
            else
            {
                casting.BindModelId(tribe.Key, modelId);
                this.bindModelIdBuffer = string.Empty;
            }
        }
    }
}
