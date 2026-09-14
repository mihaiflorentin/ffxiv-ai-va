namespace AIVoiceActing.UI.State;

using AIVoiceActing.Domain;
using AIVoiceActing.Ports;

/// <summary>
/// Pure General Voices-tab brain: an edit buffer over one <see cref="CastingPreset"/> plus
/// the selected name and a dirty flag. Every mutation is a record-replace (the draw layer
/// re-renders from <see cref="EditBuffer"/>); persistence happens when the window saves
/// the buffer through the port. Knows nothing about ImGui or the store.
/// </summary>
public sealed class CastingTabModel
{
    public CastingPreset EditBuffer { get; private set; } = Empty();

    public string SelectedName { get; private set; } = CastingPreset.DefaultPresetName;

    /// <summary>True when <see cref="EditBuffer"/> differs from what was last loaded/saved.</summary>
    public bool Dirty { get; private set; }

    public static CastingPreset Empty() => new(
        CastingPreset.DefaultPresetName,
        new Dictionary<string, VoiceSlotDto[]>(StringComparer.Ordinal),
        []);

    /// <summary>Switches the editor to <paramref name="name"/>, discarding unsaved edits.</summary>
    public void Load(string name, CastingPreset preset)
    {
        this.SelectedName = name;
        this.EditBuffer = preset;
        this.Dirty = false;
    }

    /// <summary>Deep-copies <paramref name="source"/> (buckets, tribe lists, and model
    /// bindings included) under a new name; the fork starts out unsaved.</summary>
    public void Fork(string sourceName, CastingPreset source, string newName)
    {
        var buckets = source.Buckets.ToDictionary(
            kv => kv.Key,
            kv => (VoiceSlotDto[])[.. kv.Value],
            StringComparer.Ordinal);
        var tribes = source.BeastTribes
            .Select(tribe => tribe with
            {
                ModelIds = [.. tribe.ModelIds],
                Voices = [.. tribe.Voices],
                MaleVoices = [.. tribe.MaleVoices],
                FemaleVoices = [.. tribe.FemaleVoices],
            })
            .ToArray();
        this.Load(newName, new CastingPreset(newName, buckets, tribes));
        this.Dirty = true;
    }

    /// <summary>Starts a from-scratch preset: the 17 race/gender buckets (8 races ×
    /// Male/Female + Unknown) and all known beast tribes, all empty.</summary>
    public void NewScratch(string newName)
    {
        var buckets = new Dictionary<string, VoiceSlotDto[]>(StringComparer.Ordinal);
        foreach (var race in Races.All)
        {
            buckets[CastingDefaults.RaceBucketKey(race.Id, female: false)] = [];
            buckets[CastingDefaults.RaceBucketKey(race.Id, female: true)] = [];
        }

        buckets[CastingDefaults.UnknownBucketKey] = [];
        var tribes = CastingDefaults.Tribes
            .Select(tribe => new BeastTribeCast(tribe.Key, tribe.Name, [], [], [], []))
            .ToArray();
        this.Load(newName, new CastingPreset(newName, buckets, tribes));
        this.Dirty = true;
    }

    // ---- Race/gender buckets ----

    public void SetSlot(string bucketKey, int index, VoiceSlotDto slot)
    {
        var buckets = new Dictionary<string, VoiceSlotDto[]>(this.EditBuffer.Buckets, StringComparer.Ordinal);
        if (buckets.TryGetValue(bucketKey, out var slots)
            && index >= 0
            && index < slots.Length)
        {
            slots[index] = slot;
            this.EditBuffer = this.EditBuffer with { Buckets = buckets };
            this.Dirty = true;
        }
    }

    public void AddVoice(string bucketKey, string defaultVoiceId)
    {
        var buckets = new Dictionary<string, VoiceSlotDto[]>(this.EditBuffer.Buckets, StringComparer.Ordinal);
        if (!buckets.TryGetValue(bucketKey, out var slots))
        {
            slots = [];
        }

        buckets[bucketKey] = [.. slots, new VoiceSlotDto(defaultVoiceId)];
        this.EditBuffer = this.EditBuffer with { Buckets = buckets };
        this.Dirty = true;
    }

    public void RemoveVoice(string bucketKey, int index)
    {
        var buckets = new Dictionary<string, VoiceSlotDto[]>(this.EditBuffer.Buckets, StringComparer.Ordinal);
        if (buckets.TryGetValue(bucketKey, out var slots)
            && index >= 0
            && index < slots.Length)
        {
            buckets[bucketKey] = [.. slots.Take(index), .. slots.Skip(index + 1)];
            this.EditBuffer = this.EditBuffer with { Buckets = buckets };
            this.Dirty = true;
        }
    }

    // ---- Beast-tribe voice lists ----

    public void SetBeastVoice(string tribeKey, BeastVoiceList list, int index, VoiceSlotDto slot) =>
        this.MutateTribeList(tribeKey, list, slots =>
        {
            if (index >= 0 && index < slots.Length)
            {
                slots[index] = slot;
            }

            return slots;
        });

    public void AddBeastVoice(string tribeKey, BeastVoiceList list, string defaultVoiceId) =>
        this.MutateTribeList(tribeKey, list, slots => [.. slots, new VoiceSlotDto(defaultVoiceId)]);

    public void RemoveBeastVoice(string tribeKey, BeastVoiceList list, int index) =>
        this.MutateTribeList(tribeKey, list, slots =>
            index >= 0 && index < slots.Length
                ? [.. slots.Take(index), .. slots.Skip(index + 1)]
                : slots);

    // ---- Beast-tribe model-id bindings ----

    /// <summary>Binds a model id to a tribe, moving it from any other tribe first — an id
    /// resolves to exactly one tribe.</summary>
    public void BindModelId(string tribeKey, int modelId)
    {
        var tribes = this.EditBuffer.BeastTribes
            .Select(tribe => tribe.Key == tribeKey
                ? tribe with { ModelIds = [.. tribe.ModelIds.Where(id => id != modelId), modelId] }
                : tribe with { ModelIds = [.. tribe.ModelIds.Where(id => id != modelId)] })
            .ToArray();
        if (tribes.Any(tribe => tribe.Key == tribeKey))
        {
            this.EditBuffer = this.EditBuffer with { BeastTribes = tribes };
            this.Dirty = true;
        }
    }

    public void UnbindModelId(string tribeKey, int modelId) =>
        this.MutateTribe(tribeKey, tribe => tribe with
        {
            ModelIds = [.. tribe.ModelIds.Where(id => id != modelId)],
        });

    /// <summary>Clears the dirty flag after a successful save/activation round.</summary>
    public void MarkSaved() => this.Dirty = false;

    private void MutateTribeList(string tribeKey, BeastVoiceList list, Func<VoiceSlotDto[], VoiceSlotDto[]> apply) =>
        this.MutateTribe(tribeKey, tribe => list switch
        {
            BeastVoiceList.Male => tribe with { MaleVoices = apply([.. tribe.MaleVoices]) },
            BeastVoiceList.Female => tribe with { FemaleVoices = apply([.. tribe.FemaleVoices]) },
            _ => tribe with { Voices = apply([.. tribe.Voices]) },
        });

    private void MutateTribe(string tribeKey, Func<BeastTribeCast, BeastTribeCast> apply)
    {
        var tribes = this.EditBuffer.BeastTribes.ToArray();
        var index = Array.FindIndex(tribes, tribe => tribe.Key == tribeKey);
        if (index < 0)
        {
            return;
        }

        tribes[index] = apply(tribes[index]);
        this.EditBuffer = this.EditBuffer with { BeastTribes = tribes };
        this.Dirty = true;
    }
}
