namespace AIVoiceActing.Infrastructure.Storage;

using System.Text.Json;
using System.Text.Json.Serialization;
using AIVoiceActing.Domain;
using AIVoiceActing.Ports;

/// <summary>
/// JSON-backed <see cref="ICastingPresetStore"/> over <c>casting-presets.json</c>:
/// <c>{"activePreset":"Default","presets":{...}}</c>. The built-in Default preset is
/// never stored — <see cref="GetDefault"/> derives it from the injected base voice-map
/// factory on every call (active sets merged with parked sets). Loaded lazily on first
/// access; a corrupt file is backed up to ".bak" and the store starts fresh (logged,
/// mirroring <see cref="JsonProfileStore"/>). Writes are atomic (temp file + move); a
/// private lock guards all state.
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
        var sets = new Dictionary<string, VoiceSlotDto[]>(StringComparer.Ordinal);
        foreach (var (key, slots) in map.Sets)
        {
            sets[key] = slots.Select(slot => slot.ToDto()).ToArray();
        }

        // Parked sets are part of the casting library: forks and model-id overrides may
        // point at them even though the UI picker never lists them as active.
        if (map.Disabled is { } parked)
        {
            foreach (var (key, slots) in parked.Sets)
            {
                sets[key] = slots.Select(slot => slot.ToDto()).ToArray();
            }
        }

        var variants = new Dictionary<string, string>(map.RaceVariants, StringComparer.Ordinal)
        {
            [CastingPreset.UngenderedVariantKey] = CastingPreset.UngenderedVariantKey,
        };

        return new CastingPreset(
            CastingPreset.DefaultPresetName, sets, variants, new Dictionary<string, ModelOverrideDto>());
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
                this.presets[name] = preset with { Name = name };
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
