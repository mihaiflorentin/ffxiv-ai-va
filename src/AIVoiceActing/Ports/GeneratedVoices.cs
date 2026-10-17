namespace AIVoiceActing.Ports;

using AIVoiceActing.Domain;

/// <summary>Thrown by <see cref="IGeneratedVoiceStore"/> when a generated voice cannot be written.</summary>
public sealed class GeneratedVoiceStoreException(string message, Exception? inner = null)
    : Exception(message, inner);

/// <summary>
/// What is remembered about one generated voice: whose it is, the voices it was blended from
/// (<paramref name="Sources"/> with their weights), the casting pool it was drawn from
/// (<paramref name="Pool"/>, so a regenerate needs no speaker facts) and the salt that
/// produced it (0 first, one more per regenerate).
/// </summary>
public sealed record GeneratedVoiceRecord(
    string Id,
    string SpeakerKey,
    string DisplayName,
    string Phonemizer,
    string Sex,
    int Salt,
    IReadOnlyList<BlendPart> Sources,
    IReadOnlyList<string> Pool,
    DateTimeOffset CreatedUtc);

/// <summary>Driven port over the on-disk generated voices (style tensor plus a small JSON each).</summary>
public interface IGeneratedVoiceStore
{
    /// <summary>Every readable record, stable order.</summary>
    IReadOnlyList<GeneratedVoiceRecord> Records { get; }

    /// <summary>The record for a generated voice id, or null.</summary>
    GeneratedVoiceRecord? Find(string? id);

    /// <summary>The newest record (highest salt) belonging to a speaker, or null.</summary>
    GeneratedVoiceRecord? ForSpeaker(string speakerKey);

    /// <summary>The voice as the library shows it (a style tensor realization), or null when it or its tensor is missing.</summary>
    VoiceInfo? FindVoice(string? id);

    /// <summary>Writes the tensor then the record, each atomically (temp file then move).</summary>
    void Save(GeneratedVoiceRecord record, float[] tensor);

    /// <summary>Deletes a generated voice's files. True when it existed.</summary>
    bool Remove(string id);

    /// <summary>Deletes every generated voice whose id is not in <paramref name="keep"/>; returns how many went.</summary>
    int Prune(IReadOnlySet<string> keep);
}

/// <summary>
/// Makes and re-makes the per-character blended voices. Driven by the profile resolution (first
/// sight) and by the Voices tab (Regenerate).
/// </summary>
public interface IVoiceGenerator
{
    /// <summary>
    /// The generated voice for a speaker: the one already stored, else a new blend drawn from
    /// <paramref name="castVoiceIds"/> (widened with the stock English voices when the cast is too
    /// small). Null when no blend is possible; the caller keeps the ordinary pick.
    /// </summary>
    GeneratedVoiceRecord? Ensure(string speakerKey, string displayName, IReadOnlyList<string> castVoiceIds, string? preferredSex);

    /// <summary>A new blend (next salt) for the speaker who owns <paramref name="voiceId"/>; the old one is removed. Null when it cannot be done.</summary>
    GeneratedVoiceRecord? Regenerate(string voiceId);
}
