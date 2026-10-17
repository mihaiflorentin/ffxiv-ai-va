namespace AIVoiceActing.Domain;

using System.Text.Json;
using System.Text.RegularExpressions;
using AIVoiceActing.Ports;

/// <summary>Pack-level license block; <see cref="Spdx"/> is mandatory.</summary>
public sealed record PackLicense(string Spdx, string? Attribution, string? Url);

/// <summary>One realization as declared in a manifest. <see cref="File"/> is pack-relative with forward slashes.</summary>
public sealed record PackRealization(
    RealizationKind Kind,
    string Engine,
    string File,
    string? Phonemizer,
    string? Transcript);

/// <summary>One voice as declared in a manifest, already normalized (canonical accent code, lowercase sex).</summary>
public sealed record PackVoice(
    string Id,
    string Name,
    string Accent,
    string Sex,
    string AgeBand,
    string Description,
    IReadOnlyList<string> Tags,
    VoiceQuality Quality,
    string? PreviewFile,
    IReadOnlyList<PackRealization> Realizations);

/// <summary>
/// A parsed and structurally validated <c>voicepack.json</c> (schema 1). Parsing is pure: it
/// checks the schema version, id charset and uniqueness, license presence, accent/sex/age
/// vocabularies and that every referenced path is a safe pack-relative path (no rooted
/// paths, no <c>..</c>). Whether those files exist is a separate check
/// (<see cref="FindMissingFiles"/>) because a missing file demotes one voice rather than
/// rejecting the pack. Any structural fault throws <see cref="VoicePackInvalidException"/>.
/// </summary>
public sealed partial record VoicePackManifest(
    int Schema,
    string Id,
    string Name,
    string Version,
    PackLicense License,
    IReadOnlyList<PackVoice> Voices)
{
    public const int CurrentSchema = 1;
    public const string FileName = "voicepack.json";
    public const string KokoroEngine = "kokoro";
    public const string AnyCloningEngine = "*";

    private const int MaxIdLength = 64;
    private const int MaxFileLength = 200;

    private static readonly string[] Sexes = ["female", "male", "neutral"];
    private static readonly string[] AgeBands = ["child", "young", "adult", "senior"];

    [GeneratedRegex("^[a-z0-9._-]+$")]
    private static partial Regex IdPattern();

    /// <summary>True for ids made only of <c>[a-z0-9._-]</c>, at most 64 characters.</summary>
    public static bool IsValidId(string? id) =>
        id is { Length: > 0 and <= MaxIdLength } && IdPattern().IsMatch(id);

    /// <summary>
    /// True for a relative path inside the pack: forward slashes only, no rooted or drive
    /// paths, no empty, <c>.</c> or <c>..</c> segments, no control characters.
    /// </summary>
    public static bool IsSafeRelativePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > MaxFileLength
            || path.StartsWith('/') || path.Contains('\\') || path.Contains(':')
            || path.Any(char.IsControl))
        {
            return false;
        }

        return path.Split('/').All(segment => segment.Length > 0 && segment is not ("." or ".."));
    }

    public static VoicePackManifest Parse(string json)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        }
        catch (JsonException ex)
        {
            throw new VoicePackInvalidException($"voicepack.json is not valid JSON: {ex.Message}", ex);
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new VoicePackInvalidException("voicepack.json must be a JSON object.");
            }

            if (!root.TryGetProperty("schema", out var schemaElement)
                || schemaElement.ValueKind != JsonValueKind.Number
                || !schemaElement.TryGetInt32(out var schema))
            {
                throw new VoicePackInvalidException("voicepack.json has no numeric \"schema\" version.");
            }

            if (schema != CurrentSchema)
            {
                throw new VoicePackInvalidException(
                    $"Unsupported voicepack schema {schema} (this version reads schema {CurrentSchema}).");
            }

            var id = RequireId(root, "id", "pack id");
            var name = RequireText(root, "name", 80);
            var version = RequireText(root, "version", 32);
            var license = ParseLicense(root);

            if (!root.TryGetProperty("voices", out var voicesElement) || voicesElement.ValueKind != JsonValueKind.Array)
            {
                throw new VoicePackInvalidException("voicepack.json has no \"voices\" array.");
            }

            var voices = new List<PackVoice>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var element in voicesElement.EnumerateArray())
            {
                var voice = ParseVoice(element);
                if (!seen.Add(voice.Id))
                {
                    throw new VoicePackInvalidException($"Voice id \"{voice.Id}\" appears twice in the pack.");
                }

                voices.Add(voice);
            }

            return new VoicePackManifest(schema, id, name, version, license, voices);
        }
    }

    /// <summary>
    /// Every (voice id, relative file) the manifest references for which <paramref name="exists"/>
    /// is false. Style tensors, clips and previews are included; transcripts are optional
    /// extras and reported too so the caller can word the problem.
    /// </summary>
    public IReadOnlyList<(string VoiceId, string File)> FindMissingFiles(Func<string, bool> exists)
    {
        var missing = new List<(string, string)>();
        foreach (var voice in this.Voices)
        {
            var files = voice.Realizations.SelectMany(r => new[] { r.File, r.Transcript })
                .Append(voice.PreviewFile)
                .Where(file => file is not null)
                .Select(file => file!)
                .Distinct(StringComparer.Ordinal);
            missing.AddRange(files.Where(file => !exists(file)).Select(file => (voice.Id, file)));
        }

        return missing;
    }

    private static PackLicense ParseLicense(JsonElement root)
    {
        if (!root.TryGetProperty("license", out var element) || element.ValueKind != JsonValueKind.Object)
        {
            throw new VoicePackInvalidException("voicepack.json has no \"license\" block; a pack without a license is rejected.");
        }

        var spdx = RequireText(element, "spdx", 64);
        return new PackLicense(spdx, OptionalText(element, "attribution", 2000), OptionalText(element, "url", 500));
    }

    private static PackVoice ParseVoice(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new VoicePackInvalidException("Every entry of \"voices\" must be an object.");
        }

        var id = RequireId(element, "id", "voice id");
        var name = RequireText(element, "name", 80);

        var accentText = RequireText(element, "accent", 32);
        var accent = Accents.Canonical(accentText)
            ?? throw new VoicePackInvalidException($"Voice \"{id}\" has unknown accent \"{accentText}\".");

        var sex = RequireText(element, "sex", 16).ToLowerInvariant();
        if (!Sexes.Contains(sex))
        {
            throw new VoicePackInvalidException($"Voice \"{id}\" has sex \"{sex}\"; expected female, male or neutral.");
        }

        var age = (OptionalText(element, "age", 16) ?? "adult").ToLowerInvariant();
        if (!AgeBands.Contains(age))
        {
            throw new VoicePackInvalidException($"Voice \"{id}\" has age \"{age}\"; expected child, young, adult or senior.");
        }

        var qualityText = (OptionalText(element, "quality", 16) ?? "experimental").ToLowerInvariant();
        var quality = qualityText switch
        {
            "verified" => VoiceQuality.Verified,
            "experimental" => VoiceQuality.Experimental,
            _ => throw new VoicePackInvalidException(
                $"Voice \"{id}\" has quality \"{qualityText}\"; packs may use verified or experimental."),
        };

        var tags = new List<string>();
        if (element.TryGetProperty("tags", out var tagsElement))
        {
            if (tagsElement.ValueKind != JsonValueKind.Array)
            {
                throw new VoicePackInvalidException($"Voice \"{id}\": \"tags\" must be an array of strings.");
            }

            foreach (var tag in tagsElement.EnumerateArray())
            {
                if (tag.ValueKind != JsonValueKind.String || CleanText(tag.GetString(), 32) is not { } cleaned)
                {
                    throw new VoicePackInvalidException($"Voice \"{id}\" has an invalid tag.");
                }

                tags.Add(cleaned);
            }
        }

        var preview = OptionalText(element, "previewFile", MaxFileLength);
        if (preview is not null && !IsSafeRelativePath(preview))
        {
            throw new VoicePackInvalidException($"Voice \"{id}\": previewFile \"{preview}\" is not a safe relative path.");
        }

        if (!element.TryGetProperty("realizations", out var realizationsElement)
            || realizationsElement.ValueKind != JsonValueKind.Array
            || realizationsElement.GetArrayLength() == 0)
        {
            throw new VoicePackInvalidException($"Voice \"{id}\" declares no realizations.");
        }

        var realizations = realizationsElement.EnumerateArray()
            .Select(r => ParseRealization(id, accent, r))
            .ToList();

        return new PackVoice(
            id,
            name,
            accent,
            sex,
            age,
            OptionalText(element, "description", 500) ?? string.Empty,
            tags,
            quality,
            preview,
            realizations);
    }

    private static PackRealization ParseRealization(string voiceId, string accent, JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new VoicePackInvalidException($"Voice \"{voiceId}\": every realization must be an object.");
        }

        var kindText = RequireText(element, "kind", 32);
        var file = RequireText(element, "file", MaxFileLength);
        if (!IsSafeRelativePath(file))
        {
            throw new VoicePackInvalidException(
                $"Voice \"{voiceId}\": file \"{file}\" is not a safe relative path (no rooted paths, backslashes or \"..\").");
        }

        string? transcript = OptionalText(element, "transcript", MaxFileLength);
        if (transcript is not null && !IsSafeRelativePath(transcript))
        {
            throw new VoicePackInvalidException($"Voice \"{voiceId}\": transcript \"{transcript}\" is not a safe relative path.");
        }

        if (string.Equals(kindText, nameof(RealizationKind.StyleTensor), StringComparison.OrdinalIgnoreCase))
        {
            if (!file.EndsWith(".npy", StringComparison.OrdinalIgnoreCase))
            {
                throw new VoicePackInvalidException($"Voice \"{voiceId}\": a StyleTensor file must be a .npy (found \"{file}\").");
            }

            var phonemizerText = OptionalText(element, "phonemizer", 16);
            var phonemizer = phonemizerText is null
                ? Accents.Find(accent)!.DefaultPhonemizer
                : Phonemizers.Normalize(phonemizerText)
                    ?? throw new VoicePackInvalidException($"Voice \"{voiceId}\": unknown phonemizer \"{phonemizerText}\".");
            var engine = OptionalText(element, "engine", 32)?.ToLowerInvariant() ?? KokoroEngine;
            return new PackRealization(RealizationKind.StyleTensor, engine, file, phonemizer, null);
        }

        if (string.Equals(kindText, nameof(RealizationKind.ReferenceClip), StringComparison.OrdinalIgnoreCase))
        {
            if (!file.EndsWith(".wav", StringComparison.OrdinalIgnoreCase))
            {
                throw new VoicePackInvalidException($"Voice \"{voiceId}\": a ReferenceClip file must be a .wav (found \"{file}\").");
            }

            var engine = OptionalText(element, "engine", 32)?.ToLowerInvariant() ?? AnyCloningEngine;
            return new PackRealization(RealizationKind.ReferenceClip, engine, file, null, transcript);
        }

        throw new VoicePackInvalidException(
            $"Voice \"{voiceId}\": unsupported realization kind \"{kindText}\" (expected StyleTensor or ReferenceClip).");
    }

    private static string RequireId(JsonElement element, string property, string what)
    {
        var value = RequireText(element, property, MaxIdLength);
        return IsValidId(value)
            ? value
            : throw new VoicePackInvalidException($"Invalid {what} \"{value}\": use only a-z, 0-9, '.', '_' and '-'.");
    }

    private static string RequireText(JsonElement element, string property, int maxLength) =>
        OptionalText(element, property, maxLength)
        ?? throw new VoicePackInvalidException($"Missing required \"{property}\".");

    private static string? OptionalText(JsonElement element, string property, int maxLength)
    {
        if (!element.TryGetProperty(property, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.String)
        {
            throw new VoicePackInvalidException($"\"{property}\" must be a string.");
        }

        var raw = value.GetString();
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        return CleanText(raw, maxLength)
            ?? throw new VoicePackInvalidException($"\"{property}\" is too long or contains control characters.");
    }

    private static string? CleanText(string? raw, int maxLength)
    {
        var text = raw?.Trim();
        return string.IsNullOrEmpty(text) || text.Length > maxLength
            || text.Any(ch => char.IsControl(ch) && ch is not ('\n' or '\r' or '\t'))
            ? null
            : text;
    }
}
