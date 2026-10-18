namespace AIVoiceActing.UI.Pages;

using AIVoiceActing.Domain;
using AIVoiceActing.Ports;
using AIVoiceActing.UI.Components;
using AIVoiceActing.UI.State;
using Dalamud.Bindings.ImGui;

/// <summary>
/// Advanced > Casting presets: pick, use, duplicate, start fresh, delete, export and import named
/// castings. It shares the casting edits with the Voices page, so <see cref="CastingDraftGuard"/>
/// blocks every change of casting while there are unsaved voice edits (the reason sits next to the
/// buttons as text), deleting asks first, and importing over a casting with the same name asks
/// before it replaces it. Duplicate, New and Import run in the shared modal host.
/// </summary>
internal sealed class CastingPresetsCard
{
    private const string DuplicateModal = "duplicate-preset";
    private const string NewModal = "new-preset";
    private const string ImportModal = "import-preset";
    private const float ComboWidthEm = 16f;
    private const float NameWidthEm = 20f;
    private const float ImportWidthEm = 30f;
    private const float ImportHeightEm = 8f;
    private const float BodyWidthEm = 30f;
    private const int NameMax = 64;
    private const int ImportMax = 64_000;

    private readonly UiContext ctx;
    private string duplicateName = string.Empty;
    private string newName = string.Empty;
    private string importText = string.Empty;
    private string? dialogProblem;

    public CastingPresetsCard(UiContext ctx)
    {
        this.ctx = ctx;
        ctx.Modals.Register(DuplicateModal, "Duplicate casting", this.DrawDuplicateBody);
        ctx.Modals.Register(NewModal, "New casting from scratch", this.DrawNewBody);
        ctx.Modals.Register(ImportModal, "Import casting", this.DrawImportBody);
    }

    private CastingGuardState GuardState
    {
        get
        {
            var draft = this.ctx.Casting;
            return new CastingGuardState(draft.Dirty, draft.Model.SelectedName, draft.ActiveName);
        }
    }

    public void Draw()
    {
        var draft = this.ctx.Casting;
        draft.EnsureLoaded();
        var store = this.ctx.PresetStore();
        var model = draft.Model;
        var names = store.PresetNames as string[] ?? [.. store.PresetNames];
        var isDefault = model.SelectedName == CastingPreset.DefaultPresetName;

        Ui.MutedText("A casting is the set of voices used for each race, gender and beast tribe. The Voices tab edits the one you pick here.");
        var canSwitch = CastingDraftGuard.Check(PresetAction.Select, this.GuardState);
        ImGui.BeginDisabled(!canSwitch.IsAllowed);
        var selected = Array.IndexOf(names, model.SelectedName);
        ImGui.SetNextItemWidth(Ui.Em(ComboWidthEm));
        if (ImGui.Combo("##preset", ref selected, names, names.Length))
        {
            draft.Select(names[Math.Clamp(selected, 0, names.Length - 1)]);
        }

        ImGui.EndDisabled();
        ImGui.SameLine();
        ImGui.AlignTextToFramePadding();
        Ui.MutedTextInline(draft.IsActive ? "In use" : $"Not in use (using \"{draft.ActiveName}\")");
        if (canSwitch.Message is { } blocked)
        {
            StatusPill.Draw(Severity.Warning, blocked);
        }

        this.DrawButtons(store, draft);
        if (isDefault)
        {
            Ui.MutedText("The built-in casting cannot be changed. Duplicate it to start from it, or save your Voices tab edits to make a copy.");
        }
    }

    private void DrawButtons(ICastingPresetStore store, CastingDraft draft)
    {
        var model = draft.Model;
        var state = this.GuardState;

        var use = CastingDraftGuard.Check(PresetAction.Use, state);
        if (ActionButton.Primary("Use this casting##preset", use.IsAllowed, use.Message))
        {
            this.Run(() =>
            {
                store.Activate(model.SelectedName);
                this.ctx.InvalidateVoiceMap();
                this.ctx.Report($"Now using \"{model.SelectedName}\". Speakers without a voice of their own are cast from it.", isError: false);
            });
        }

        ImGui.SameLine();
        var duplicate = CastingDraftGuard.Check(PresetAction.Duplicate, state);
        if (ActionButton.Secondary("Duplicate…##preset", duplicate.IsAllowed, duplicate.Message))
        {
            this.duplicateName = model.SelectedName == CastingPreset.DefaultPresetName
                ? CastingDraft.UniqueName(store.PresetNames)
                : CastingDraft.UniqueName(store.PresetNames, $"{model.SelectedName} copy");
            this.dialogProblem = null;
            this.ctx.Modals.Open(DuplicateModal);
        }

        ImGui.SameLine();
        var create = CastingDraftGuard.Check(PresetAction.New, state);
        if (ActionButton.Secondary("New…##preset", create.IsAllowed, create.Message))
        {
            this.newName = CastingDraft.UniqueName(store.PresetNames);
            this.dialogProblem = null;
            this.ctx.Modals.Open(NewModal);
        }

        ImGui.SameLine();
        var delete = CastingDraftGuard.Check(PresetAction.Delete, state);
        if (ActionButton.Secondary("Delete##preset", delete.Verdict == GuardVerdict.Confirm, delete.Message))
        {
            this.AskDelete(store, draft, delete);
        }

        ImGui.SameLine();
        if (ActionButton.Secondary("Export##preset"))
        {
            ImGui.SetClipboardText(ShareCodec.Encode(model.EditBuffer));
            this.ctx.Report("Casting share string copied to the clipboard.", isError: false);
        }

        ImGui.SameLine();
        var import = CastingDraftGuard.Check(PresetAction.Import, state);
        if (ActionButton.Secondary("Import…##preset", import.IsAllowed, import.Message))
        {
            this.importText = string.Empty;
            this.dialogProblem = null;
            this.ctx.Modals.Open(ImportModal);
        }
    }

    private void AskDelete(ICastingPresetStore store, CastingDraft draft, GuardResult verdict)
    {
        var name = draft.Model.SelectedName;
        this.ctx.Confirm.Ask(
            verdict.Message ?? $"Delete the casting \"{name}\"?",
            verdict.ConfirmLabel ?? "Delete",
            () => this.Run(() =>
            {
                store.Delete(name);
                this.ctx.InvalidateVoiceMap();
                draft.Select(CastingPreset.DefaultPresetName);
                this.ctx.Report($"Deleted \"{name}\".", isError: false);
            }));
    }

    // ---------------------------------------------------------------- dialogs

    private void DrawDuplicateBody()
    {
        Ui.MutedTextAt($"Copies \"{this.ctx.Casting.Name}\" under a new name. The original stays as it is.", BodyWidthEm);
        ImGui.SetNextItemWidth(Ui.Em(NameWidthEm));
        ImGui.InputText("##duplicate-name", ref this.duplicateName, NameMax);
        this.DrawProblem();
        if (ImGui.Button("Duplicate##duplicate-ok"))
        {
            this.Submit(DuplicateModal, PresetAction.Duplicate, this.duplicateName, name =>
            {
                var draft = this.ctx.Casting;
                draft.Model.Fork(draft.Name, draft.Model.EditBuffer, name);
                this.ctx.PresetStore().Save(draft.Model.EditBuffer);
                draft.Model.MarkSaved();
                this.ctx.Report($"Duplicated as \"{name}\". Use it to make it the active casting.", isError: false);
            });
        }

        ImGui.SameLine();
        if (ImGui.Button("Cancel##duplicate"))
        {
            this.ctx.Modals.Close(DuplicateModal);
        }
    }

    private void DrawNewBody()
    {
        Ui.MutedTextAt("Starts with every row empty. Add voices on the Voices tab, then use it.", BodyWidthEm);
        ImGui.SetNextItemWidth(Ui.Em(NameWidthEm));
        ImGui.InputText("##new-name", ref this.newName, NameMax);
        this.DrawProblem();
        if (ImGui.Button("Create##new-ok"))
        {
            this.Submit(NewModal, PresetAction.New, this.newName, name =>
            {
                var draft = this.ctx.Casting;
                draft.Model.NewScratch(name);
                this.ctx.PresetStore().Save(draft.Model.EditBuffer);
                draft.Model.MarkSaved();
                this.ctx.Report($"Created \"{name}\". Fill in its voices, then use it.", isError: false);
            });
        }

        ImGui.SameLine();
        if (ImGui.Button("Cancel##new"))
        {
            this.ctx.Modals.Close(NewModal);
        }
    }

    private void DrawImportBody()
    {
        Ui.MutedTextAt("Paste a casting share string (copied with Export):", BodyWidthEm);
        ImGui.InputTextMultiline("##import-text", ref this.importText, ImportMax, Ui.Em(ImportWidthEm, ImportHeightEm));
        this.DrawProblem();
        if (ImGui.Button("Import##import-ok"))
        {
            this.TryImport();
        }

        ImGui.SameLine();
        if (ImGui.Button("Cancel##import"))
        {
            this.ctx.Modals.Close(ImportModal);
        }
    }

    private void DrawProblem()
    {
        if (this.dialogProblem is { } problem)
        {
            StatusPill.Draw(Severity.Error, problem);
        }
    }

    private void TryImport()
    {
        CastingPreset preset;
        try
        {
            preset = ShareCodec.Decode<CastingPreset>(this.importText);
        }
        catch (Exception e) when (e is ShareCodecException or CastingPresetException)
        {
            this.dialogProblem = e.Message;
            return;
        }

        var store = this.ctx.PresetStore();
        var verdict = CastingDraftGuard.CheckName(PresetAction.Import, preset.Name, store.PresetNames, this.GuardState);
        switch (verdict.Verdict)
        {
            case GuardVerdict.Blocked:
                this.dialogProblem = verdict.Message;
                break;
            case GuardVerdict.Confirm:
                // The dialog closes first so the confirmation is the only modal on screen.
                this.ctx.Modals.Close(ImportModal);
                this.ctx.Confirm.Ask(
                    verdict.Message ?? "Replace the casting?",
                    verdict.ConfirmLabel ?? "Replace",
                    () => this.SaveImported(preset));
                break;
            default:
                this.SaveImported(preset);
                this.ctx.Modals.Close(ImportModal);
                break;
        }
    }

    private void SaveImported(CastingPreset preset) => this.Run(() =>
    {
        this.ctx.PresetStore().Save(preset);
        this.ctx.Casting.Model.Load(preset.Name, preset);
        this.ctx.Report($"Imported \"{preset.Name}\".", isError: false);
    });

    /// <summary>Checks the typed name against the guard, runs <paramref name="apply"/> and closes the dialog; problems stay in the dialog.</summary>
    private void Submit(string modal, PresetAction action, string typed, Action<string> apply)
    {
        var verdict = CastingDraftGuard.CheckName(action, typed, this.ctx.PresetStore().PresetNames, this.GuardState);
        if (!verdict.IsAllowed)
        {
            this.dialogProblem = verdict.Message;
            return;
        }

        var name = typed.Trim();
        if (this.Run(() => apply(name)))
        {
            this.ctx.Modals.Close(modal);
        }
    }

    private bool Run(Action action)
    {
        try
        {
            action();
            return true;
        }
        catch (CastingPresetException e)
        {
            this.ctx.Report(e.Message, isError: true);
            return false;
        }
    }
}
