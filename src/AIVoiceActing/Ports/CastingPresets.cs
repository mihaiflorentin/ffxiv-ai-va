namespace AIVoiceActing.Ports;

using AIVoiceActing.Domain;

/// <summary>Thrown by <see cref="ICastingPresetStore"/> when a preset operation is invalid
/// (unknown name, empty name, or mutation of the immutable built-in preset).</summary>
public sealed class CastingPresetException(string message, Exception? inner = null)
    : Exception(message, inner);

/// <summary>One persisted voice slot of a casting set: id plus per-slot performance knobs
/// (the persistence shape of <see cref="Domain.VoiceSlot"/>).</summary>
public sealed record VoiceSlotDto(
    string Id,
    float ExaggerationBias = 0f,
    float Pitch = 1f,
    float Speed = 1f,
    float Volume = 1f);

/// <summary>One model-id override: game model chara id → named voice set, with the human
/// label the TextToTalk-style override table carries.</summary>
public sealed record ModelOverrideDto(string Name, string SetKey);

/// <summary>
/// A named casting: which voice sets exist (<paramref name="Sets"/>), which set each
/// race/gender row uses (<paramref name="Variants"/>, keyed "&lt;raceId&gt;|&lt;Group&gt;"
/// exactly as <see cref="Domain.RaceVoiceMap"/> resolves variants), and which model ids
/// bind to which sets (<paramref name="ModelOverrides"/>). The built-in "Default" preset
/// mirrors the shipped <c>voices.json</c> casting (parked sets included) and is immutable.
/// </summary>
public sealed record CastingPreset(
    string Name,
    IReadOnlyDictionary<string, VoiceSlotDto[]> Sets,
    IReadOnlyDictionary<string, string> Variants,
    IReadOnlyDictionary<string, ModelOverrideDto> ModelOverrides)
{
    /// <summary>The immutable built-in preset name.</summary>
    public const string DefaultPresetName = "Default";

    /// <summary>Variant-grid row for the Ungendered group, keyed as
    /// <see cref="Domain.RaceVoiceMap"/> composes its fallback key; activating a preset
    /// re-points the fallback set to this row's target.</summary>
    public const string UngenderedVariantKey = "ungendered";
}

/// <summary>Voice-slot conversions between the persistence DTO and the domain slot.</summary>
public static class CastingPresetConversion
{
    public static VoiceSlot ToSlot(this VoiceSlotDto dto) =>
        new(dto.Id, dto.ExaggerationBias, dto.Pitch, dto.Speed, dto.Volume);

    public static VoiceSlotDto ToDto(this VoiceSlot slot) =>
        new(slot.Id, slot.ExaggerationBias, slot.Pitch, slot.Speed, slot.Volume);

    public static Dictionary<string, VoiceSlot[]> ToSlots(
        this IReadOnlyDictionary<string, VoiceSlotDto[]> sets) =>
        sets.ToDictionary(kv => kv.Key, kv => kv.Value.Select(ToSlot).ToArray(), StringComparer.Ordinal);

    public static Dictionary<string, VoiceSlotDto[]> ToDtos(
        this IReadOnlyDictionary<string, VoiceSlot[]> sets) =>
        sets.ToDictionary(kv => kv.Key, kv => kv.Value.Select(ToDto).ToArray(), StringComparer.Ordinal);
}

/// <summary>
/// Driven port over the persisted casting presets. "Default" is always present and
/// derived from the base voice map; user presets live in a JSON file, are forked or
/// built from scratch, and exactly one is active (overlaying the base casting on the
/// next voice-map build).
/// </summary>
public interface ICastingPresetStore
{
    /// <summary>The active preset name; never empty ("Default" before the first activation).</summary>
    string ActivePresetName { get; }

    /// <summary>"Default" first, then user presets sorted ordinal.</summary>
    IReadOnlyList<string> PresetNames { get; }

    /// <summary>Derives the built-in Default preset from the current base voice map on
    /// every call: active sets plus parked sets, variants as shipped.</summary>
    CastingPreset GetDefault();

    /// <summary>Loads a user preset; "Default" is not stored — use <see cref="GetDefault"/>.
    /// A missing name throws <see cref="CastingPresetException"/>.</summary>
    CastingPreset Get(string name);

    /// <summary>Upserts a user preset. An empty name or the name "Default" throws
    /// <see cref="CastingPresetException"/>.</summary>
    void Save(CastingPreset preset);

    /// <summary>Deletes a user preset. Deleting "Default" throws; deleting the active
    /// preset resets <see cref="ActivePresetName"/> to "Default".</summary>
    void Delete(string name);

    /// <summary>Makes <paramref name="name"/> the active preset; the name must exist
    /// (or be "Default"). Persists immediately.</summary>
    void Activate(string name);
}
