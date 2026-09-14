namespace AIVoiceActing.UI.State;

using System.Globalization;
using AIVoiceActing.Domain;
using AIVoiceActing.Ports;

/// <summary>
/// Pure Casting-tab brain: an edit buffer over one <see cref="CastingPreset"/> plus the
/// selected name and a dirty flag. Every mutation is a record-replace (the draw layer
/// re-renders from <see cref="EditBuffer"/>); persistence happens when the window saves
/// the buffer through the port. Knows nothing about ImGui or the store.
/// </summary>
public sealed class CastingTabModel
{
    private const string NewSetPrefix = "custom-";

    public CastingPreset EditBuffer { get; private set; } = Empty();

    public string SelectedName { get; private set; } = CastingPreset.DefaultPresetName;

    /// <summary>True when <see cref="EditBuffer"/> differs from what was last loaded/saved.</summary>
    public bool Dirty { get; private set; }

    public static CastingPreset Empty() => new(
        CastingPreset.DefaultPresetName,
        new Dictionary<string, VoiceSlotDto[]>(StringComparer.Ordinal),
        new Dictionary<string, string>(StringComparer.Ordinal),
        new Dictionary<string, ModelOverrideDto>(StringComparer.Ordinal));

    /// <summary>Switches the editor to <paramref name="name"/>, discarding unsaved edits.</summary>
    public void Load(string name, CastingPreset preset)
    {
        this.SelectedName = name;
        this.EditBuffer = preset;
        this.Dirty = false;
    }

    /// <summary>Deep-copies <paramref name="source"/> (slots included — parked sets are
    /// part of the library) under a new name; the fork starts out unsaved.</summary>
    public void Fork(string sourceName, CastingPreset source, string newName)
    {
        var sets = source.Sets.ToDictionary(
            kv => kv.Key,
            kv => kv.Value.Select(slot => slot with { }).ToArray(),
            StringComparer.Ordinal);
        var variants = new Dictionary<string, string>(source.Variants, StringComparer.Ordinal);
        var overrides = source.ModelOverrides.ToDictionary(
            kv => kv.Key,
            kv => kv.Value with { },
            StringComparer.Ordinal);
        this.Load(newName, new CastingPreset(newName, sets, variants, overrides));
        this.Dirty = true;
    }

    /// <summary>Starts a from-scratch preset: one empty set (custom-1) with every race
    /// row and the ungendered row pointing at it.</summary>
    public void NewScratch(string newName)
    {
        var sets = new Dictionary<string, VoiceSlotDto[]>(StringComparer.Ordinal)
        {
            [NextSetName([])] = [],
        };
        var firstSet = sets.Keys.Single();
        var variants = VariantRowKeys().ToDictionary(key => key, _ => firstSet, StringComparer.Ordinal);
        this.Load(newName, new CastingPreset(
            newName, sets, variants, new Dictionary<string, ModelOverrideDto>()));
        this.Dirty = true;
    }

    /// <summary>All variant-grid row keys: the 16 playable race|group rows plus the
    /// ungendered row (keyed as RaceVoiceMap composes its fallback key).</summary>
    public static IReadOnlyList<string> VariantRowKeys() =>
    [
        .. Races.All.SelectMany(race => (string[])[$"{race.Id}|Male", $"{race.Id}|Female"]),
        CastingPreset.UngenderedVariantKey,
    ];

    /// <summary>The next free "custom-N" set name for the given existing keys.</summary>
    public static string NextSetName(IEnumerable<string> existingKeys)
    {
        var highest = existingKeys
            .Select(key => key.StartsWith(NewSetPrefix, StringComparison.Ordinal)
                && int.TryParse(key[NewSetPrefix.Length..], out var n) ? n : 0)
            .DefaultIfEmpty(0)
            .Max();
        return $"{NewSetPrefix}{highest + 1}";
    }

    public void SetVariant(string variantKey, string setKey)
    {
        var variants = new Dictionary<string, string>(this.EditBuffer.Variants, StringComparer.Ordinal)
        {
            [variantKey] = setKey,
        };
        this.EditBuffer = this.EditBuffer with { Variants = variants };
        this.Dirty = true;
    }

    /// <summary>Adds an empty set if the key is not present yet (no-op otherwise).</summary>
    public void EnsureSet(string setKey)
    {
        if (this.EditBuffer.Sets.ContainsKey(setKey))
        {
            return;
        }

        var sets = new Dictionary<string, VoiceSlotDto[]>(this.EditBuffer.Sets, StringComparer.Ordinal)
        {
            [setKey] = [],
        };
        this.EditBuffer = this.EditBuffer with { Sets = sets };
        this.Dirty = true;
    }

    public void SetSlot(string setKey, int index, VoiceSlotDto slot)
    {
        if (this.EditBuffer.Sets.TryGetValue(setKey, out var slots)
            && index >= 0
            && index < slots.Length)
        {
            this.ReplaceSlots(setKey, [.. slots[..index], slot, .. slots[(index + 1)..]]);
        }
    }

    public void AddSlot(string setKey, string defaultVoiceId)
    {
        if (this.EditBuffer.Sets.TryGetValue(setKey, out var slots))
        {
            this.ReplaceSlots(setKey, [.. slots, new VoiceSlotDto(defaultVoiceId)]);
        }
    }

    public void RemoveSlot(string setKey, int index)
    {
        if (this.EditBuffer.Sets.TryGetValue(setKey, out var slots)
            && index >= 0
            && index < slots.Length)
        {
            this.ReplaceSlots(setKey, [.. slots[..index], .. slots[(index + 1)..]]);
        }
    }

    public void SetModelOverride(int modelId, string name, string setKey)
    {
        var overrides = new Dictionary<string, ModelOverrideDto>(this.EditBuffer.ModelOverrides, StringComparer.Ordinal)
        {
            [modelId.ToString(CultureInfo.InvariantCulture)] = new ModelOverrideDto(name, setKey),
        };
        this.EditBuffer = this.EditBuffer with { ModelOverrides = overrides };
        this.Dirty = true;
    }

    public void RemoveModelOverride(int modelId)
    {
        var overrides = new Dictionary<string, ModelOverrideDto>(this.EditBuffer.ModelOverrides, StringComparer.Ordinal);
        if (overrides.Remove(modelId.ToString(CultureInfo.InvariantCulture)))
        {
            this.EditBuffer = this.EditBuffer with { ModelOverrides = overrides };
            this.Dirty = true;
        }
    }

    /// <summary>Clears the dirty flag after a successful save/activation round.</summary>
    public void MarkSaved() => this.Dirty = false;

    private void ReplaceSlots(string setKey, VoiceSlotDto[] slots)
    {
        var sets = new Dictionary<string, VoiceSlotDto[]>(this.EditBuffer.Sets, StringComparer.Ordinal)
        {
            [setKey] = slots,
        };
        this.EditBuffer = this.EditBuffer with { Sets = sets };
        this.Dirty = true;
    }
}
