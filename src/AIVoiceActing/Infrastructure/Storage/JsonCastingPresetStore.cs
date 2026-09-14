namespace AIVoiceActing.Infrastructure.Storage;

using System.Text.Json;
using System.Text.Json.Serialization;
using AIVoiceActing.Domain;
using AIVoiceActing.Ports;

/// <summary>
/// JSON-backed <see cref="ICastingPresetStore"/> over <c>casting-presets.json</c>:
/// <c>{"activePreset":"Default","presets":{name:{"buckets":{...},"beastTribes":[...]}}}</c>.
/// The built-in Default preset is never stored — <see cref="GetDefault"/> derives it from
/// the injected base voice-map factory on every call (the 16 race/gender variant rows,
/// the Unknown row, and one beast-tribe row per known tribe). Loaded lazily on first
/// access; a corrupt file is backed up to ".bak" and the store starts fresh (logged,
/// mirroring <see cref="JsonProfileStore"/>). Pre-0.0.26 preset entries (set/variant
/// shape) are discarded on load rather than migrated. Writes are atomic (temp file +
/// move); a private lock guards all state.
/// </summary>
public sealed class JsonCastingPresetStore : ICastingPresetStore
{
    private sealed record StoreFile(
        [property: JsonPropertyName("activePreset")] string ActivePreset = CastingPreset.DefaultPresetName,
        [property: JsonPropertyName("presets")] Dictionary<string, CastingPreset>? Presets = null);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly object gate = new();
    private readonly Func<RaceVoiceMap> baseMapFactory;
    private readonly ILogSink log;
    private readonly Dictionary<string, CastingPreset> presets = new(StringComparer.Ordinal);
    private string activePresetName = CastingPreset.DefaultPresetName;
    private bool loaded;

    public JsonCastingPresetStore(string filePath, Func<RaceVoiceMap> baseMapFactory, ILogSink log)
    {
        FilePath = filePath;
        this.baseMapFactory = baseMapFactory;
        this.log = log;
    }

    public string FilePath { get; }

    public string ActivePresetName
    {
        get
        {
            lock (this.gate)
            {
                this.EnsureLoadedUnlocked();
                return this.activePresetName;
            }
        }
    }

    public IReadOnlyList<string> PresetNames
    {
        get
        {
            lock (this.gate)
            {
                this.EnsureLoadedUnlocked();
                return
                [
                    CastingPreset.DefaultPresetName,
                    .. this.presets.Keys.Order(StringComparer.Ordinal),
                ];
            }
        }
    }

    public CastingPreset GetDefault()
    {
        var map = this.baseMapFactory();
        var buckets = new Dictionary<string, VoiceSlotDto[]>(StringComparer.Ordinal);
        foreach (var (variantKey, setKey) in map.RaceVariants)
        {
            buckets[variantKey] = map.Sets.TryGetValue(setKey, out var slots)
                ? slots.ToDtoArray()
                : [];
        }

        buckets[CastingDefaults.UnknownBucketKey] = map.Sets.TryGetValue(CastingDefaults.UnknownBucketKey, out var unknown)
            ? unknown.ToDtoArray()
            : [];

        var tribes = CastingDefaults.Tribes
            .Select(tribe => new BeastTribeCast(
                Key: tribe.Key,
                Name: tribe.Name,
                ModelIds: map.BeastTribeBindings.TryGetValue(tribe.Key, out var boundIds)
                    ? boundIds
                    : [],
                Voices: map.Disabled?.Sets.TryGetValue(tribe.ManifestSetKey, out var parked) == true
                    ? parked.ToDtoArray()
                    : [],
                MaleVoices: [],
                FemaleVoices: []))
            .ToArray();

        return new CastingPreset(CastingPreset.DefaultPresetName, buckets, tribes);
    }

    public CastingPreset Get(string name)
    {
        lock (this.gate)
        {
            this.EnsureLoadedUnlocked();
            if (name == CastingPreset.DefaultPresetName)
            {
                throw new CastingPresetException(
                    "The Default preset is built in; read it via GetDefault().");
            }

            return this.presets.TryGetValue(name, out var preset)
                ? preset
                : throw new CastingPresetException($"No casting preset named \"{name}\".");
        }
    }

    public void Save(CastingPreset preset)
    {
        var name = preset.Name.Trim();
        if (name.Length == 0)
        {
            throw new CastingPresetException("A casting preset needs a name.");
        }

        if (name == CastingPreset.DefaultPresetName)
        {
            throw new CastingPresetException("The built-in Default preset is immutable.");
        }

        lock (this.gate)
        {
            this.EnsureLoadedUnlocked();
            this.presets[name] = preset with { Name = name };
            this.SaveUnlocked();
        }
    }

    public void Delete(string name)
    {
        lock (this.gate)
        {
            this.EnsureLoadedUnlocked();
            if (name == CastingPreset.DefaultPresetName)
            {
                throw new CastingPresetException("The built-in Default preset cannot be deleted.");
            }

            if (!this.presets.Remove(name))
            {
                throw new CastingPresetException($"No casting preset named \"{name}\".");
            }

            if (this.activePresetName == name)
            {
                this.activePresetName = CastingPreset.DefaultPresetName;
            }

            this.SaveUnlocked();
        }
    }

    public void Activate(string name)
    {
        lock (this.gate)
        {
            this.EnsureLoadedUnlocked();
            if (name != CastingPreset.DefaultPresetName && !this.presets.ContainsKey(name))
            {
                throw new CastingPresetException($"No casting preset named \"{name}\".");
            }

            this.activePresetName = name;
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
            var file = JsonSerializer.Deserialize<StoreFile>(File.ReadAllText(FilePath), JsonOptions)
                ?? throw new JsonException("Preset file deserialized to null.");
            this.activePresetName = file.ActivePreset is { Length: > 0 } active
                ? active
                : CastingPreset.DefaultPresetName;
            foreach (var (name, preset) in file.Presets ?? [])
            {
                // Pre-0.0.26 entries carried set/variant keys that no longer exist; an
                // empty bucket map AND empty tribe list means the shape never converted.
                if ((preset.Buckets?.Count ?? 0) == 0 && (preset.BeastTribes?.Count ?? 0) == 0)
                {
                    this.log.Info($"discarded pre-0.0.26 preset '{name}'");
                    continue;
                }

                this.presets[name] = preset with { Name = name };
            }

            if (this.presets.Count == 0)
            {
                this.activePresetName = CastingPreset.DefaultPresetName;
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
                this.log.Error($"Failed to back up corrupt casting preset file \"{backup}\".", copyEx);
            }

            this.presets.Clear();
            this.activePresetName = CastingPreset.DefaultPresetName;
            this.log.Error($"Corrupt casting preset file \"{FilePath}\"; backed up and starting fresh.", e);
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
                JsonSerializer.Serialize(new StoreFile(this.activePresetName, this.presets), JsonOptions));
            File.Move(temp, FilePath, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new CastingPresetException($"Failed to save casting presets to \"{FilePath}\".", e);
        }
    }
}
