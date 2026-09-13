namespace AIVoiceActing.Ports;

using AIVoiceActing.Domain;

/// <summary>Thrown by <see cref="IProfileStore"/> when persistent voice profiles cannot be loaded or saved.</summary>
public sealed class ProfileStoreException(string message, Exception? inner = null)
    : Exception(message, inner);

/// <summary>Persisted per-speaker voice assignment.</summary>
/// <param name="SpeakerKey">Stable <c>AIVoiceActing.Domain.SpeakerIdentity.Key</c>.</param>
/// <param name="ReferenceVoiceId">Bundled reference clip chosen for this speaker.</param>
/// <param name="ExaggerationBias">Per-speaker additive bias on the planned exaggeration, clamped to 0..1.</param>
/// <param name="CreatedUtc">When the assignment was first created.</param>
/// <param name="Custom">True when the entry is a manual user override.</param>
public sealed record VoiceProfile(
    string SpeakerKey,
    string ReferenceVoiceId,
    float ExaggerationBias,
    DateTimeOffset CreatedUtc,
    bool Custom,
    float Pitch = 1f,
    float Speed = 1f);

/// <summary>Driven port over the persistent voice-assignment store.</summary>
public interface IProfileStore
{
    /// <summary>Path of the backing store file (JSON).</summary>
    string FilePath { get; }

    /// <summary>All persisted profiles.</summary>
    IReadOnlyCollection<VoiceProfile> Entries { get; }

    /// <summary>
    /// Returns the stored profile for <paramref name="speakerKey"/>, creating and persisting a
    /// deterministic slot assignment (over <paramref name="candidateVoiceSlots"/>) on first
    /// sight. The chosen slot's (voice id, exaggeration bias) pair persists as one unit, so
    /// candidates must carry their bias (see <see cref="Domain.RaceVoiceMap"/>).
    /// </summary>
    VoiceProfile GetOrCreate(
        string speakerKey,
        Func<VoiceSlot[]> candidateVoiceSlots,
        byte? race,
        byte? tribe,
        byte? sex);

    /// <summary>Records a manual override; overrides win over deterministic assignment and persist.</summary>
    void SetOverride(string speakerKey, string referenceVoiceId, float exaggerationBias);

    /// <summary>
    /// Deletes any persisted entry (override or deterministic slot) for
    /// <paramref name="speakerKey"/>, persisting immediately. True when an entry existed.
    /// The speaker is simply forgotten: the next lookup re-assigns deterministically.
    /// </summary>
    bool Remove(string speakerKey);
}
