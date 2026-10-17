namespace AIVoiceActing.UI.Pages;

using System.Numerics;
using AIVoiceActing.Domain;
using AIVoiceActing.Ports;
using AIVoiceActing.UI.Components;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;

/// <summary>
/// The shared voice-list editor used by race rows and beast-tribe lists: filter row, one table
/// row per voice (trash, voice, pitch/speed/volume/bias, preview) and the add button. Mutations
/// route through delegates so both surfaces share one implementation; slider edits write straight
/// to the casting edit buffer (in memory until Save).
/// </summary>
internal sealed class VoiceSlotList(UiContext ctx)
{
    private const float VoiceColumnWeight = 2f;

    /// <summary>Filter state, one editor instance per drawn row (keyed by bucket key).</summary>
    private readonly Dictionary<string, VoiceSetEditor> editors = new(StringComparer.Ordinal);

    public void Draw(
        string rowKey,
        IReadOnlyList<VoiceSlotDto> slots,
        IReadOnlyList<VoiceCatalogEntry> catalog,
        string[] rawIds,
        Action<string, int, VoiceSlotDto> setSlot,
        Action<string, int> removeSlot,
        Action<string, string> addVoice)
    {
        var editor = this.EditorFor(rowKey);
        editor.DrawFilters(catalog);
        var ids = editor.OfferedIds(catalog, rawIds);
        ImGui.PushID(rowKey);
        var removeAt = default(int?);
        if (ImGui.BeginTable(
                "##slots",
                7,
                ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY,
                new Vector2(Ui.Fill(), Ui.Em(16))))
        {
            ImGui.TableSetupScrollFreeze(0, 1);
            ImGui.TableSetupColumn("##trash", ImGuiTableColumnFlags.WidthFixed, Ui.Em(1.6f));
            ImGui.TableSetupColumn("Voice", ImGuiTableColumnFlags.WidthStretch, VoiceColumnWeight);
            ImGui.TableSetupColumn("Pitch", ImGuiTableColumnFlags.WidthFixed, Ui.Em(6));
            ImGui.TableSetupColumn("Speed", ImGuiTableColumnFlags.WidthFixed, Ui.Em(6));
            ImGui.TableSetupColumn("Vol", ImGuiTableColumnFlags.WidthFixed, Ui.Em(5.5f));
            ImGui.TableSetupColumn("Bias", ImGuiTableColumnFlags.WidthFixed, Ui.Em(6));
            ImGui.TableSetupColumn("Test", ImGuiTableColumnFlags.WidthFixed, Ui.Em(2.5f));
            ImGui.TableHeadersRow();

            for (var i = 0; i < slots.Count; i++)
            {
                var slot = slots[i];
                ImGui.PushID(i);
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                if (Controls.IconButton(FontAwesomeIcon.Trash, "remove-slot", "Remove this voice from the set."))
                {
                    removeAt = i;
                }

                ImGui.TableNextColumn();
                ImGui.SetNextItemWidth(Ui.Fill());
                if (editor.DrawVoiceCombo("##voice", ids, slot.Id, out var picked) && picked != slot.Id)
                {
                    slot = slot with { Id = picked };
                    setSlot(rowKey, i, slot);
                }

                var index = i;
                DrawSlider(v => setSlot(rowKey, index, v), slot, "##pitch", 0.5f, 1.5f, slot.Pitch, static (s, v) => s with { Pitch = v }, "Playback pitch multiplier: 1 is natural; child-like races sit above 1.");
                DrawSlider(v => setSlot(rowKey, index, v), slot, "##speed", 0.7f, 1.3f, slot.Speed, static (s, v) => s with { Speed = v }, "Pace multiplier: 1 is natural.");
                DrawSlider(v => setSlot(rowKey, index, v), slot, "##volume", 0f, 2f, slot.Volume, static (s, v) => s with { Volume = v }, "Loudness multiplier: 1 plays as synthesized, up to 2 boosts quiet voices.");
                DrawSlider(v => setSlot(rowKey, index, v), slot, "##bias", 0f, 1f, slot.ExaggerationBias, static (s, v) => s with { ExaggerationBias = v }, "Exaggeration bias added on top of the default for this cast.");

                ImGui.TableNextColumn();
                var blocked = ctx.PreviewBlockedReason;
                if (Controls.IconButton(
                        FontAwesomeIcon.Play, "test", "Speak a test line with this voice.",
                        blocked is null, blocked))
                {
                    ctx.PreviewVoice(new VoiceProfile(
                        $"preview:{slot.Id}", slot.Id, slot.ExaggerationBias,
                        DateTimeOffset.UtcNow, Custom: true, slot.Pitch, slot.Speed, slot.Volume));
                }

                ImGui.PopID();
            }

            ImGui.EndTable();
        }

        if (removeAt is { } removal)
        {
            removeSlot(rowKey, removal);
        }

        if (ImGui.SmallButton("+ Add voice"))
        {
            addVoice(rowKey, ids.Length > 0 ? ids[0] : rawIds.FirstOrDefault() ?? "default");
        }

        ImGui.PopID();
    }

    /// <summary>Per-slot slider, written straight through to the edit buffer.</summary>
    private static void DrawSlider(
        Action<VoiceSlotDto> commit,
        VoiceSlotDto slot,
        string label,
        float min,
        float max,
        float value,
        Func<VoiceSlotDto, float, VoiceSlotDto> apply,
        string tooltip)
    {
        ImGui.TableNextColumn();
        ImGui.SetNextItemWidth(Ui.Fill());
        var v = value;
        if (ImGui.SliderFloat(label, ref v, min, max, "%.2f")
            && Math.Abs(v - value) > float.Epsilon)
        {
            commit(apply(slot, Math.Clamp(v, min, max)));
        }

        Controls.Tooltip(tooltip);
    }

    private VoiceSetEditor EditorFor(string rowKey)
    {
        if (!this.editors.TryGetValue(rowKey, out var editor))
        {
            editor = new VoiceSetEditor();
            this.editors[rowKey] = editor;
        }

        return editor;
    }
}
