namespace AIVoiceActing.UI.Pages;

using AIVoiceActing.Ports;
using AIVoiceActing.UI.Components;
using AIVoiceActing.UI.State;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;

/// <summary>One list of voices inside the editor: where it reads from and how it changes the casting edits.</summary>
internal sealed record SlotGroup(
    string Heading,
    Func<IReadOnlyList<VoiceSlotDto>> Slots,
    Action<int, VoiceSlotDto> Set,
    Action<int> Remove,
    Action<string> Add);

/// <summary>What the editor shows: a title, one or more voice lists and optional extra controls (a tribe's creature models).</summary>
internal sealed record EditTarget(string Title, IReadOnlyList<SlotGroup> Groups, Action? Extra = null);

/// <summary>
/// The casting editor modal behind each "Edit" button: the voices of one race and gender row (or
/// one beast tribe) with a fingerprint, a voice button that opens the picker, a Tune popover and
/// its summary chips, preview and remove. Every change goes into the unsaved casting edits; the
/// Voices page shows the Save bar.
/// </summary>
internal sealed class SlotEditor
{
    private const string ModalId = "edit-voices";
    private const float VoiceColumnEm = 15f;
    private const float TuningColumnEm = 24f;
    private const float ActionsColumnEm = 5f;
    private const float VoiceButtonEm = 9f;
    private const float BodyWidthEm = 44f;

    private readonly UiContext ctx;
    private readonly VoicePicker picker;
    private EditTarget? target;

    public SlotEditor(UiContext ctx)
    {
        this.ctx = ctx;
        this.picker = new VoicePicker(ctx, "slots");
        ctx.Modals.Register(ModalId, "Edit voices", this.DrawBody, () => this.picker.IsOpen);
    }

    public void Open(EditTarget editTarget)
    {
        this.target = editTarget;
        this.ctx.Modals.Open(ModalId);
    }

    private void DrawBody()
    {
        if (this.target is not { } current)
        {
            ImGui.CloseCurrentPopup();
            return;
        }

        ImGui.TextUnformatted(current.Title);
        Ui.MutedTextAt("A new character of this kind gets one of these at random the first time they speak, and keeps it.", BodyWidthEm);
        Ui.Gap(Ui.Space2);

        foreach (var group in current.Groups)
        {
            this.DrawGroup(group);
        }

        current.Extra?.Invoke();

        Ui.Gap(Ui.Space2);
        if (this.ctx.Casting.Dirty)
        {
            StatusPill.Draw(Severity.Info, "Changes are not saved yet. Save them on the Voices page.");
            Ui.Gap(Ui.Space1);
        }

        if (Theme.PrimaryButton("Done##edit-voices"))
        {
            this.ctx.Modals.Close(ModalId);
        }

        this.picker.Draw();
    }

    private void DrawGroup(SlotGroup group)
    {
        ImGui.PushID(group.Heading);
        if (group.Heading.Length > 0)
        {
            Ui.Gap(Ui.Space1);
            using var accent = Theme.PushText(Theme.Accent);
            ImGui.TextUnformatted(group.Heading);
        }

        var slots = group.Slots();
        var removeAt = -1;
        if (slots.Count == 0)
        {
            Ui.MutedTextInline("No voices set. Speakers fall back to the next row down.");
        }
        else if (ImGui.BeginTable("##slots", 3, ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH))
        {
            ImGui.TableSetupColumn("Voice", ImGuiTableColumnFlags.WidthFixed, Ui.Em(VoiceColumnEm));
            ImGui.TableSetupColumn("Tuning", ImGuiTableColumnFlags.WidthFixed, Ui.Em(TuningColumnEm));
            ImGui.TableSetupColumn("##actions", ImGuiTableColumnFlags.WidthFixed, Ui.Em(ActionsColumnEm));
            ImGui.TableHeadersRow();
            for (var i = 0; i < slots.Count; i++)
            {
                if (this.DrawSlot(group, i, slots[i]))
                {
                    removeAt = i;
                }
            }

            ImGui.EndTable();
        }

        if (removeAt >= 0)
        {
            group.Remove(removeAt);
        }

        if (ImGui.Button("Add a voice##add"))
        {
            this.picker.Open("Add a voice", null, group.Add);
        }

        ImGui.PopID();
    }

    /// <summary>Draws one slot row; returns true when its remove button was pressed.</summary>
    private bool DrawSlot(SlotGroup group, int index, VoiceSlotDto slot)
    {
        ImGui.PushID(index);
        ImGui.TableNextRow();

        ImGui.TableNextColumn();
        var missing = VoiceNames.IsMissing(this.ctx.VoiceLibrary, slot.Id);
        VoiceFingerprint.Draw(slot.Id, PreviewButton.IsActive(this.ctx.Preview, slot.Id), this.ctx.Config.ReduceMotion, dim: missing);
        ImGui.SameLine();
        var name = VoiceNames.Display(this.ctx.VoiceLibrary, slot.Id) + (missing ? " (missing)" : string.Empty);
        if (ImGui.Button(Ui.Ellipsize(name, Ui.Em(VoiceButtonEm)) + "##voice"))
        {
            this.picker.Open("Choose a voice", slot.Id, picked => group.Set(index, slot with { Id = picked }));
        }

        Ui.Tooltip("Change the voice");

        ImGui.TableNextColumn();
        var tune = TunePopover.Draw("tune", TuneValues.From(slot));
        if (tune.Changed)
        {
            group.Set(index, tune.Values.Apply(slot));
        }

        ImGui.SameLine();
        var chips = TuneSummary.Chips(TuneValues.From(slot));
        Ui.MutedTextInline(Ui.Ellipsize(chips.Count == 0 ? "Natural" : string.Join(" · ", chips), ImGui.GetContentRegionAvail().X));

        ImGui.TableNextColumn();
        PreviewButton.Draw(
            "##preview",
            this.ctx.Preview,
            slot.Id,
            this.ctx.VoicesNotReadyReason,
            () => this.ctx.PreviewVoice(slot.Id, TuneValues.From(slot)));
        ImGui.SameLine();
        var remove = IconButton.Draw("##remove", FontAwesomeIcon.Trash, "Remove this voice from the row");

        ImGui.PopID();
        return remove;
    }
}
