namespace AIVoiceActing.UI.Pages;

using System.Numerics;
using AIVoiceActing.Domain;
using AIVoiceActing.Ports;
using AIVoiceActing.UI.Components;
using AIVoiceActing.UI.State;
using Dalamud.Bindings.ImGui;

/// <summary>
/// Named casting presets over the flat voice grid: every race/gender row and every beast tribe
/// owns its voice list directly. Activate, fork, edit, share. Edits stay in the pure presenter's
/// buffer until Save; activation re-overlays the container's voice map for speakers without
/// their own assignment. Fork, New and Import run in the shared <see cref="ModalHost"/>.
/// </summary>
internal sealed class CastingDefaultsView
{
    private const string ForkModal = "fork-preset";
    private const string NewModal = "new-preset";
    private const string ImportModal = "import-preset";

    private readonly UiContext ctx;
    private readonly CastingTabModel casting = new();
    private readonly VoiceSlotList slotList;
    private readonly BeastTribesView tribes;
    private bool initialized;
    private string presetImportBuffer = string.Empty;
    private string forkNameBuffer = string.Empty;
    private string scratchNameBuffer = string.Empty;
    private bool deleteArmed;

    public CastingDefaultsView(UiContext ctx)
    {
        this.ctx = ctx;
        this.slotList = new VoiceSlotList(ctx);
        this.tribes = new BeastTribesView(ctx, this.casting, this.slotList);
        ctx.Modals.Register(ForkModal, "Fork preset", this.DrawForkBody);
        ctx.Modals.Register(NewModal, "New preset from scratch", this.DrawNewBody);
        ctx.Modals.Register(ImportModal, "Import preset", this.DrawImportBody);
    }

    public void Draw()
    {
        var store = this.ctx.PresetStore();
        if (!this.initialized)
        {
            var active = store.ActivePresetName;
            this.casting.Load(active, LoadPreset(store, active));
            this.initialized = true;
        }

        this.DrawPresetBar(store);

        ImGui.Separator();
        Controls.Checkbox(
            "Use race/gender voice presets for unlisted speakers",
            "Speakers without their own assignment pick randomly from their race/gender row; off resolves everyone from the Unknown row.",
            () => this.ctx.Config.UseRaceVoicePresets,
            v => { this.ctx.Config.UseRaceVoicePresets = v; this.ctx.Save(); });
        Controls.Checkbox(
            "Age-aware voices",
            "Children speak a little higher and quicker, elders a little lower and slower, on top of their row's voice. Manual overrides are never changed.",
            () => this.ctx.Config.AgeAwareCasting,
            v => { this.ctx.Config.AgeAwareCasting = v; this.ctx.Save(); });

        var catalog = this.ctx.VoiceCatalog();
        var rawIds = this.ctx.VoiceMap().DistinctVoiceIds();

        Controls.Section("Race & gender rows");
        ImGui.TextWrapped(
            "First sight of a speaker picks randomly from the matching row and is pinned from then on (override it in NPC/Player Voices). "
            + "\"Unknown\" covers speakers whose race/gender cannot be read.");
        foreach (var race in Races.All)
        {
            this.DrawBucketSection(CastingDefaults.RaceBucketKey(race.Id, female: false), $"{race.Name} — Male", catalog, rawIds);
            this.DrawBucketSection(CastingDefaults.RaceBucketKey(race.Id, female: true), $"{race.Name} — Female", catalog, rawIds);
        }

        this.DrawBucketSection(CastingDefaults.UnknownBucketKey, "Unknown (cannot identify)", catalog, rawIds);

        this.tribes.Draw(catalog, rawIds);
    }

    private void DrawPresetBar(ICastingPresetStore store)
    {
        var nameArray = store.PresetNames as string[] ?? [.. store.PresetNames];
        var selected = Array.IndexOf(nameArray, this.casting.SelectedName);
        ImGui.SetNextItemWidth(Ui.Em(14));
        if (ImGui.Combo("##preset", ref selected, nameArray, nameArray.Length))
        {
            var name = nameArray[Math.Clamp(selected, 0, nameArray.Length - 1)];
            if (name != this.casting.SelectedName)
            {
                this.casting.Load(name, LoadPreset(store, name));
                this.deleteArmed = false;
            }
        }

        var isDefault = this.casting.SelectedName == CastingPreset.DefaultPresetName;
        var alreadyActive = store.ActivePresetName == this.casting.SelectedName;

        ImGui.SameLine();
        if (Controls.Button("Activate", enabled: !alreadyActive && !this.casting.Dirty,
                alreadyActive ? "Already the active preset." : "Save or discard your edits first."))
        {
            try
            {
                store.Activate(this.casting.SelectedName);
                this.ctx.InvalidateVoiceMap();
                this.ctx.Report(
                    $"Activated preset \"{this.casting.SelectedName}\" — speakers without their own assignment re-cast on next sight.",
                    isError: false);
            }
            catch (CastingPresetException e)
            {
                this.ctx.Report(e.Message, isError: true);
            }
        }

        Controls.Tooltip("Makes this preset the live casting. Stored characters keep their voices; manage those in Characters.");

        ImGui.SameLine();
        if (Controls.Button("Save",
                enabled: this.casting.Dirty && !isDefault,
                !this.casting.Dirty ? "No unsaved edits." : "The built-in Default preset is immutable."))
        {
            try
            {
                store.Save(this.casting.EditBuffer);
                this.casting.MarkSaved();
                this.ctx.Report($"Saved preset \"{this.casting.EditBuffer.Name}\".", isError: false);
            }
            catch (CastingPresetException e)
            {
                this.ctx.Report(e.Message, isError: true);
            }
        }

        ImGui.SameLine();
        if (Controls.Button("Fork…", enabled: true))
        {
            this.forkNameBuffer = isDefault ? "My casting" : $"{this.casting.SelectedName} copy";
            this.ctx.Modals.Open(ForkModal);
        }

        ImGui.SameLine();
        if (Controls.Button("New", enabled: true))
        {
            this.scratchNameBuffer = "New casting";
            this.ctx.Modals.Open(NewModal);
        }

        ImGui.SameLine();
        if (Controls.Button(this.deleteArmed ? "Really delete?" : "Delete", enabled: !isDefault,
                isDefault ? "The built-in Default preset cannot be deleted." : null))
        {
            if (this.deleteArmed)
            {
                try
                {
                    store.Delete(this.casting.SelectedName);
                    this.casting.Load(CastingPreset.DefaultPresetName, store.GetDefault());
                    this.ctx.Report("Preset deleted; the built-in casting is active again.", isError: false);
                }
                catch (CastingPresetException e)
                {
                    this.ctx.Report(e.Message, isError: true);
                }

                this.deleteArmed = false;
            }
            else
            {
                this.deleteArmed = true;
            }
        }

        ImGui.SameLine();
        if (Controls.Button("Export", enabled: true))
        {
            ImGui.SetClipboardText(ShareCodec.Encode(this.casting.EditBuffer));
            this.ctx.Report("Preset share string copied to the clipboard.", isError: false);
        }

        ImGui.SameLine();
        if (Controls.Button("Import", enabled: true))
        {
            this.presetImportBuffer = string.Empty;
            this.ctx.Modals.Open(ImportModal);
        }

        if (isDefault)
        {
            ImGui.TextDisabled("The Default preset is immutable — Fork carries your edits into a new preset.");
        }
    }

    /// <summary>One race/gender bucket: a collapsing header with the live voice count
    /// over the shared voice-list editor.</summary>
    private void DrawBucketSection(string bucketKey, string label, IReadOnlyList<VoiceCatalogEntry> catalog, string[] rawIds)
    {
        var slots = this.casting.EditBuffer.Buckets.GetValueOrDefault(bucketKey) ?? [];
        if (!ImGui.CollapsingHeader($"{label} ({slots.Length} voices)###row-{bucketKey}"))
        {
            return;
        }

        this.slotList.Draw(
            bucketKey,
            slots,
            catalog,
            rawIds,
            (key, index, slot) => this.casting.SetSlot(key, index, slot),
            this.casting.RemoveVoice,
            this.casting.AddVoice);
    }

    /// <summary>Load-switch helper: "Default" derives from the map, everything else
    /// comes from the file (a vanished preset falls back to Default).</summary>
    private static CastingPreset LoadPreset(ICastingPresetStore store, string name)
    {
        if (name == CastingPreset.DefaultPresetName)
        {
            return store.GetDefault();
        }

        try
        {
            return store.Get(name);
        }
        catch (CastingPresetException)
        {
            return store.GetDefault();
        }
    }

    private void DrawForkBody()
    {
        var store = this.ctx.PresetStore();
        ImGui.TextWrapped($"Copies \"{this.casting.SelectedName}\" under a new name; the original stays untouched.");
        ImGui.SetNextItemWidth(Ui.Em(20));
        ImGui.InputText("##fork-name", ref this.forkNameBuffer, 64);
        if (ImGui.Button("Fork"))
        {
            var name = this.forkNameBuffer.Trim();
            if (name.Length == 0)
            {
                this.ctx.Report("The fork needs a name.", isError: true);
            }
            else
            {
                try
                {
                    this.casting.Fork(this.casting.SelectedName, this.casting.EditBuffer, name);
                    store.Save(this.casting.EditBuffer);
                    this.casting.MarkSaved();
                    this.ctx.Report($"Forked \"{name}\" — press Activate to apply it.", isError: false);
                    ImGui.CloseCurrentPopup();
                }
                catch (CastingPresetException e)
                {
                    this.ctx.Report(e.Message, isError: true);
                }
            }
        }

        ImGui.SameLine();
        if (ImGui.Button("Cancel"))
        {
            ImGui.CloseCurrentPopup();
        }
    }

    private void DrawNewBody()
    {
        var store = this.ctx.PresetStore();
        ImGui.TextWrapped("Starts with one empty voice set; every race row points at it until you reassign.");
        ImGui.SetNextItemWidth(Ui.Em(20));
        ImGui.InputText("##new-name", ref this.scratchNameBuffer, 64);
        if (ImGui.Button("Create"))
        {
            var name = this.scratchNameBuffer.Trim();
            if (name.Length == 0)
            {
                this.ctx.Report("The preset needs a name.", isError: true);
            }
            else
            {
                try
                {
                    this.casting.NewScratch(name);
                    store.Save(this.casting.EditBuffer);
                    this.casting.MarkSaved();
                    this.ctx.Report($"Created \"{name}\" — fill its voice sets, then press Activate.", isError: false);
                    ImGui.CloseCurrentPopup();
                }
                catch (CastingPresetException e)
                {
                    this.ctx.Report(e.Message, isError: true);
                }
            }
        }

        ImGui.SameLine();
        if (ImGui.Button("Cancel"))
        {
            ImGui.CloseCurrentPopup();
        }
    }

    private void DrawImportBody()
    {
        var store = this.ctx.PresetStore();
        ImGui.TextWrapped("Paste a preset share string (copied with Export):");
        ImGui.InputTextMultiline("##import-preset-text", ref this.presetImportBuffer, 64_000, new Vector2(Ui.Em(30), Ui.Em(8)));
        if (ImGui.Button("Import"))
        {
            try
            {
                var preset = ShareCodec.Decode<CastingPreset>(this.presetImportBuffer);
                store.Save(preset);
                this.casting.Load(preset.Name, preset);
                this.ctx.Report($"Imported preset \"{preset.Name}\".", isError: false);
                ImGui.CloseCurrentPopup();
            }
            catch (Exception e) when (e is ShareCodecException or CastingPresetException)
            {
                this.ctx.Report(e.Message, isError: true);
            }
        }

        ImGui.SameLine();
        if (ImGui.Button("Cancel"))
        {
            ImGui.CloseCurrentPopup();
        }
    }
}
