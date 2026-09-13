namespace AIVoiceActing.UI.Components;

using System.Numerics;
using AIVoiceActing.Domain;
using AIVoiceActing.Ports;
using AIVoiceActing.UI.State;
using Dalamud.Bindings.ImGui;

/// <summary>
/// Override voice table ([trash] | Name | (World) | Voice | Bias | Vol | ▶ Test) plus its
/// add form. Removal is deferred to after the table (collection mutation mid-draw is unsafe);
/// the add form is owned by the pure <see cref="VoiceEntryForm"/>; transient voice/bias/volume
/// edits persist once per completed interaction, not per dragged frame. The ▶ Test button
/// routes through the injected speak delegate — the same ports the game path uses — and
/// is disabled with a reason while models are missing or a request is in flight.
/// Voice ids are computed once per frame by the owner and passed in — the table never
/// re-resolves them per row.
/// </summary>
public sealed class VoiceTable
{
    private readonly bool showWorld;
    private readonly Action save;

    /// <summary>Transient edits not yet persisted; released on interaction end so dragging
    /// the sliders never writes the store per frame.</summary>
    private readonly Dictionary<string, (string VoiceId, float Bias, float Volume)> pending = new(StringComparer.Ordinal);

    /// <param name="showWorld">Player tables show the World column (parsed from the key).</param>
    /// <param name="save">Kept for symmetry with the other widgets; the profile store
    /// persists SetOverride itself, so the table no longer saves per edit.</param>
    public VoiceTable(bool showWorld, Action save)
    {
        this.showWorld = showWorld;
        this.save = save;
    }

    /// <summary>Records a manual override in the profile store (the store persists itself).</summary>
    public required Action<string, string, float, float> SetOverride { get; init; }

    /// <summary>Deletes the stored entry for a speaker.</summary>
    public required Action<string> Remove { get; init; }

    /// <summary>Speaks the test line for a row's profile (fire-and-forget, faults reported).</summary>
    public required Action<VoiceProfile> SpeakTest { get; init; }

    public required Func<bool> EngineReady { get; init; }

    public required Func<string> EngineNotReadyReason { get; init; }

    /// <summary>Draws the override rows filtered from <paramref name="entries"/> plus the add form.</summary>
    public void Draw(
        string tableId,
        IReadOnlyCollection<VoiceProfile> entries,
        Func<VoiceProfile, SpeakerKeyView> view,
        VoiceEntryForm form,
        IReadOnlyList<string> voiceIds)
    {
        var removeKey = default(string?);
        var columnCount = this.showWorld ? 7 : 6;

        // Explicit height + ScrollY: scrolling lives INSIDE the table, so the header row
        // freezes and the window layout stays put regardless of row count.
        var flags = ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY;
        if (ImGui.BeginTable(tableId, columnCount, flags, new Vector2(-1, 260)))
        {
            ImGui.TableSetupScrollFreeze(0, 1);
            ImGui.TableSetupColumn("##trash", ImGuiTableColumnFlags.WidthFixed, 24f);
            ImGui.TableSetupColumn("Name", ImGuiTableColumnFlags.WidthStretch, 2f);
            if (this.showWorld)
            {
                ImGui.TableSetupColumn("World", ImGuiTableColumnFlags.WidthFixed, 60f);
            }

            ImGui.TableSetupColumn("Voice", ImGuiTableColumnFlags.WidthStretch, 2f);
            ImGui.TableSetupColumn("Bias", ImGuiTableColumnFlags.WidthFixed, 110f);
            ImGui.TableSetupColumn("Vol", ImGuiTableColumnFlags.WidthFixed, 80f);
            ImGui.TableSetupColumn("Test", ImGuiTableColumnFlags.WidthFixed, 48f);
            ImGui.TableHeadersRow();

            foreach (var profile in entries)
            {
                var key = view(profile);
                ImGui.PushID(profile.SpeakerKey);
                ImGui.TableNextRow();
                ImGui.TableNextColumn();

                if (ImGui.SmallButton("🗑"))
                {
                    removeKey = profile.SpeakerKey;
                }

                ImGui.TableNextColumn();
                ImGui.TextUnformatted(key.Name);

                if (this.showWorld)
                {
                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted(key.World?.ToString() ?? "—");
                }

                ImGui.TableNextColumn();
                var (voiceId, bias, volume) = this.pending.TryGetValue(profile.SpeakerKey, out var edit)
                    ? edit
                    : (profile.ReferenceVoiceId, profile.ExaggerationBias, profile.Volume);
                ImGui.SetNextItemWidth(-1);
                if (this.DrawVoiceCombo("##voice", voiceIds, voiceId, out var pickedVoice))
                {
                    this.pending.Remove(profile.SpeakerKey);
                    this.SetOverride(profile.SpeakerKey, pickedVoice, Math.Clamp(bias, 0f, 1f), Math.Clamp(volume, 0f, 2f));
                }

                ImGui.TableNextColumn();
                ImGui.SetNextItemWidth(-1);
                if (ImGui.SliderFloat("##bias", ref bias, 0f, 1f, "%.2f"))
                {
                    // Defer while dragging: the release frame reports deactivated WITHOUT a
                    // value change, so the commit happens below, outside the changed branch.
                    this.pending[profile.SpeakerKey] = (voiceId, Math.Clamp(bias, 0f, 1f), volume);
                }

                Controls.Tooltip("Exaggeration bias added on top of the default for this speaker: 0 follows the global setting, higher is more theatrical.");

                if (ImGui.IsItemDeactivatedAfterEdit()
                    && this.pending.TryGetValue(profile.SpeakerKey, out var committedBias)
                    && committedBias.Bias == Math.Clamp(bias, 0f, 1f))
                {
                    this.SetOverride(profile.SpeakerKey, voiceId, committedBias.Bias, committedBias.Volume);
                    this.pending.Remove(profile.SpeakerKey);
                }

                ImGui.TableNextColumn();
                ImGui.SetNextItemWidth(-1);
                if (ImGui.SliderFloat("##vol", ref volume, 0f, 2f, "%.2f"))
                {
                    this.pending[profile.SpeakerKey] = (voiceId, bias, Math.Clamp(volume, 0f, 2f));
                }

                Controls.Tooltip("Per-speaker loudness multiplier: 1 plays as synthesized, up to 2 boosts quiet voices.");

                if (ImGui.IsItemDeactivatedAfterEdit()
                    && this.pending.TryGetValue(profile.SpeakerKey, out var committedVol)
                    && committedVol.Volume == Math.Clamp(volume, 0f, 2f))
                {
                    this.SetOverride(profile.SpeakerKey, voiceId, Math.Clamp(bias, 0f, 1f), committedVol.Volume);
                    this.pending.Remove(profile.SpeakerKey);
                }

                ImGui.TableNextColumn();
                var test = this.EngineReady()
                    ? ImGui.SmallButton("▶")
                    : Controls.Button("▶", false, this.EngineNotReadyReason());
                if (test)
                {
                    this.SpeakTest(profile);
                }

                ImGui.PopID();
            }

            ImGui.EndTable();
        }

        if (removeKey is { } removed)
        {
            this.pending.Remove(removed);
            this.Remove(removed);
        }

        ImGui.Separator();
        this.DrawAddForm(entries, form, voiceIds);
    }

    /// <summary>
    /// Array+count combo (the IReadOnlyList binding never commits selections). A stored id
    /// missing from the bank is APPENDED as a "(missing)" placeholder so the stored value
    /// stays visible and selectable — appending (not prepending) means committing can
    /// never accidentally pick the placeholder; any real pick commits a real voice.
    /// </summary>
    private bool DrawVoiceCombo(string label, IReadOnlyList<string> voiceIds, string voiceId, out string picked)
    {
        var display = voiceIds as string[] ?? [.. voiceIds];
        var index = Array.IndexOf(display, voiceId);
        if (index < 0)
        {
            display = [.. display, $"{voiceId} (missing)"];
            index = display.Length - 1;
        }

        var changed = ImGui.Combo(label, ref index, display, display.Length);
        picked = display[index];
        return changed;
    }

    private void DrawAddForm(IReadOnlyCollection<VoiceProfile> entries, VoiceEntryForm form, IReadOnlyList<string> voiceIds)
    {
        var existing = entries.Select(e => e.SpeakerKey).ToHashSet(StringComparer.Ordinal);
        ImGui.SetNextItemWidth(180f);
        var name = form.Name;
        if (ImGui.InputTextWithHint(this.showWorld ? "##player-name" : "##npc-name", "Name", ref name, 100))
        {
            form.Name = name;
        }

        Controls.Tooltip("The character's name exactly as it appears in chat.");

        if (this.showWorld)
        {
            ImGui.SameLine();
            ImGui.SetNextItemWidth(80f);
            var world = form.World;
            if (ImGui.InputTextWithHint("##player-world", "World id", ref world, 16))
            {
                form.World = world;
            }

            Controls.Tooltip("Numeric world id distinguishing same-named characters across worlds.");
        }

        ImGui.SameLine();
        if (Controls.Button("Add", enabled: true)
            && form.TryBuildKey(out var key, out _)
            && !existing.Contains(key)) // duplicates surface via the Validate line below
        {
            this.SetOverride(key, voiceIds.Count > 0 ? voiceIds[0] : "default", 0f, 1f);
            form.Reset();
        }

        var error = form.Validate(existing);
        if (error is not null && (form.Name.Length > 0 || form.World.Length > 0))
        {
            ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(1f, 0.4f, 0.4f, 1f));
            ImGui.TextUnformatted(error);
            ImGui.PopStyleColor();
        }
    }
}
