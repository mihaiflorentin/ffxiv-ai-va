namespace AIVoiceActing.Domain;

/// <summary>Accent bank a Kokoro voice id belongs to, parsed from the id prefix.</summary>
public enum VoiceLanguage
{
    Unknown,
    AmericanEnglish,
    BritishEnglish,
    Spanish,
    French,
    Italian,
    Portuguese,
    Hindi,
    Japanese,
    Chinese,
}

/// <summary>Intended voice sex of a Kokoro voice id, parsed from the id prefix.</summary>
public enum VoiceSex
{
    Unknown,
    Female,
    Male,
}

/// <summary>One entry of the installed Kokoro voice bank: id plus prefix-derived language/sex.</summary>
public sealed record VoiceCatalogEntry(string Id, VoiceLanguage Language, VoiceSex Sex);

/// <summary>
/// Pure catalog over the installed Kokoro voice bank (voices/*.npy). Kokoro ids carry
/// their accent and sex in a two-char prefix ("af_heart" = American English female,
/// "zm_yunjian" = Chinese male); <see cref="Describe"/> parses that prefix and
/// <see cref="FromDirectory"/> enumerates a bank directory. No I/O beyond the one
/// enumeration; an absent or empty bank yields an empty catalog so the UI can degrade
/// to raw id pickers.
/// </summary>
public static class VoiceCatalog
{
    /// <summary>Parses a Kokoro voice id prefix; null, short, or unmatched ids
    /// describe as Unknown.</summary>
    public static VoiceCatalogEntry Describe(string? voiceId)
    {
        if (voiceId is not { Length: >= 4 } id
            || id[2] != '_')
        {
            return new VoiceCatalogEntry(voiceId ?? string.Empty, VoiceLanguage.Unknown, VoiceSex.Unknown);
        }

        var language = id[0] switch
        {
            'a' => VoiceLanguage.AmericanEnglish,
            'b' => VoiceLanguage.BritishEnglish,
            'e' => VoiceLanguage.Spanish,
            'f' => VoiceLanguage.French,
            'i' => VoiceLanguage.Italian,
            'p' => VoiceLanguage.Portuguese,
            'h' => VoiceLanguage.Hindi,
            'j' => VoiceLanguage.Japanese,
            'z' => VoiceLanguage.Chinese,
            _ => VoiceLanguage.Unknown,
        };

        // An unmatched language letter means the id is not a Kokoro bank id at all;
        // the sex letter carries no meaning then either.
        if (language == VoiceLanguage.Unknown)
        {
            return new VoiceCatalogEntry(voiceId, VoiceLanguage.Unknown, VoiceSex.Unknown);
        }

        var sex = id[1] switch
        {
            'f' => VoiceSex.Female,
            'm' => VoiceSex.Male,
            _ => VoiceSex.Unknown,
        };

        return new VoiceCatalogEntry(voiceId, language, sex);
    }

    /// <summary>Enumerates a voice-bank directory ("*.npy"), newest-order irrelevant:
    /// sorted by id for stable pickers. A missing or unreadable directory yields an
    /// empty catalog (never throws — off-bank installs keep working).</summary>
    public static IReadOnlyList<VoiceCatalogEntry> FromDirectory(string voicesDir)
    {
        if (string.IsNullOrWhiteSpace(voicesDir) || !Directory.Exists(voicesDir))
        {
            return [];
        }

        try
        {
            return [.. Directory.EnumerateFiles(voicesDir, "*.npy")
                .Select(Path.GetFileNameWithoutExtension)
                .Where(id => !string.IsNullOrEmpty(id))
                .Select(Describe)
                .OrderBy(entry => entry.Id, StringComparer.Ordinal)];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>Filters catalog entries: every null/empty argument is a no-filter pass,
    /// and <paramref name="contains"/> is a case-insensitive substring match on the id.</summary>
    public static IReadOnlyList<VoiceCatalogEntry> Filter(
        IEnumerable<VoiceCatalogEntry> entries,
        VoiceLanguage? language,
        VoiceSex? sex,
        string? contains)
    {
        var needle = string.IsNullOrWhiteSpace(contains) ? null : contains.Trim();
        return [.. entries.Where(entry =>
            (language is null || entry.Language == language)
            && (sex is null || entry.Sex == sex)
            && (needle is null || entry.Id.Contains(needle, StringComparison.OrdinalIgnoreCase)))];
    }
}
