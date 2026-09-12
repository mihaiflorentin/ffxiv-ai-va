namespace AIVoiceActing.Domain;

using System.Text.Json;
using System.Text.Json.Serialization;

/// <summary>One reference-voice slot: clip id plus the slot's exaggeration bias (additive, 0..1).</summary>
public sealed record VoiceSlot(
    string Id,
    float ExaggerationBias);

/// <summary>
/// Maps voice groups (with race nuances) to bundled reference-voice slots, loaded from
/// <c>voices.json</c>. Sets: <c>male</c>, <c>deepMale</c> (Roegadyn/Hrothgar males — deep,
/// v1: lower biases), <c>female</c>, <c>highPitchFemale</c> (Lalafell/Viera females — high,
/// v1: higher biases), <c>ungendered</c>. Race variants are keyed "<race>|<group>"
/// (e.g. "3|Female"); a missing variant falls back to the group's base set; Unknown and
/// Ungendered speakers share the ungendered set. v1 ships one clip ("default"); groups carry
/// 2–4 slots of that id with distinct biases so deterministic assignment still yields audible
/// variety. The slot shape accepts future real clip ids unchanged.
/// </summary>
public sealed class RaceVoiceMap
{
    private sealed record ManifestFile(
        [property: JsonPropertyName("sets")] Dictionary<string, VoiceSlot[]>? Sets,
        [property: JsonPropertyName("raceVariants")] Dictionary<string, string>? RaceVariants);

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private readonly Dictionary<string, VoiceSlot[]> sets;
    private readonly Dictionary<string, string> raceVariants;

    public RaceVoiceMap(Dictionary<string, VoiceSlot[]> sets, Dictionary<string, string> raceVariants)
    {
        this.sets = new Dictionary<string, VoiceSlot[]>(sets, StringComparer.Ordinal);
        this.raceVariants = new Dictionary<string, string>(raceVariants, StringComparer.Ordinal);
    }

    public static RaceVoiceMap FromJson(string json)
    {
        var manifest = JsonSerializer.Deserialize<ManifestFile>(json, JsonOptions)
            ?? throw new InvalidOperationException("voices.json deserialized to null.");
        return new RaceVoiceMap(manifest.Sets ?? [], manifest.RaceVariants ?? []);
    }

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
