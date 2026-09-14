namespace AIVoiceActing.Infrastructure.Storage;

using System.Text.Json;
using System.Text.Json.Serialization;
using AIVoiceActing.Domain;
using AIVoiceActing.Ports;

/// <summary>
/// JSON-backed <see cref="IProfileStore"/> over <c>ConfigDirectory/voice-assignments.json</c>
/// (TextToTalk parity): one map keyed by speaker key holding both manual overrides and
/// deterministic assignments. Loaded lazily on first access; a corrupt file is backed up to
/// ".bak" and the store starts fresh (logged). Writes are atomic (temp file + move).
/// Single-process plugin: a private lock guards all state. Invariant: an existing entry is
/// NEVER reassigned — only <see cref="SetOverride"/> (Custom = true) replaces one. Hashed
/// assignments go through <see cref="VoiceAssigner.AssignSlot"/>, so the chosen slot's
/// (voice id, exaggeration bias) persists as one unit.
/// </summary>
public sealed class JsonProfileStore : IProfileStore
{
    private sealed record EntryDto(
        [property: JsonPropertyName("referenceVoiceId")] string ReferenceVoiceId,
        [property: JsonPropertyName("exaggerationBias")] float ExaggerationBias,
        [property: JsonPropertyName("createdUtc")] DateTimeOffset CreatedUtc,
        [property: JsonPropertyName("custom")] bool Custom,
        [property: JsonPropertyName("pitch")] float Pitch = 1f,
        [property: JsonPropertyName("speed")] float Speed = 1f,
        [property: JsonPropertyName("volume")] float Volume = 1f);

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly object gate = new();
    private readonly ILogSink log;
    private readonly Dictionary<string, VoiceProfile> entries;
    private bool loaded;

    public JsonProfileStore(string filePath, ILogSink log)
    {
        FilePath = filePath;
        this.log = log;
        this.entries = [];
    }

    public string FilePath { get; }

    public IReadOnlyCollection<VoiceProfile> Entries
    {
        get
        {
            lock (this.gate)
            {
                this.EnsureLoadedUnlocked();
                return [.. this.entries.Values];
            }
        }
    }
    public VoiceProfile GetOrCreate(
        string speakerKey,
        Func<VoiceSlot[]> candidateVoiceSlots,
        byte? race,
        byte? tribe,
        byte? sex)
    {
        lock (this.gate)
        {
            this.EnsureLoadedUnlocked();
            if (this.entries.TryGetValue(speakerKey, out var existing))
            {
                return existing; // never reassigned; stale ids are repaired by the container
            }

            var slots = candidateVoiceSlots();
            if (slots is not { Length: > 0 })
            {
                throw new ProfileStoreException($"No candidate voices provided for speaker \"{speakerKey}\".");
            }

            var profile = VoiceAssigner.AssignSlot(speakerKey, slots, this.entries);
            this.entries[speakerKey] = profile;
            this.SaveUnlocked();
            return profile;
        }
    }

    public void SetOverride(
        string speakerKey,
        string referenceVoiceId,
        float exaggerationBias,
        float volume = 1f,
        float? pitch = null,
        float? speed = null)
    {
        lock (this.gate)
        {
            this.EnsureLoadedUnlocked();
            // Null pitch/speed keep the previous entry's values (manual overrides on
            // child-pitch races stay child-pitched); supplied values replace them.
            var previous = this.entries.TryGetValue(speakerKey, out var existing) ? existing : null;
            var effectivePitch = pitch ?? previous?.Pitch ?? 1f;
            var effectiveSpeed = speed ?? previous?.Speed ?? 1f;
            this.entries[speakerKey] = new VoiceProfile(
                speakerKey,
                referenceVoiceId,
                Math.Clamp(exaggerationBias, 0f, 1f),
                DateTimeOffset.UtcNow,
                Custom: true,
                effectivePitch,
                effectiveSpeed,
                Math.Clamp(volume, 0f, 2f));
            this.SaveUnlocked();
        }
    }

    public void Clear()
    {
        lock (this.gate)
        {
            this.EnsureLoadedUnlocked();
            this.entries.Clear();
            this.SaveUnlocked();
        }
    }


    public bool Remove(string speakerKey)
    {
        lock (this.gate)
        {
            this.EnsureLoadedUnlocked();
            if (!this.entries.Remove(speakerKey))
            {
                return false;
            }

            this.SaveUnlocked();
            return true;
        }
    }

    private void EnsureLoadedUnlocked()
    {
        if (this.loaded)
        {
            return;
        }

        this.loaded = true;
        if (!File.Exists(FilePath))
        {
            return;
        }

        try
        {
            var raw = JsonSerializer.Deserialize<Dictionary<string, EntryDto>>(
                File.ReadAllText(FilePath), JsonOptions)
                ?? throw new JsonException("Store file deserialized to null.");
            foreach (var (key, dto) in raw)
            {
                this.entries[key] = new VoiceProfile(
                    key,
                    dto.ReferenceVoiceId ?? throw new JsonException($"Entry \"{key}\" has no voice id."),
                    dto.ExaggerationBias,
                    dto.CreatedUtc,
                    dto.Custom,
                    dto.Pitch,
                    dto.Speed,
                    dto.Volume);
            }
        }
        catch (Exception e) when (e is JsonException or FormatException or InvalidOperationException)
        {
            var backup = FilePath + ".bak";
            try
            {
                File.Copy(FilePath, backup, overwrite: true);
            }
            catch (Exception copyEx) when (copyEx is IOException or UnauthorizedAccessException)
            {
                this.log.Error($"Failed to back up corrupt profile store \"{backup}\".", copyEx);
            }

            this.entries.Clear();
            this.log.Error($"Corrupt voice-assignment store \"{FilePath}\"; backed up and starting fresh.", e);
        }
    }

    private void SaveUnlocked()
    {
        try
        {
            var directory = Path.GetDirectoryName(FilePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var temp = FilePath + ".tmp";
            File.WriteAllText(
                temp,
                JsonSerializer.Serialize(
                    this.entries.ToDictionary(
                        kv => kv.Key,
                        kv => new EntryDto(
                            kv.Value.ReferenceVoiceId,
                            kv.Value.ExaggerationBias,
                            kv.Value.CreatedUtc,
                            kv.Value.Custom,
                            kv.Value.Pitch,
                            kv.Value.Speed,
                            kv.Value.Volume)),
                    JsonOptions));
            File.Move(temp, FilePath, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new ProfileStoreException($"Failed to save voice assignments to \"{FilePath}\".", e);
        }
    }
}
