namespace AIVoiceActing.Tests.Mock;

using AIVoiceActing.Domain;
using AIVoiceActing.Ports;

/// <summary>In-memory <see cref="IProfileStore"/> that records every write, for the character table tests.</summary>
public sealed class FakeProfileStore : IProfileStore
{
    private readonly Dictionary<string, VoiceProfile> entries = new(StringComparer.Ordinal);

    public string FilePath => "memory";

    public IReadOnlyCollection<VoiceProfile> Entries => [.. this.entries.Values];

    /// <summary>Every <see cref="SetOverride"/> call, in order.</summary>
    public List<VoiceProfile> Writes { get; } = [];

    public int RemoveCalls { get; private set; }

    public VoiceProfile Add(string key, string voice, float bias = 0f, float pitch = 1f, float speed = 1f, float volume = 1f)
    {
        var profile = new VoiceProfile(key, voice, bias, DateTimeOffset.UnixEpoch, Custom: true, pitch, speed, volume);
        this.entries[key] = profile;
        return profile;
    }

    public VoiceProfile? Get(string key) => this.entries.GetValueOrDefault(key);

    public VoiceProfile GetOrCreate(string speakerKey, Func<VoiceSlot[]> candidateVoiceSlots, byte? race, byte? tribe, byte? sex) =>
        throw new NotSupportedException();

    public void SetOverride(string speakerKey, string referenceVoiceId, float exaggerationBias, float volume = 1f, float? pitch = null, float? speed = null)
    {
        var previous = this.entries.GetValueOrDefault(speakerKey);
        var profile = new VoiceProfile(
            speakerKey,
            referenceVoiceId,
            exaggerationBias,
            DateTimeOffset.UnixEpoch,
            Custom: true,
            pitch ?? previous?.Pitch ?? 1f,
            speed ?? previous?.Speed ?? 1f,
            volume);
        this.entries[speakerKey] = profile;
        this.Writes.Add(profile);
    }

    public void Clear() => this.entries.Clear();

    public bool Remove(string speakerKey)
    {
        this.RemoveCalls++;
        return this.entries.Remove(speakerKey);
    }
}
