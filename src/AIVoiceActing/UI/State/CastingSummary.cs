namespace AIVoiceActing.UI.State;

using AIVoiceActing.Domain;
using AIVoiceActing.Ports;

/// <summary>One race/gender row of the casting, summarised for the Voices page.</summary>
public sealed record CastingRowInfo(string Key, string Label, IReadOnlyList<VoiceSlotDto> Slots)
{
    /// <summary>The distinct voice ids of the row, in slot order.</summary>
    public IReadOnlyList<string> VoiceIds => CastingSummary.DistinctIds(this.Slots);
}

/// <summary>One beast tribe, summarised: its three voice lists and how many creature models are bound.</summary>
public sealed record TribeRowInfo(BeastTribeCast Tribe)
{
    /// <summary>The distinct voice ids across the tribe's lists (any gender, male, female).</summary>
    public IReadOnlyList<string> VoiceIds =>
        CastingSummary.DistinctIds([.. this.Tribe.Voices, .. this.Tribe.MaleVoices, .. this.Tribe.FemaleVoices]);
}

/// <summary>
/// Pure read-model of a casting preset for the "Who sounds like who" section: the race/gender
/// rows with plain labels ("Hyur, male"), the tribe rows, and every voice id the casting and the
/// stored characters use (so unresolved ones can be listed as missing voices).
/// </summary>
public static class CastingSummary
{
    public const string UnknownLabel = "Anyone else";

    /// <summary>Every race twice (male, then female), then the catch-all row.</summary>
    public static IReadOnlyList<CastingRowInfo> RaceRows(CastingPreset preset)
    {
        var rows = new List<CastingRowInfo>();
        foreach (var race in Races.All)
        {
            foreach (var female in new[] { false, true })
            {
                var key = CastingDefaults.RaceBucketKey(race.Id, female);
                rows.Add(new CastingRowInfo(key, RowLabel(race.Name, female), SlotsOf(preset, key)));
            }
        }

        rows.Add(new CastingRowInfo(CastingDefaults.UnknownBucketKey, UnknownLabel, SlotsOf(preset, CastingDefaults.UnknownBucketKey)));
        return rows;
    }

    /// <summary>"Hyur, male" or "Miqo'te, female".</summary>
    public static string RowLabel(string raceName, bool female) => $"{raceName}, {(female ? "female" : "male")}";

    public static IReadOnlyList<TribeRowInfo> TribeRows(CastingPreset preset) =>
        [.. preset.BeastTribes.Select(tribe => new TribeRowInfo(tribe))];

    public static IReadOnlyList<string> DistinctIds(IEnumerable<VoiceSlotDto> slots) =>
        [.. slots.Select(slot => slot.Id).Distinct(StringComparer.Ordinal)];

    /// <summary>
    /// Every voice id the preset's rows and tribes use plus the stored characters' voices, sorted
    /// and distinct. Compare against the library to find ids no installed pack provides.
    /// </summary>
    public static string[] ReferencedIds(CastingPreset preset, IEnumerable<VoiceProfile> profiles)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var slot in preset.Buckets.Values.SelectMany(slots => slots))
        {
            ids.Add(slot.Id);
        }

        foreach (var tribe in preset.BeastTribes)
        {
            foreach (var slot in tribe.Voices.Concat(tribe.MaleVoices).Concat(tribe.FemaleVoices))
            {
                ids.Add(slot.Id);
            }
        }

        foreach (var profile in profiles)
        {
            ids.Add(profile.ReferenceVoiceId);
        }

        ids.Remove(string.Empty);
        return [.. ids.Order(StringComparer.Ordinal)];
    }

    private static IReadOnlyList<VoiceSlotDto> SlotsOf(CastingPreset preset, string key) =>
        preset.Buckets.TryGetValue(key, out var slots) ? slots : [];
}
