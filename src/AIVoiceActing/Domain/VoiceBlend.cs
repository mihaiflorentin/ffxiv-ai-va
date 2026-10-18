namespace AIVoiceActing.Domain;

using System.Security.Cryptography;
using System.Text;
using AIVoiceActing.Ports;

/// <summary>One source voice of a blend and its share (shares of a plan sum to 1).</summary>
public sealed record BlendPart(string VoiceId, float Weight);

/// <summary>A voice that can take part in a blend: an English Kokoro style tensor of a known sex.</summary>
public sealed record BlendCandidate(string Id, string Phonemizer, string Sex);

/// <summary>The recipe for one generated voice: the new id, its phonemizer and sex, and the weighted sources.</summary>
public sealed record BlendPlan(string VoiceId, string Phonemizer, string Sex, int Salt, IReadOnlyList<BlendPart> Parts);

/// <summary>
/// Pure maths of generated voices. A Kokoro style tensor is a 510x1x256 float table; a weighted
/// average of two or three tensors that share a phonemizer and a sex is a new, natural-sounding
/// voice. Everything here is deterministic: the same speaker key, salt and candidate set always
/// yield the same plan and the same tensor bytes (the hash is SHA-256, never string.GetHashCode).
/// </summary>
public static class VoiceBlend
{
    public const string GeneratedPackId = "generated";

    private const string IdMarker = "_gen_";
    private const int HashChars = 12;
    private const double MinRawWeight = 1.0;
    private const double RawWeightSpread = 1.5;

    /// <summary>True for the English phonemizers (<c>en-us</c>, <c>en-gb</c>) a blend may use.</summary>
    public static bool IsEnglishPhonemizer(string? phonemizer) =>
        Phonemizers.Normalize(phonemizer) is Phonemizers.EnglishUs or Phonemizers.EnglishGb;

    /// <summary>
    /// True for an American-accent voice: filed under <c>en-US</c> (the stock <c>af_*</c>/<c>am_*</c>
    /// bank) or carrying a stock American id. Decided by accent and id, never by the phonemizer: the
    /// Isles pack pronounces through the American phonemizer but speaks Irish, Welsh and so on.
    /// </summary>
    public static bool IsAmerican(VoiceInfo voice) =>
        string.Equals(voice.Accent, "en-US", StringComparison.OrdinalIgnoreCase)
        || (voice.Id is { Length: > 3 } id && id[0] == 'a' && id[1] is 'f' or 'm' && id[2] == '_');

    /// <summary>True for a pack voice with a foreign accent in English (<c>en-x-*</c>, such as Japanese-accented).</summary>
    public static bool IsForeignAccented(VoiceInfo voice) =>
        voice.Accent?.StartsWith("en-x-", StringComparison.OrdinalIgnoreCase) == true;

    /// <summary>
    /// A voice as a blend candidate, or null when it cannot be blended: unavailable, American,
    /// itself a generated voice (no blends of blends, which would also hide an American ancestor),
    /// no Kokoro style tensor, or a non-English phonemizer. English is decided by the phonemizer
    /// alone, so a pack voice with a foreign accent but an English phonemizer still counts.
    /// </summary>
    public static BlendCandidate? CandidateFor(VoiceInfo voice)
    {
        if (!voice.Available || IsAmerican(voice) || voice.PackId == GeneratedPackId)
        {
            return null;
        }

        var tensor = voice.Realizations.FirstOrDefault(r =>
            r.Kind == RealizationKind.StyleTensor
            && r.Available
            && string.Equals(r.Engine, "kokoro", StringComparison.OrdinalIgnoreCase));
        var phonemizer = Phonemizers.Normalize(tensor?.Phonemizer);
        if (phonemizer is not (Phonemizers.EnglishUs or Phonemizers.EnglishGb))
        {
            return null;
        }

        var sex = voice.Sex is "male" or "female" ? voice.Sex : "neutral";
        return new BlendCandidate(voice.Id, phonemizer, sex);
    }

    /// <summary>
    /// A candidate for the wider pool a blend falls back on when the casting set is too small:
    /// <see cref="CandidateFor"/> minus foreign-accented voices, so a Midlands speaker is never
    /// blended with a Japanese-accented one just because the set had a single voice. What remains
    /// is British stock plus the regional pack voices that have Kokoro tensors. When even that is
    /// too small the plan is null and the speaker keeps the plain pick from the casting.
    /// </summary>
    public static BlendCandidate? WidenCandidateFor(VoiceInfo voice) =>
        IsForeignAccented(voice) ? null : CandidateFor(voice);

    /// <summary>Public id of a generated voice: <c>{a|b}{f|m}_gen_{hash}</c> (the shape KokoroSharp needs to pick the G2P).</summary>
    public static string IdFor(string phonemizer, string sex, string hash) =>
        $"{Phonemizers.PrefixFor(phonemizer)}{(sex == "male" ? 'm' : 'f')}{IdMarker}{hash}";

    /// <summary>True when the id has the generated-voice shape.</summary>
    public static bool IsGeneratedId(string? id) =>
        id is { Length: > 7 }
        && (id[0] is 'a' or 'b')
        && (id[1] is 'f' or 'm')
        && id.AsSpan(2).StartsWith(IdMarker, StringComparison.Ordinal);

    /// <summary>The stable hash of a speaker and a salt: 12 lowercase hex characters.</summary>
    public static string Hash(string speakerKey, int salt) =>
        Convert.ToHexString(Digest(speakerKey, salt).AsSpan(0, HashChars / 2)).ToLowerInvariant();

    /// <summary>
    /// Plans a blend for a speaker. <paramref name="cast"/> is the casting set (English
    /// candidates only), <paramref name="widen"/> the wider English pool used when the cast has
    /// fewer than two voices of the chosen phonemizer and sex. <paramref name="preferredSex"/>
    /// (<c>male</c>/<c>female</c>) steers the choice of group. Null when no two voices of one
    /// phonemizer and sex exist.
    /// </summary>
    public static BlendPlan? Plan(
        string speakerKey,
        int salt,
        IReadOnlyList<BlendCandidate> cast,
        IReadOnlyList<BlendCandidate> widen,
        string? preferredSex)
    {
        var digest = Digest(speakerKey, salt);
        var pool = ChoosePool(cast, widen, preferredSex);
        if (pool is not { Count: >= 2 })
        {
            return null;
        }

        var rng = new SplitMix64(BitConverter.ToUInt64(digest, 16));
        var count = pool.Count >= 3 && (rng.Next() & 1) == 0 ? 3 : 2;
        var order = pool.OrderBy(c => c.Id, StringComparer.Ordinal).ToList();
        var picked = new List<BlendCandidate>(count);
        for (var i = 0; i < count; i++)
        {
            var at = (int)(rng.Next() % (ulong)order.Count);
            picked.Add(order[at]);
            order.RemoveAt(at);
        }

        var raw = picked.Select(_ => MinRawWeight + (rng.NextDouble() * RawWeightSpread)).ToArray();
        var total = raw.Sum();
        var parts = picked
            .Select((c, i) => new BlendPart(c.Id, (float)(raw[i] / total)))
            .ToList();
        var first = picked[0];
        return new BlendPlan(
            IdFor(first.Phonemizer, first.Sex, Hash(speakerKey, salt)), first.Phonemizer, first.Sex, salt, parts);
    }

    /// <summary>
    /// Weighted average of equally sized tensors. Weights are re-normalised to sum to 1 here, so
    /// stored (rounded) weights still produce a tensor on the same scale as its sources.
    /// </summary>
    public static float[] Mix(IReadOnlyList<float[]> tensors, IReadOnlyList<float> weights)
    {
        if (tensors.Count == 0 || tensors.Count != weights.Count)
        {
            throw new ArgumentException("One weight per tensor is required.", nameof(weights));
        }

        var length = tensors[0].Length;
        if (tensors.Any(t => t.Length != length))
        {
            throw new ArgumentException("Tensors must have equal length.", nameof(tensors));
        }

        var sum = weights.Sum(w => (double)w);
        if (sum <= 0)
        {
            throw new ArgumentException("Weights must sum to a positive number.", nameof(weights));
        }

        var result = new float[length];
        for (var i = 0; i < length; i++)
        {
            double acc = 0;
            for (var t = 0; t < tensors.Count; t++)
            {
                acc += tensors[t][i] * (weights[t] / sum);
            }

            result[i] = (float)acc;
        }

        return result;
    }

    private static List<BlendCandidate>? ChoosePool(
        IReadOnlyList<BlendCandidate> cast, IReadOnlyList<BlendCandidate> widen, string? preferredSex)
    {
        // The wanted sex always wins (a male character never gets a female blend); within it,
        // a group of two or more beats a lone voice, then the larger group, then name order.
        var groups = cast
            .GroupBy(c => (c.Phonemizer, c.Sex))
            .OrderByDescending(g => g.Key.Sex == preferredSex)
            .ThenByDescending(g => g.Count() >= 2)
            .ThenByDescending(g => g.Count())
            .ThenBy(g => g.Key.Phonemizer, StringComparer.Ordinal)
            .ThenBy(g => g.Key.Sex, StringComparer.Ordinal)
            .ToList();
        if (groups.Count == 0)
        {
            return null;
        }

        var best = groups[0];
        var (phonemizer, sex) = best.Key;
        IEnumerable<BlendCandidate> members = best;
        if (best.Count() < 2)
        {
            members = members.Concat(widen.Where(c => c.Phonemizer == phonemizer && c.Sex == sex));
        }

        return [.. members.DistinctBy(c => c.Id)];
    }

    private static byte[] Digest(string speakerKey, int salt) =>
        SHA256.HashData(Encoding.UTF8.GetBytes($"{speakerKey}|{salt}"));

    /// <summary>SplitMix64: tiny, portable and stable across runtimes (unlike <see cref="Random"/> seeds).</summary>
    private sealed class SplitMix64(ulong seed)
    {
        private ulong state = seed;

        public ulong Next()
        {
            var z = this.state += 0x9E3779B97F4A7C15UL;
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
            return z ^ (z >> 31);
        }

        public double NextDouble() => (this.Next() >> 11) * (1.0 / (1UL << 53));
    }
}
