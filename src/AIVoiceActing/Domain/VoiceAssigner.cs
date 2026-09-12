namespace AIVoiceActing.Domain;

using AIVoiceActing.Ports;
using Standart.Hash.xxHash;

/// <summary>
/// Deterministic, persistent voice assignment (TextToTalk parity):
/// <c>xxHash32(speakerKey) % candidateCount</c>. xxHash is used instead of GetHashCode
/// because the latter is randomized per process; xxHash32 over the UTF-8 key bytes is stable
/// across runs, so a speaker keeps the same voice after a game restart. A key already present
/// in <paramref name="existing"/> is returned unchanged — a speaker is never reassigned.
/// </summary>
public static class VoiceAssigner
{
    /// <summary>Stable index into a candidate list: <c>xxHash32(key) % candidateCount</c>.</summary>
    public static int AssignIndex(string speakerKey, int candidateCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(candidateCount);
        var hash = xxHash32.ComputeHash(speakerKey ?? string.Empty);
        return (int)(hash % (uint)candidateCount);
    }

    /// <summary>
    /// Returns the existing profile for the key, or assigns a new one over
    /// <paramref name="candidates"/> (voice ids). New profiles carry no bias — bias lives in
    /// slot-based assignment (<see cref="AssignSlot"/>) and manual overrides.
    /// </summary>
    public static VoiceProfile Assign(
        string speakerKey,
        string[] candidates,
        IReadOnlyDictionary<string, VoiceProfile> existing)
    {
        if (existing.TryGetValue(speakerKey, out var existingProfile))
        {
            return existingProfile;
        }

        if (candidates is not { Length: > 0 })
        {
            throw new ArgumentException("At least one candidate voice id is required.", nameof(candidates));
        }

        var voiceId = candidates[AssignIndex(speakerKey, candidates.Length)];
        return NewProfile(speakerKey, voiceId, exaggerationBias: 0f);
    }

    /// <summary>
    /// Slot-based assignment over a <see cref="RaceVoiceMap"/> slot set: the hash picks the
    /// slot, so the carried exaggeration bias is deterministic per speaker.
    /// </summary>
    public static VoiceProfile AssignSlot(
        string speakerKey,
        VoiceSlot[] slots,
        IReadOnlyDictionary<string, VoiceProfile> existing)
    {
        if (existing.TryGetValue(speakerKey, out var existingProfile))
        {
            return existingProfile;
        }

        if (slots is not { Length: > 0 })
        {
            throw new ArgumentException("At least one candidate voice slot is required.", nameof(slots));
        }

        var slot = slots[AssignIndex(speakerKey, slots.Length)];
        return NewProfile(speakerKey, slot.Id, slot.ExaggerationBias);
    }

    private static VoiceProfile NewProfile(string speakerKey, string voiceId, float exaggerationBias) =>
        new(speakerKey, voiceId, Math.Clamp(exaggerationBias, 0f, 1f), DateTimeOffset.UtcNow, Custom: false);
}
