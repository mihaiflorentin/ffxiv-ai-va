namespace AIVoiceActing.Infrastructure.Storage;

using System.Text.Json;
using System.Text.Json.Serialization;
using AIVoiceActing.Domain;
using AIVoiceActing.Ports;

/// <summary>
/// Generated voices on disk: <c>&lt;dir&gt;/&lt;hash&gt;.npy</c> (the blended style tensor) next to
/// <c>&lt;dir&gt;/&lt;hash&gt;.json</c> (speaker, source voices, weights, salt). Both files are written
/// atomically (temp file then move), tensor first, so a record never points at a missing or
/// half-written tensor. Records are read once on first use and kept in memory; a record whose
/// tensor is missing or the wrong size is ignored (and logged), not deleted. The voice is
/// exposed to the library as an ordinary Kokoro style-tensor voice, so the engine loads it like
/// a pack voice.
/// </summary>
public sealed class GeneratedVoiceStore : IGeneratedVoiceStore
{
    private sealed record RecordDto(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("speakerKey")] string SpeakerKey,
        [property: JsonPropertyName("displayName")] string DisplayName,
        [property: JsonPropertyName("phonemizer")] string Phonemizer,
        [property: JsonPropertyName("sex")] string Sex,
        [property: JsonPropertyName("salt")] int Salt,
        [property: JsonPropertyName("sources")] List<PartDto>? Sources,
        [property: JsonPropertyName("pool")] List<string>? Pool,
        [property: JsonPropertyName("createdUtc")] DateTimeOffset CreatedUtc);

    private sealed record PartDto(
        [property: JsonPropertyName("voice")] string Voice,
        [property: JsonPropertyName("weight")] float Weight);

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static readonly long TensorBytes =
        (long)NpyHeader.StyleRows * NpyHeader.StyleColumns * sizeof(float);

    private readonly Func<string> directory;
    private readonly ILogSink? log;
    private readonly object gate = new();
    private Dictionary<string, (GeneratedVoiceRecord Record, VoiceInfo Voice)>? loaded;

    public GeneratedVoiceStore(Func<string> directory, ILogSink? log = null)
    {
        this.directory = directory ?? throw new ArgumentNullException(nameof(directory));
        this.log = log;
    }

    public IReadOnlyList<GeneratedVoiceRecord> Records
    {
        get
        {
            lock (this.gate)
            {
                return [.. this.EnsureLoadedUnlocked().Values.Select(v => v.Record).OrderBy(r => r.Id, StringComparer.Ordinal)];
            }
        }
    }

    public GeneratedVoiceRecord? Find(string? id)
    {
        lock (this.gate)
        {
            return id is not null && this.EnsureLoadedUnlocked().TryGetValue(id, out var entry) ? entry.Record : null;
        }
    }

    public GeneratedVoiceRecord? ForSpeaker(string speakerKey)
    {
        lock (this.gate)
        {
            return this.EnsureLoadedUnlocked().Values
                .Select(v => v.Record)
                .Where(r => string.Equals(r.SpeakerKey, speakerKey, StringComparison.Ordinal))
                .OrderByDescending(r => r.Salt)
                .FirstOrDefault();
        }
    }

    public VoiceInfo? FindVoice(string? id)
    {
        lock (this.gate)
        {
            return id is not null && this.EnsureLoadedUnlocked().TryGetValue(id, out var entry) ? entry.Voice : null;
        }
    }

    public void Save(GeneratedVoiceRecord record, float[] tensor)
    {
        lock (this.gate)
        {
            var entries = this.EnsureLoadedUnlocked();
            var dir = this.directory();
            try
            {
                Directory.CreateDirectory(dir);
                WriteAtomic(this.TensorPath(dir, record.Id), NpyHeader.WriteStyleTensor(tensor));
                var dto = new RecordDto(
                    record.Id,
                    record.SpeakerKey,
                    record.DisplayName,
                    record.Phonemizer,
                    record.Sex,
                    record.Salt,
                    [.. record.Sources.Select(p => new PartDto(p.VoiceId, p.Weight))],
                    [.. record.Pool],
                    record.CreatedUtc);
                WriteAtomic(this.JsonPath(dir, record.Id), JsonSerializer.SerializeToUtf8Bytes(dto, JsonOptions));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                throw new GeneratedVoiceStoreException($"Could not save generated voice \"{record.Id}\": {e.Message}", e);
            }

            entries[record.Id] = (record, this.BuildVoice(dir, record));
        }
    }

    public bool Remove(string id)
    {
        lock (this.gate)
        {
            var existed = this.EnsureLoadedUnlocked().Remove(id);
            if (!IsSafeName(id))
            {
                return existed;
            }

            var dir = this.directory();
            foreach (var path in new[] { this.TensorPath(dir, id), this.JsonPath(dir, id) })
            {
                try
                {
                    if (File.Exists(path))
                    {
                        File.Delete(path);
                        existed = true;
                    }
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    this.log?.Warn($"Generated voice file \"{path}\" could not be deleted: {e.Message}");
                }
            }

            return existed;
        }
    }

    public int Prune(IReadOnlySet<string> keep)
    {
        lock (this.gate)
        {
            var stale = this.EnsureLoadedUnlocked().Keys.Where(id => !keep.Contains(id)).ToList();
            var removed = 0;
            foreach (var id in stale)
            {
                if (this.Remove(id))
                {
                    removed++;
                }
            }

            return removed;
        }
    }

    private Dictionary<string, (GeneratedVoiceRecord Record, VoiceInfo Voice)> EnsureLoadedUnlocked()
    {
        if (this.loaded is { } cached)
        {
            return cached;
        }

        var entries = new Dictionary<string, (GeneratedVoiceRecord, VoiceInfo)>(StringComparer.Ordinal);
        var dir = this.directory();
        if (Directory.Exists(dir))
        {
            foreach (var file in Directory.EnumerateFiles(dir, "*.json").OrderBy(f => f, StringComparer.Ordinal))
            {
                this.TryLoad(dir, file, entries);
            }
        }

        return this.loaded = entries;
    }

    private void TryLoad(string dir, string file, Dictionary<string, (GeneratedVoiceRecord, VoiceInfo)> entries)
    {
        try
        {
            var dto = JsonSerializer.Deserialize<RecordDto>(File.ReadAllBytes(file), JsonOptions);
            if (dto is null
                || !VoiceBlend.IsGeneratedId(dto.Id)
                || !string.Equals(Path.GetFileNameWithoutExtension(file), HashOf(dto.Id), StringComparison.Ordinal)
                || dto.Sources is not { Count: > 0 })
            {
                this.log?.Warn($"Generated voice record \"{file}\" is not valid; ignored.");
                return;
            }

            var record = new GeneratedVoiceRecord(
                dto.Id,
                dto.SpeakerKey,
                dto.DisplayName,
                dto.Phonemizer,
                dto.Sex,
                dto.Salt,
                [.. dto.Sources.Select(p => new BlendPart(p.Voice, p.Weight))],
                [.. dto.Pool ?? []],
                dto.CreatedUtc);
            var tensor = this.TensorPath(dir, record.Id);
            if (!File.Exists(tensor) || new FileInfo(tensor).Length < TensorBytes)
            {
                this.log?.Warn($"Generated voice \"{record.Id}\" has no usable tensor; ignored.");
                return;
            }

            entries[record.Id] = (record, this.BuildVoice(dir, record));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            this.log?.Warn($"Generated voice record \"{file}\" could not be read: {e.Message}");
        }
    }

    private VoiceInfo BuildVoice(string dir, GeneratedVoiceRecord record)
    {
        var british = record.Phonemizer == Phonemizers.EnglishGb;
        var name = $"Generated for {record.DisplayName}";
        return new VoiceInfo(
            record.Id,
            name,
            british ? "en-GB" : "en-US",
            record.Sex,
            "adult",
            "A voice blended from " + string.Join(", ", record.Sources.Select(p => p.VoiceId)) + ".",
            "Derived from its source voices",
            null,
            ["generated"],
            VoiceQuality.Experimental,
            null,
            VoiceBlend.GeneratedPackId,
            [new VoiceRealization(
                RealizationKind.StyleTensor,
                VoicePackManifest.KokoroEngine,
                this.TensorPath(dir, record.Id),
                record.Phonemizer,
                null)]);
    }

    private string TensorPath(string dir, string id) => Path.Combine(dir, HashOf(id) + ".npy");

    private string JsonPath(string dir, string id) => Path.Combine(dir, HashOf(id) + ".json");

    /// <summary>The file stem of an id: the hash after the <c>_gen_</c> marker.</summary>
    private static string HashOf(string id) => id.Length > 7 ? id[7..] : id;

    private static bool IsSafeName(string id) =>
        VoiceBlend.IsGeneratedId(id) && id.AsSpan(7).IndexOfAny(Path.GetInvalidFileNameChars()) < 0;

    private static void WriteAtomic(string path, byte[] bytes)
    {
        var partial = path + ".part";
        File.WriteAllBytes(partial, bytes);
        File.Move(partial, path, overwrite: true);
    }
}
