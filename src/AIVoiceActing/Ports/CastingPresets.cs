namespace AIVoiceActing.Ports;

using AIVoiceActing.Domain;

/// <summary>Thrown by <see cref="ICastingPresetStore"/> when a preset operation is invalid
/// (unknown name, empty name, or mutation of the immutable built-in preset).</summary>
public sealed class CastingPresetException(string message, Exception? inner = null)
    : Exception(message, inner);

/// <summary>One persisted voice slot of a casting row: id plus per-slot performance knobs
/// (the persistence shape of <see cref="Domain.VoiceSlot"/>).</summary>
public sealed record VoiceSlotDto(
    string Id,
    float ExaggerationBias = 0f,
    float Pitch = 1f,
    float Speed = 1f,
    float Volume = 1f);

/// <summary>One beast-tribe row of a casting preset. The tribe's voice pool serves every
/// speaker of the tribe; gendered lists override the pool once filled. Model ids bound
/// here route game model chara ids to the tribe (harvested from the Conversation log
/// lines); empty until the user binds them.</summary>
/// <param name="Key">Stable ascii id, e.g. "amaljaa".</param>
/// <param name="Name">Display name, e.g. "Amalj'aa".</param>
/// <param name="ModelIds">Bound model chara ids (empty until the user binds via logs).</param>
/// <param name="Voices">Tribe pool: the tribe's default/fallback list.</param>
/// <param name="MaleVoices">Empty until the user splits by gender.</param>
/// <param name="FemaleVoices">Empty until the user splits by gender.</param>
public sealed record BeastTribeCast(
    string Key,
    string Name,
    IReadOnlyList<int> ModelIds,
    IReadOnlyList<VoiceSlotDto> Voices,
    IReadOnlyList<VoiceSlotDto> MaleVoices,
    IReadOnlyList<VoiceSlotDto> FemaleVoices);

/// <summary>
/// A named casting: which voice slots each race/gender row uses (<paramref name="Buckets"/>,
/// keyed "&lt;raceId&gt;|Male|Female" via <see cref="Domain.CastingDefaults.RaceBucketKey"/> plus
/// <see cref="Domain.CastingDefaults.UnknownBucketKey"/>), and how each beast tribe is cast
/// (<paramref name="BeastTribes"/>). The built-in "Default" preset mirrors the shipped
/// <c>voices.json</c> casting and is immutable.
/// </summary>
public sealed record CastingPreset(
    string Name,
    IReadOnlyDictionary<string, VoiceSlotDto[]> Buckets,
    IReadOnlyList<BeastTribeCast> BeastTribes)
{
    /// <summary>The immutable built-in preset name.</summary>
    public const string DefaultPresetName = "Default";
}

/// <summary>Voice-slot conversions between the persistence DTO and the domain slot.</summary>
public static class CastingPresetConversion
{
    public static VoiceSlot ToSlot(this VoiceSlotDto dto) =>
        new(dto.Id, dto.ExaggerationBias, dto.Pitch, dto.Speed, dto.Volume);

    public static VoiceSlotDto ToDto(this VoiceSlot slot) =>
        new(slot.Id, slot.ExaggerationBias, slot.Pitch, slot.Speed, slot.Volume);

    public static VoiceSlot[] ToSlotArray(this IReadOnlyList<VoiceSlotDto> dtos) =>
        [.. dtos.Select(ToSlot)];

    public static VoiceSlotDto[] ToDtoArray(this IReadOnlyList<VoiceSlot> slots) =>
        [.. slots.Select(ToDto)];
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
    /// every call: the race/gender buckets plus one beast-tribe row per known tribe.</summary>
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
