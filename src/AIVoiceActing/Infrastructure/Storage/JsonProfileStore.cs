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
/// NEVER reassigned — only <see cref="SetOverride"/> (Custom = true) replaces one.
/// </summary>
public sealed class JsonProfileStore : IProfileStore
{
    private sealed record EntryDto(
        [property: JsonPropertyName("referenceVoiceId")] string ReferenceVoiceId,
        [property: JsonPropertyName("exaggerationBias")] float ExaggerationBias,
        [property: JsonPropertyName("createdUtc")] DateTimeOffset CreatedUtc,
        [property: JsonPropertyName("custom")] bool Custom);

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly object gate = new();
    private readonly ILogSink log;
    private readonly Func<byte?, byte?, byte?, float>? exaggerationBiasFor;
    private readonly Dictionary<string, VoiceProfile> entries;
    private bool loaded;

    /// <param name="exaggerationBiasFor">
    /// Optional race/tribe/sex → bias delegate applied to newly created (non-override)
    /// entries; typically backed by <see cref="RaceVoiceMap"/> slots.
    /// </param>
    public JsonProfileStore(
        string filePath,
        ILogSink log,
        Func<byte?, byte?, byte?, float>? exaggerationBiasFor = null)
    {
        FilePath = filePath;
        this.log = log;
        this.exaggerationBiasFor = exaggerationBiasFor;
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
        Func<string[]> candidateVoiceIds,
        byte? race,
        byte? tribe,
        byte? sex)
    {
        lock (this.gate)
        {
            this.EnsureLoadedUnlocked();
            if (this.entries.TryGetValue(speakerKey, out var existing))
            {
                return existing; // never reassigned
            }

            var candidates = candidateVoiceIds();
            if (candidates is not { Length: > 0 })
            {
                throw new ProfileStoreException($"No candidate voices provided for speaker \"{speakerKey}\".");
            }

            var voiceId = candidates[VoiceAssigner.AssignIndex(speakerKey, candidates.Length)];
            var rawBias = this.exaggerationBiasFor?.Invoke(race, tribe, sex) ?? 0f;
            var profile = new VoiceProfile(
                speakerKey, voiceId, Math.Clamp(rawBias, 0f, 1f), DateTimeOffset.UtcNow, Custom: false);
            this.entries[speakerKey] = profile;
            this.SaveUnlocked();
            return profile;
        }
    }

    public void SetOverride(string speakerKey, string referenceVoiceId, float exaggerationBias)
    {
        lock (this.gate)
        {
            this.EnsureLoadedUnlocked();
            this.entries[speakerKey] = new VoiceProfile(
                speakerKey,
                referenceVoiceId,
                Math.Clamp(exaggerationBias, 0f, 1f),
                DateTimeOffset.UtcNow,
                Custom: true);
            this.SaveUnlocked();
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
                    dto.Custom);
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
                            kv.Value.Custom)),
                    JsonOptions));
            File.Move(temp, FilePath, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new ProfileStoreException($"Failed to save voice assignments to \"{FilePath}\".", e);
        }
    }
}
