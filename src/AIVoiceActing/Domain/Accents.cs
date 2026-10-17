namespace AIVoiceActing.Domain;

/// <summary>One canonical accent: a stable code, a UI label, and the G2P that suits it by default.</summary>
public sealed record AccentInfo(string Code, string Label, string DefaultPhonemizer);

/// <summary>
/// The canonical accent list voices are filed under. Codes are BCP-47-like and stable (they
/// are stored in pack manifests); labels are what the UI shows. English regional accents
/// default to the American or British phonemizer; pack authors override per voice.
/// </summary>
public static class Accents
{
    public static readonly IReadOnlyList<AccentInfo> All =
    [
        new("en-US", "American", Phonemizers.EnglishUs),
        new("en-GB", "British", Phonemizers.EnglishGb),
        new("en-IE", "Irish", Phonemizers.EnglishGb),
        new("en-GB-SCT", "Scottish", Phonemizers.EnglishGb),
        new("en-GB-WLS", "Welsh", Phonemizers.EnglishGb),
        new("en-GB-NIR", "Northern Irish", Phonemizers.EnglishGb),
        new("en-GB-NTH", "Northern English", Phonemizers.EnglishGb),
        new("en-GB-MID", "Midlands English", Phonemizers.EnglishGb),
        new("en-GB-STH", "Southern English", Phonemizers.EnglishGb),
        new("en-AU", "Australian", Phonemizers.EnglishGb),
        new("en-NZ", "New Zealand", Phonemizers.EnglishGb),
        new("en-ZA", "South African", Phonemizers.EnglishGb),
        new("en-IN", "Indian", Phonemizers.EnglishGb),
        new("en-CA", "Canadian", Phonemizers.EnglishUs),
        new("es-ES", "Spanish", Phonemizers.Spanish),
        new("fr-FR", "French", Phonemizers.French),
        new("it-IT", "Italian", Phonemizers.Italian),
        new("pt-BR", "Brazilian Portuguese", Phonemizers.Portuguese),
        new("hi-IN", "Hindi", Phonemizers.Hindi),
        new("ja-JP", "Japanese", Phonemizers.Japanese),
        new("zh-CN", "Mandarin Chinese", Phonemizers.Chinese),
    ];

    private static readonly Dictionary<string, AccentInfo> ByCode =
        All.ToDictionary(accent => accent.Code, StringComparer.OrdinalIgnoreCase);

    /// <summary>True for a code on the canonical list (case-insensitive).</summary>
    public static bool IsKnown(string? code) => code is not null && ByCode.ContainsKey(code);

    /// <summary>The accent for a code, or null.</summary>
    public static AccentInfo? Find(string? code) =>
        code is not null && ByCode.TryGetValue(code, out var accent) ? accent : null;

    /// <summary>UI label for a code; unknown codes show as themselves.</summary>
    public static string LabelFor(string? code) => Find(code)?.Label ?? code ?? string.Empty;

    /// <summary>Canonical-cased code for a case-insensitive match, else null.</summary>
    public static string? Canonical(string? code) => Find(code)?.Code;

    /// <summary>The accent a Kokoro bank voice belongs to, from its language prefix.</summary>
    public static string ForKokoroLanguage(VoiceLanguage language) => language switch
    {
        VoiceLanguage.AmericanEnglish => "en-US",
        VoiceLanguage.BritishEnglish => "en-GB",
        VoiceLanguage.Spanish => "es-ES",
        VoiceLanguage.French => "fr-FR",
        VoiceLanguage.Italian => "it-IT",
        VoiceLanguage.Portuguese => "pt-BR",
        VoiceLanguage.Hindi => "hi-IN",
        VoiceLanguage.Japanese => "ja-JP",
        VoiceLanguage.Chinese => "zh-CN",
        _ => "en-US",
    };
}

/// <summary>
/// Grapheme-to-phoneme languages a style tensor can be paired with. Kokoro derives a
/// voice's language from the first letter of its internal name (KokoroSharp's
/// <c>GetLanguage</c>, which also requires <c>name[2] == '_'</c>), so a phonemizer maps
/// to that letter and <see cref="InternalName"/> builds a name that selects it.
/// </summary>
public static class Phonemizers
{
    public const string EnglishUs = "en-us";
    public const string EnglishGb = "en-gb";
    public const string Spanish = "es";
    public const string French = "fr";
    public const string Italian = "it";
    public const string Portuguese = "pt-br";
    public const string Hindi = "hi";
    public const string Japanese = "ja";
    public const string Chinese = "zh";

    private static readonly Dictionary<string, char> Prefixes = new(StringComparer.Ordinal)
    {
        [EnglishUs] = 'a',
        [EnglishGb] = 'b',
        [Spanish] = 'e',
        [French] = 'f',
        [Italian] = 'i',
        [Portuguese] = 'p',
        [Hindi] = 'h',
        [Japanese] = 'j',
        [Chinese] = 'z',
    };

    /// <summary>Normalizes a manifest value (<c>en-US</c>, or the Kokoro letters <c>a</c>/<c>b</c>) to a known phonemizer, else null.</summary>
    public static string? Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var key = value.Trim().ToLowerInvariant();
        if (Prefixes.ContainsKey(key))
        {
            return key;
        }

        return key.Length == 1
            ? Prefixes.Where(pair => pair.Value == key[0]).Select(pair => pair.Key).FirstOrDefault()
            : null;
    }

    /// <summary>Kokoro language letter for a normalized phonemizer.</summary>
    public static char PrefixFor(string phonemizer) =>
        Prefixes.TryGetValue(phonemizer, out var prefix) ? prefix : 'a';

    /// <summary>Phonemizer of a Kokoro bank id by its language letter.</summary>
    public static string ForKokoroId(string voiceId) =>
        VoiceCatalog.Describe(voiceId).Language switch
        {
            VoiceLanguage.BritishEnglish => EnglishGb,
            VoiceLanguage.Spanish => Spanish,
            VoiceLanguage.French => French,
            VoiceLanguage.Italian => Italian,
            VoiceLanguage.Portuguese => Portuguese,
            VoiceLanguage.Hindi => Hindi,
            VoiceLanguage.Japanese => Japanese,
            VoiceLanguage.Chinese => Chinese,
            _ => EnglishUs,
        };

    /// <summary>
    /// The name a pack voice is registered under inside Kokoro: <c>{lang}{sex}_pk_{id}</c>
    /// (for example <c>bm_pk_isles.ie_m_01</c>). The public id never changes; only this
    /// internal name carries the phonemizer. The <c>pk_</c> marker keeps it clear of every
    /// stock bank name.
    /// </summary>
    public static string InternalName(string voiceId, string phonemizer, string sex) =>
        $"{PrefixFor(phonemizer)}{(sex == "male" ? 'm' : 'f')}_pk_{voiceId}";
}
