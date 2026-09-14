namespace AIVoiceActing.Domain;

using AIVoiceActing.Ports;

/// <summary>
/// First-sight voice assignment: a speaker's first resolution picks RANDOMLY from the
/// matching row's voice slots; the profile store persists the pick, so the speaker keeps
/// that voice forever (only <c>SetOverride</c> or an explicit remove changes it). Rows
/// with several voices spread their cast across the crowd instead of pinning one voice
/// per bucket.
/// </summary>
public static class VoiceAssigner
{
    /// <summary>
    /// Picks one slot from <paramref name="candidates"/> at random and builds the profile
    /// for it, carrying the slot's performance knobs (bias/pitch/speed/volume;
    /// Custom = false). Uses <paramref name="rng"/> when given (tests seed one for
    /// determinism), <see cref="Random.Shared"/> otherwise. The returned profile has an
    /// empty <see cref="VoiceProfile.SpeakerKey"/> — the caller stamps the key on
    /// assignment.
    /// </summary>
    public static VoiceProfile AssignSlot(IReadOnlyList<VoiceSlot> candidates, Random? rng = null)
    {
        if (candidates is not { Count: > 0 })
        {
            throw new ArgumentException("At least one candidate voice slot is required.", nameof(candidates));
        }

        var slot = candidates[(rng ?? Random.Shared).Next(candidates.Count)];
        return NewProfile(slot.Id, slot.ExaggerationBias, slot.Pitch, slot.Speed, slot.Volume);
    }

    private static VoiceProfile NewProfile(
        string voiceId,
        float exaggerationBias,
        float pitch = 1f,
        float speed = 1f,
        float volume = 1f) =>
        new(
            string.Empty,
            voiceId,
            Math.Clamp(exaggerationBias, 0f, 1f),
            DateTimeOffset.UtcNow,
            Custom: false,
            pitch,
            speed,
            Math.Clamp(volume, 0f, 2f));
}
