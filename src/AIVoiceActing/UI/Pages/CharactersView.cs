namespace AIVoiceActing.UI.Pages;

using System.Numerics;
using AIVoiceActing.Domain;
using AIVoiceActing.Ports;
using AIVoiceActing.UI.Components;
using AIVoiceActing.UI.State;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;

/// <summary>
/// Manual voice overrides scoped to one speaker kind: NPC names (npc:) or players (name + world,
/// pc:). Assigned speakers persist; everyone else resolves from the active casting's rows. The
/// table and commit-on-release sliders are shared by both kinds; the add form, clear, export and
/// import all scope to the kind's key prefix.
/// </summary>
internal sealed class CharactersView
{
    private readonly UiContext ctx;
    private readonly string keyPrefix;
    private readonly string importModal;
    private readonly CharacterAddForm addForm;
    private readonly VoiceSetEditor editor = new();

    /// <summary>Slider edits hold a pending tuple per speaker, committed once on release.</summary>
    private readonly Dictionary<string, (string VoiceId, float Bias, float Pitch, float Speed, float Volume)>
        pending = new(StringComparer.Ordinal);

    private bool clearArmed;
    private string importBuffer = string.Empty;

    public CharactersView(UiContext ctx, string keyPrefix, bool player)
    {
        this.ctx = ctx;
        this.keyPrefix = keyPrefix;
        this.importModal = "import-assignments-" + (player ? "pc" : "npc");
        this.addForm = new CharacterAddForm(ctx, player);
        ctx.Modals.Register(this.importModal, "Import voice assignments", this.DrawImportBody);
    }

    public void Draw()
    {
        var store = this.ctx.Profiles;
        var entries = store.Entries
            .Where(e => e.SpeakerKey.StartsWith(this.keyPrefix, StringComparison.Ordinal))
            .ToArray();

        this.DrawToolbar(store, entries);

        var catalog = this.ctx.VoiceCatalog();
        this.editor.DrawFilters(catalog);
        var ids = this.editor.OfferedIds(catalog, this.ctx.VoiceMap().DistinctVoiceIds());
        var rows = CharacterVoicesModel.BuildView(entries);
        var voiceQuery = this.editor.Search;

        var totalRows = rows.Count;
        if (voiceQuery is { Length: > 0 })
        {
            rows = [.. rows.Where(profile => this.VoiceOrNameMatches(profile, voiceQuery))];
            ImGui.SameLine();
            using var colour = Theme.PushText(Theme.Warn);
            ImGui.TextUnformatted($"{rows.Count} of {totalRows} shown");
        }

        this.addForm.Draw(entries);
        this.DrawTable(store, rows, ids);
    }

    private void DrawToolbar(IProfileStore store, VoiceProfile[] entries)
    {
        if (Controls.Button(
                this.clearArmed ? "Really clear all?" : "Clear all",
                enabled: entries.Length > 0,
                entries.Length > 0 ? null : "No stored assignments."))
        {
            if (this.clearArmed)
            {
                var count = entries.Count(entry => store.Remove(entry.SpeakerKey));
                foreach (var key in this.pending.Keys
                    .Where(k => k.StartsWith(this.keyPrefix, StringComparison.Ordinal))
                    .ToArray())
                {
                    this.pending.Remove(key);
                }

                this.ctx.Report($"Cleared {count} voice assignment(s); speakers re-assign from the active casting.", isError: false);
                this.clearArmed = false;
            }
            else
            {
                this.clearArmed = true;
            }
        }

        Controls.Tooltip($"Forgets every stored {this.keyPrefix} assignment. The next line a speaker says picks from the active casting again.");

        ImGui.SameLine();
        if (Controls.Button("Export", enabled: entries.Length > 0))
        {
            ImGui.SetClipboardText(CharacterVoicesModel.ShareText(entries));
            this.ctx.Report($"Copied {entries.Length} voice assignment(s) to the clipboard.", isError: false);
        }

        ImGui.SameLine();
        if (Controls.Button("Import", enabled: true))
        {
            this.importBuffer = string.Empty;
            this.ctx.Modals.Open(this.importModal);
        }
    }

    private bool VoiceOrNameMatches(VoiceProfile profile, string query)
    {
        var voice = this.pending.TryGetValue(profile.SpeakerKey, out var pendingEntry)
            ? pendingEntry.VoiceId
            : profile.ReferenceVoiceId;
        if (voice.Contains(query, StringComparison.InvariantCultureIgnoreCase))
        {
            return true;
        }

        var view = SpeakerKeyView.Parse(profile.SpeakerKey);
        return view.Name.Contains(query, StringComparison.InvariantCultureIgnoreCase)
            || (view.World is not null
                && this.ctx.Worlds.Label(view.World).Contains(query, StringComparison.InvariantCultureIgnoreCase));
    }

    private void DrawTable(IProfileStore store, IReadOnlyList<VoiceProfile> rows, string[] ids)
    {
        var removeKey = default(string?);
        if (ImGui.BeginTable(
                $"##voices{this.keyPrefix}",
                9,
                ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY,
                Vector2.Zero))
        {
            foreach (var profile in rows)
            {
                var key = SpeakerKeyView.Parse(profile.SpeakerKey);
                ImGui.PushID(profile.SpeakerKey);
                ImGui.TableNextRow();

                ImGui.TableNextColumn();
                if (Controls.IconButton(FontAwesomeIcon.Trash, "remove-speaker", "Forget this speaker's voice assignment."))
                {
                    removeKey = profile.SpeakerKey;
                }

                ImGui.TableNextColumn();
                ImGui.TextUnformatted(key.Name);
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(this.ctx.Worlds.Label(key.World));

                this.DrawEditableCells(store, profile, ids);
                ImGui.PopID();
            }

            ImGui.EndTable();
        }

        if (removeKey is { } removed)
        {
            this.pending.Remove(removed);
            store.Remove(removed);
        }
    }

    private void DrawEditableCells(IProfileStore store, VoiceProfile profile, string[] ids)
    {
        ImGui.TableNextColumn();
        var current = this.pending.TryGetValue(profile.SpeakerKey, out var held)
            ? held
            : (profile.ReferenceVoiceId, profile.ExaggerationBias, profile.Pitch, profile.Speed, profile.Volume);
        var (voiceId, bias, pitch, speed, volume) = current;

        ImGui.SetNextItemWidth(Ui.Fill());
        if (this.editor.DrawVoiceCombo("##voice", ids, voiceId, out var picked) && picked != voiceId)
        {
            this.pending.Remove(profile.SpeakerKey);
            this.Commit(profile.SpeakerKey, picked, bias, pitch, speed, volume);
            voiceId = picked;
        }

        ImGui.TableNextColumn();
        ImGui.SetNextItemWidth(Ui.Fill());
        if (ImGui.SliderFloat("##bias", ref bias, 0f, 1f, "%.2f"))
        {
            this.pending[profile.SpeakerKey] = (voiceId, Math.Clamp(bias, 0f, 1f), pitch, speed, volume);
        }

        Controls.Tooltip("Exaggeration bias added on top of the default for this speaker.");
        this.CommitOnRelease(profile.SpeakerKey);

        ImGui.TableNextColumn();
        ImGui.SetNextItemWidth(Ui.Fill());
        if (ImGui.SliderFloat("##pitch", ref pitch, 0.5f, 1.5f, "%.2f"))
        {
            this.pending[profile.SpeakerKey] = (voiceId, Math.Clamp(bias, 0f, 1f), Math.Clamp(pitch, 0.5f, 1.5f), speed, volume);
        }

        Controls.Tooltip("Playback pitch multiplier: 1 is natural; child-pitched voices sit above 1.");
        this.CommitOnRelease(profile.SpeakerKey);

        ImGui.TableNextColumn();
        ImGui.SetNextItemWidth(Ui.Fill());
        if (ImGui.SliderFloat("##speed", ref speed, 0.7f, 1.3f, "%.2f"))
        {
            this.pending[profile.SpeakerKey] = (voiceId, Math.Clamp(bias, 0f, 1f), pitch, Math.Clamp(speed, 0.7f, 1.3f), volume);
        }

        Controls.Tooltip("Pace multiplier: 1 is natural.");
        this.CommitOnRelease(profile.SpeakerKey);

        ImGui.TableNextColumn();
        ImGui.SetNextItemWidth(Ui.Fill());
        if (ImGui.SliderFloat("##volume", ref volume, 0f, 2f, "%.2f"))
        {
            this.pending[profile.SpeakerKey] = (voiceId, Math.Clamp(bias, 0f, 1f), pitch, speed, Math.Clamp(volume, 0f, 2f));
        }

        Controls.Tooltip("Per-speaker loudness multiplier: 1 plays as synthesized, up to 2 boosts quiet voices.");
        this.CommitOnRelease(profile.SpeakerKey);

        ImGui.TableNextColumn();
        var blocked = this.ctx.PreviewBlockedReason;
        if (Controls.IconButton(
                FontAwesomeIcon.Play, "test", "Speak a test line with this voice.",
                blocked is null, blocked))
        {
            this.ctx.PreviewVoice(profile with
            {
                ReferenceVoiceId = voiceId,
                ExaggerationBias = Math.Clamp(bias, 0f, 1f),
                Pitch = pitch,
                Speed = speed,
                Volume = Math.Clamp(volume, 0f, 2f),
            });
        }
    }

    /// <summary>Commit-on-release for one voices-row slider: while a drag is active
    /// the pending tuple tracks it; the deactivated frame writes it through the store
    /// exactly once (persist-once-per-interaction).</summary>
    private void CommitOnRelease(string speakerKey)
    {
        if (ImGui.IsItemDeactivatedAfterEdit()
            && this.pending.TryGetValue(speakerKey, out var committed))
        {
            this.Commit(
                speakerKey,
                committed.VoiceId,
                committed.Bias,
                committed.Pitch,
                committed.Speed,
                committed.Volume);
            this.pending.Remove(speakerKey);
        }
    }

    private void Commit(string speakerKey, string voiceId, float bias, float pitch, float speed, float volume) =>
        this.ctx.Profiles.SetOverride(
            speakerKey,
            voiceId,
            Math.Clamp(bias, 0f, 1f),
            Math.Clamp(volume, 0f, 2f),
            pitch,
            speed);

    private void DrawImportBody()
    {
        var store = this.ctx.Profiles;
        ImGui.TextWrapped("Paste an assignment share string (copied with Export):");
        ImGui.InputTextMultiline("##import-assignments-text", ref this.importBuffer, 64_000, new Vector2(Ui.Em(30), Ui.Em(8)));
        if (ImGui.Button("Import"))
        {
            try
            {
                var shares = CharacterVoicesModel.ParseShare(this.importBuffer);
                var known = store.Entries.Select(e => e.SpeakerKey).ToHashSet(StringComparer.Ordinal);
                var added = 0;
                var updated = 0;
                var skipped = 0;
                foreach (var share in shares)
                {
                    if (string.IsNullOrEmpty(share.SpeakerKey))
                    {
                        continue;
                    }

                    if (!share.SpeakerKey.StartsWith(this.keyPrefix, StringComparison.Ordinal))
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

                    store.SetOverride(
                        share.SpeakerKey,
                        share.ReferenceVoiceId,
                        share.ExaggerationBias,
                        share.Volume,
                        share.Pitch,
                        share.Speed);
                }

                this.ctx.Report(
                    $"Imported {added} new and {updated} updated voice assignment(s)"
                    + (skipped > 0 ? $" ({skipped} skipped: wrong type for this tab)." : "."),
                    isError: false);
                ImGui.CloseCurrentPopup();
            }
            catch (ShareCodecException e)
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
