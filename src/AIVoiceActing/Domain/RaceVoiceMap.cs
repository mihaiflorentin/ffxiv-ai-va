namespace AIVoiceActing.Domain;

using System.Text.Json;
using System.Text.Json.Serialization;

/// <summary>
/// One voice slot: id plus per-slot performance knobs. <paramref name="Pitch"/> is a
/// playback multiplier (1 = natural; Lalafell/child-like sets use ~1.18) applied by the
/// synthesizer as a resample. <paramref name="Speed"/> paces the read (1 = natural;
/// the Lalafell sets use 1.1 for the cheerful, bubbly cadence).
/// </summary>
public sealed record VoiceSlot(string Id, float ExaggerationBias = 0f, float Pitch = 1f, float Speed = 1f);

/// <summary>
/// Maps voice groups (with per-race accent variants) to bundled reference-voice slots,
/// loaded from <c>voices.json</c>. Active sets cover every playable race with a
/// lore-fitting accent cast; race variants are keyed "&lt;race&gt;|&lt;group&gt;"
/// (e.g. "3|Male"); a missing variant falls back to the group's base set. Unknown and
/// Ungendered speakers share the ungendered set.
/// </summary>
public sealed class RaceVoiceMap
{
    private sealed record ManifestFile(
        [property: JsonPropertyName("sets")] Dictionary<string, VoiceSlot[]>? Sets,
        [property: JsonPropertyName("raceVariants")] Dictionary<string, string>? RaceVariants,
        [property: JsonPropertyName("disabledSets")] ManifestFile? DisabledSets);

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private readonly Dictionary<string, VoiceSlot[]> sets;
    private readonly Dictionary<string, string> raceVariants;

    public RaceVoiceMap(
        Dictionary<string, VoiceSlot[]> sets,
        Dictionary<string, string> raceVariants,
        RaceVoiceMap? disabled = null)
    {
        this.sets = new Dictionary<string, VoiceSlot[]>(sets, StringComparer.Ordinal);
        this.raceVariants = new Dictionary<string, string>(raceVariants, StringComparer.Ordinal);
        this.Disabled = disabled;
    }

    /// <summary>Active sets by key (read-only view; parked/disabled sets are NOT here).</summary>
    public IReadOnlyDictionary<string, VoiceSlot[]> Sets => this.sets;

    /// <summary>Active race-variant keys ("&lt;race&gt;|&lt;group&gt;" → set name).</summary>
    public IReadOnlyDictionary<string, string> RaceVariants => this.raceVariants;

    public static RaceVoiceMap FromJson(string json)
    {
        var manifest = JsonSerializer.Deserialize<ManifestFile>(json, JsonOptions)
            ?? throw new InvalidOperationException("voices.json deserialized to null.");

        // "disabledSets" holds PARKED accent implementations (true Icelandic/Scottish/
        // Scouse/Nordic/Japanese flavours that need F5 reference clips and model-id
        // wiring). NEVER REMOVE that block from voices.json: it is the casting work,
        // preserved so enabling an accent is a data change, not a rewrite. The loader
        // parses it into a side map that the UI never sees (DistinctVoiceIds/SlotsFor
        // read only the active sets), which is what keeps parked voices out of the picker.
        var disabledSets = manifest.DisabledSets;
        var disabled = disabledSets is null
            ? null
            : new RaceVoiceMap(disabledSets.Sets ?? [], disabledSets.RaceVariants ?? []);

        return new RaceVoiceMap(manifest.Sets ?? [], manifest.RaceVariants ?? [], disabled);
    }

    /// <summary>The parked accent implementations; never shown in the UI, never removed.</summary>
    public RaceVoiceMap? Disabled { get; }

    /// <summary>The slot set for a voice group, nuanced by race; defensive copy.</summary>
    public VoiceSlot[] SlotsFor(VoiceGroup group, byte? race) =>
        (VoiceSlot[])this.ResolveSet(group, race).Clone();

    /// <summary>Candidate voice ids for a group, one entry per slot (hash-ready order).</summary>
    public string[] VoicesFor(VoiceGroup group, byte? race) =>
        this.ResolveSet(group, race).Select(slot => slot.Id).ToArray();

    /// <summary>All distinct reference-voice ids across every set, stable order — the UI
    /// voice-picker options (one clip may back many slots).</summary>
    public string[] DistinctVoiceIds() =>
        [.. this.sets.Values.SelectMany(slots => slots).Select(slot => slot.Id).Distinct()];

    private VoiceSlot[] ResolveSet(VoiceGroup group, byte? race)
    {
        if (race is { } raceId
            && this.raceVariants.TryGetValue($"{raceId}|{group}", out var variantKey)
            && this.sets.TryGetValue(variantKey, out var variant))
        {
            return variant;
        }

        return this.sets.TryGetValue(FallbackKey(group), out var set)
            ? set
            : throw new InvalidOperationException(
                $"RaceVoiceMap has no \"{FallbackKey(group)}\" set for group {group}.");
    }

    private static string FallbackKey(VoiceGroup group) => group switch
    {
        VoiceGroup.Male => "male",
        VoiceGroup.Female => "female",
        _ => "ungendered",
    };
}
