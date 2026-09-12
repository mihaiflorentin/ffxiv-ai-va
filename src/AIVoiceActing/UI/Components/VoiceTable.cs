namespace AIVoiceActing.UI.Components;

using System.Numerics;
using AIVoiceActing.Domain;
using AIVoiceActing.Ports;
using AIVoiceActing.UI.State;
using Dalamud.Bindings.ImGui;

/// <summary>
/// Override voice table ([trash] | Name | (World) | Voice | Bias | ▶ Test) plus its add
/// form. Removal is deferred to after the table (collection mutation mid-draw is unsafe);
/// the add form is owned by the pure <see cref="VoiceEntryForm"/>; transient voice/bias
/// edits persist once per completed interaction, not per dragged frame. The ▶ Test button
/// routes through the injected speak delegate — the same ports the game path uses — and
/// is disabled with a reason while models are missing.
/// </summary>
public sealed class VoiceTable
{
    private readonly bool showWorld;
    private readonly Func<IReadOnlyList<string>> voiceOptions;
    private readonly Action save;

    /// <summary>Transient edits not yet persisted; released on interaction end so dragging
    /// the bias slider never writes the store per frame.</summary>
    private readonly Dictionary<string, (string VoiceId, float Bias)> pending = new(StringComparer.Ordinal);

    /// <param name="showWorld">Player tables show the World column (parsed from the key).</param>
    /// <param name="voiceOptions">Reference-voice ids for the combo.</param>
    /// <param name="save">Config persist delegate; the store persists SetOverride itself —
    /// this is called once per completed interaction.</param>
    public VoiceTable(bool showWorld, Func<IReadOnlyList<string>> voiceOptions, Action save)
    {
        this.showWorld = showWorld;
        this.voiceOptions = voiceOptions;
        this.save = save;
    }

    /// <summary>Records a manual override in the profile store.</summary>
    public required Action<string, string, float> SetOverride { get; init; }

    /// <summary>Deletes the stored entry for a speaker.</summary>
    public required Action<string> Remove { get; init; }

    /// <summary>Speaks the test line for a row's profile (fire-and-forget).</summary>
    public required Func<VoiceProfile, Task?> SpeakTest { get; init; }

    public required Func<bool> EngineReady { get; init; }

    public required string EngineNotReadyReason { get; init; }

    /// <summary>Draws the override rows filtered from <paramref name="entries"/> plus the add form.</summary>
    public void Draw(
        string tableId,
        IReadOnlyCollection<VoiceProfile> entries,
        Func<VoiceProfile, SpeakerKeyView> view,
        VoiceEntryForm form)
    {
        var removeKey = default(string?);
        var columnCount = this.showWorld ? 6 : 5;

        if (ImGui.BeginTable(tableId, columnCount, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg))
        {
            ImGui.TableSetupScrollFreeze(0, 1);
            ImGui.TableSetupColumn("##trash", ImGuiTableColumnFlags.WidthFixed, 24f);
            ImGui.TableSetupColumn("Name", ImGuiTableColumnFlags.WidthStretch, 2f);
            if (this.showWorld)
            {
                ImGui.TableSetupColumn("World", ImGuiTableColumnFlags.WidthFixed, 60f);
            }

            ImGui.TableSetupColumn("Voice", ImGuiTableColumnFlags.WidthStretch, 2f);
            ImGui.TableSetupColumn("Bias", ImGuiTableColumnFlags.WidthFixed, 140f);
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
                var voices = this.voiceOptions();
                var (voiceId, bias) = this.pending.TryGetValue(profile.SpeakerKey, out var edit)
                    ? edit
                    : (profile.ReferenceVoiceId, profile.ExaggerationBias);
                var voiceIndex = Math.Max(0, voices.ToList().IndexOf(voiceId));
                ImGui.SetNextItemWidth(-1);
                if (ImGui.Combo("##voice", ref voiceIndex, voices))
                {
                    this.Apply(profile, voices[voiceIndex], bias, defer: false);
                }

                ImGui.TableNextColumn();
                ImGui.SetNextItemWidth(-1);
                if (ImGui.SliderFloat("##bias", ref bias, 0f, 1f, "%.2f"))
                {
                    this.Apply(profile, voiceId, bias, defer: !ImGui.IsItemDeactivatedAfterEdit());
                }

                ImGui.TableNextColumn();
                var test = this.EngineReady()
                    ? ImGui.SmallButton("▶")
                    : Controls.Button("▶", false, this.EngineNotReadyReason);
                if (test)
                {
                    _ = this.SpeakTest(profile);
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
        this.DrawAddForm(entries, form);
    }

    /// <summary>Commits an edit; deferred edits live in <see cref="pending"/> until the
    /// interaction ends, then persist in one write.</summary>
    private void Apply(VoiceProfile profile, string voiceId, float bias, bool defer)
    {
        if (defer)
        {
            this.pending[profile.SpeakerKey] = (voiceId, Math.Clamp(bias, 0f, 1f));
            return;
        }

        this.pending.Remove(profile.SpeakerKey);
        this.SetOverride(profile.SpeakerKey, voiceId, Math.Clamp(bias, 0f, 1f));
        this.save();
    }

    private void DrawAddForm(IReadOnlyCollection<VoiceProfile> entries, VoiceEntryForm form)
    {
        var existing = entries.Select(e => e.SpeakerKey).ToHashSet(StringComparer.Ordinal);
        ImGui.SetNextItemWidth(180f);
        var name = form.Name;
        if (ImGui.InputTextWithHint(this.showWorld ? "##player-name" : "##npc-name", "Name", ref name, 100))
        {
            form.Name = name;
        }

        if (this.showWorld)
        {
            ImGui.SameLine();
            ImGui.SetNextItemWidth(80f);
            var world = form.World;
            if (ImGui.InputTextWithHint("##player-world", "World id", ref world, 16))
            {
                form.World = world;
            }
        }

        ImGui.SameLine();
        if (Controls.Button("Add", enabled: true) && form.TryBuildKey(out var key, out _))
        {
            var voices = this.voiceOptions();
            this.SetOverride(key, voices.Count > 0 ? voices[0] : "default", 0f);
            this.save();
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
