namespace AIVoiceActing.Domain;

/// <summary>Which of a beast tribe's voice lists a bucket key addresses: the tribe pool
/// (unknown gender / fallback), or a gendered list that overrides the pool once filled.</summary>
public enum BeastVoiceList
{
    Pool,
    Male,
    Female,
}

/// <summary>
/// The fixed shape of the casting grid: stable bucket-key composition for race/gender
/// rows and beast-tribe rows, plus the known beast-tribe roster (allied societies and
/// savage tribes). Pure data — the manifest carries the voices, the presets carry the
/// user's edits; this type only fixes the keys everything else agrees on.
/// </summary>
public static class CastingDefaults
{
    /// <summary>One known beast tribe: stable key, display name, and the parked
    /// <c>voices.json</c> set that seeds the tribe's pool in the Default preset.</summary>
    /// <param name="Key">Stable ascii id used in bucket keys and preset persistence.</param>
    /// <param name="Name">Display name for the UI.</param>
    /// <param name="ManifestSetKey">The v0.0.24 parked set key casting this tribe.</param>
    public sealed record Tribe(string Key, string Name, string ManifestSetKey);

    /// <summary>Every beast tribe the caster knows, roster order (alphabetical).</summary>
    public static IReadOnlyList<Tribe> Tribes { get; } =
    [
        new("amaljaa", "Amalj'aa", "setAmaljaa"),
        new("ananta", "Ananta", "setAnanta"),
        new("arkasodara", "Arkasodara", "setArkasodara"),
        new("dragon", "Dragon", "setDragon"),
        new("dwarf", "Dwarf", "setDwarf"),
        new("garlean", "Garlean", "setGarlean"),
        new("goblin", "Goblin", "setGoblin"),
        new("ixal", "Ixal", "setIxal"),
        new("kobold", "Kobold", "setKobold"),
        new("kojin", "Kojin", "setKojin"),
        new("loporrit", "Loporrit", "setLoporrit"),
        new("mamoolja", "Mamool Ja", "setMamoolJa"),
        new("moogle", "Moogle", "setMoogle"),
        new("namazu", "Namazu", "setNamazu"),
        new("numou", "Nu Mou", "setNuMou"),
        new("omicron", "Omicron", "setOmicron"),
        new("pelupelu", "Pelupelu", "setPelupelu"),
        new("pixie", "Pixie", "setPixie"),
        new("qitari", "Qitari", "setQitari"),
        new("sahagin", "Sahagin", "setSahagin"),
        new("sylph", "Sylph", "setSylph"),
        new("vanuvanu", "Vanu Vanu", "setVanuVanu"),
        new("vath", "Vath", "setVath"),
        new("yokhuy", "Yok Huy", "setYokHuy"),
    ];

    /// <summary>The race/gender bucket key for speakers whose identity cannot be read
    /// (and the explicit "Unknown" row of the General Voices tab).</summary>
    public const string UnknownBucketKey = "ungendered";

    /// <summary>Bucket key for a playable race/gender row: "1|Male" .. "8|Female".</summary>
    public static string RaceBucketKey(byte race, bool female) =>
        $"{race}|{(female ? "Female" : "Male")}";

    /// <summary>Bucket key for a beast-tribe voice list: "bt:&lt;tribe&gt;" for the pool,
    /// "bt:&lt;tribe&gt;|Male" / "bt:&lt;tribe&gt;|Female" for the gendered lists.</summary>
    public static string BeastBucketKey(string tribeKey, BeastVoiceList list) => list switch
    {
        BeastVoiceList.Male => $"bt:{tribeKey}|Male",
        BeastVoiceList.Female => $"bt:{tribeKey}|Female",
        _ => $"bt:{tribeKey}",
    };
}
