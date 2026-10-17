namespace AIVoiceActing.UI.Pages;

using AIVoiceActing.Domain;
using AIVoiceActing.Ports;
using AIVoiceActing.UI.Components;
using AIVoiceActing.UI.State;
using Dalamud.Bindings.ImGui;

/// <summary>
/// Casting presets: pick, use, duplicate, start fresh, delete, export and import named castings.
/// This is the former preset bar of the Voices tab; it now lives in Settings (the Voices tab only
/// links here). It shares the casting edits with the Voices page, so switching preset is blocked
/// while there are unsaved voice changes. Duplicate, New and Import run in the shared modal host;
/// deleting asks first.
/// </summary>
internal sealed class CastingPresetPanel
{
    private const string ForkModal = "fork-preset";
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
    private string forkName = string.Empty;
    private string newName = string.Empty;
    private string importText = string.Empty;

    public CastingPresetPanel(UiContext ctx)
    {
        this.ctx = ctx;
        ctx.Modals.Register(ForkModal, "Duplicate casting", this.DrawForkBody);
        ctx.Modals.Register(NewModal, "New casting from scratch", this.DrawNewBody);
        ctx.Modals.Register(ImportModal, "Import casting", this.DrawImportBody);
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
        ImGui.BeginDisabled(draft.Dirty);
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
        if (draft.Dirty)
        {
            Ui.MutedText("You have unsaved voice changes. Save or discard them on the Voices tab before switching.");
        }

        this.DrawButtons(store, draft, isDefault);
        if (isDefault)
        {
            Ui.MutedText("The built-in casting cannot be changed. Duplicate it to start from it, or save your Voices tab edits to make a copy.");
        }
    }

    private void DrawButtons(ICastingPresetStore store, CastingDraft draft, bool isDefault)
    {
        var model = draft.Model;
        if (ActionButton.Primary("Use this casting##preset", !draft.IsActive && !draft.Dirty, draft.IsActive ? "Already in use." : "Save or discard your voice edits first."))
        {
            this.Run(() =>
            {
                store.Activate(model.SelectedName);
                this.ctx.InvalidateVoiceMap();
                this.ctx.Report($"Now using \"{model.SelectedName}\". Speakers without a voice of their own are cast from it.", isError: false);
            });
        }

        ImGui.SameLine();
        if (ActionButton.Secondary("Duplicate…##preset", !draft.Dirty, "Save or discard your voice edits first."))
        {
            this.forkName = isDefault ? CastingDraft.UniqueName(store.PresetNames) : $"{model.SelectedName} copy";
            this.ctx.Modals.Open(ForkModal);
        }

        ImGui.SameLine();
        if (ActionButton.Secondary("New…##preset", !draft.Dirty, "Save or discard your voice edits first."))
        {
            this.newName = CastingDraft.UniqueName(store.PresetNames);
            this.ctx.Modals.Open(NewModal);
        }

        ImGui.SameLine();
        if (ActionButton.Secondary("Delete##preset", !isDefault, "The built-in casting cannot be deleted."))
        {
            var name = model.SelectedName;
            this.ctx.Confirm.Ask(
                $"Delete the casting \"{name}\"? Characters keep their voices. Speakers without one go back to the built-in casting.",
                "Delete",
                () => this.Run(() =>
                {
                    store.Delete(name);
                    this.ctx.InvalidateVoiceMap();
                    draft.Select(CastingPreset.DefaultPresetName);
                    this.ctx.Report($"Deleted \"{name}\".", isError: false);
                }));
        }

        ImGui.SameLine();
        if (ImGui.Button("Export##preset"))
        {
            ImGui.SetClipboardText(ShareCodec.Encode(model.EditBuffer));
            this.ctx.Report("Casting share string copied to the clipboard.", isError: false);
        }

        ImGui.SameLine();
        if (ActionButton.Secondary("Import…##preset", !draft.Dirty, "Save or discard your voice edits first."))
        {
            this.importText = string.Empty;
            this.ctx.Modals.Open(ImportModal);
        }
    }

    private void DrawForkBody()
    {
        Ui.MutedTextAt($"Copies \"{this.ctx.Casting.Name}\" under a new name. The original stays as it is.", BodyWidthEm);
        ImGui.SetNextItemWidth(Ui.Em(NameWidthEm));
        ImGui.InputText("##fork-name", ref this.forkName, NameMax);
        if (ImGui.Button("Duplicate##fork-ok"))
        {
            this.Submit(ForkModal, this.forkName, "name the copy", name =>
            {
                var draft = this.ctx.Casting;
                draft.Model.Fork(draft.Name, draft.Model.EditBuffer, name);
                this.ctx.PresetStore().Save(draft.Model.EditBuffer);
                draft.Model.MarkSaved();
                this.ctx.Report($"Duplicated as \"{name}\". Use it to make it the active casting.", isError: false);
            });
        }

        ImGui.SameLine();
        if (ImGui.Button("Cancel##fork"))
        {
            this.ctx.Modals.Close(ForkModal);
        }
    }

    private void DrawNewBody()
    {
        Ui.MutedTextAt("Starts with every row empty. Add voices on the Voices tab, then use it.", BodyWidthEm);
        ImGui.SetNextItemWidth(Ui.Em(NameWidthEm));
        ImGui.InputText("##new-name", ref this.newName, NameMax);
        if (ImGui.Button("Create##new-ok"))
        {
            this.Submit(NewModal, this.newName, "name the casting", name =>
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
        if (ImGui.Button("Import##import-ok"))
        {
            try
            {
                var preset = ShareCodec.Decode<CastingPreset>(this.importText);
                this.ctx.PresetStore().Save(preset);
                this.ctx.Casting.Model.Load(preset.Name, preset);
                this.ctx.Report($"Imported \"{preset.Name}\".", isError: false);
                this.ctx.Modals.Close(ImportModal);
            }
            catch (Exception e) when (e is ShareCodecException or CastingPresetException)
            {
                this.ctx.Report(e.Message, isError: true);
            }
        }

        ImGui.SameLine();
        if (ImGui.Button("Cancel##import"))
        {
            this.ctx.Modals.Close(ImportModal);
        }
    }

    /// <summary>Validates the typed name, runs <paramref name="apply"/> and closes the modal; store errors are reported and keep it open.</summary>
    private void Submit(string modal, string typed, string need, Action<string> apply)
    {
        var name = typed.Trim();
        if (name.Length == 0)
        {
            this.ctx.Report($"Please {need}.", isError: true);
            return;
        }

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
