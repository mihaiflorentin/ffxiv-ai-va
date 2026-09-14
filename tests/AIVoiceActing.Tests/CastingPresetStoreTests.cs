namespace AIVoiceActing.Tests;

using AIVoiceActing.Domain;
using AIVoiceActing.Infrastructure.Storage;
using AIVoiceActing.Ports;
using AIVoiceActing.Tests.Mock;
using Xunit;

public sealed class CastingPresetStoreTests : IDisposable
{
    private readonly string directory = Path.Combine(
        Path.GetTempPath(), "aiva-tests", Guid.NewGuid().ToString("N"));

    private string FilePath => Path.Combine(this.directory, "casting-presets.json");

    public void Dispose()
    {
        if (Directory.Exists(this.directory))
        {
            Directory.Delete(this.directory, recursive: true);
        }
    }

    private JsonCastingPresetStore NewStore() => new(
        this.FilePath,
        BaseMap,
        new FakeLogSink());

    /// <summary>A stand-in base voice map with one variant row and one parked set.</summary>
    private static RaceVoiceMap BaseMap() => new(
        new Dictionary<string, VoiceSlot[]>(StringComparer.Ordinal)
        {
            ["male"] = [new VoiceSlot("bm_lewis", 0.1f, 1f, 1f, 1f)],
            ["female"] = [new VoiceSlot("hf_alpha", 0f, 1f, 1f, 1f)],
            ["ungendered"] = [new VoiceSlot("em_alex", 0f, 1f, 1f, 1f)],
        },
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["6|Male"] = "male",
        },
        new RaceVoiceMap(
            new Dictionary<string, VoiceSlot[]>(StringComparer.Ordinal)
            {
                ["setPixie"] = [new VoiceSlot("bf_lily", 0f, 1.12f, 1.05f, 1f)],
            },
            []));

    private static CastingPreset SamplePreset(string name = "Mine") => new(
        Name: name,
        Buckets: new Dictionary<string, VoiceSlotDto[]>(StringComparer.Ordinal)
        {
            ["6|Male"] = [new VoiceSlotDto("zf_xiaoyi", 0.2f, 1.18f, 1.1f, 1.4f)],
            [CastingDefaults.UnknownBucketKey] = [new VoiceSlotDto("pf_dora")],
        },
        BeastTribes:
        [
            new BeastTribeCast(
                "ixal",
                "Ixal",
                ModelIds: [1106],
                Voices: [new VoiceSlotDto("pm_santa", 0f, 1.08f, 1.1f, 1f)],
                MaleVoices: [new VoiceSlotDto("bm_george")],
                FemaleVoices: []),
        ]);

    [Fact]
    public void FreshStore_ActiveIsDefault_AndNamesListDefaultFirst()
    {
        var store = this.NewStore();
        Assert.Equal("Default", store.ActivePresetName);
        Assert.Equal(["Default"], store.PresetNames);
    }

    [Fact]
    public void GetDefault_DerivesBuckets_AndFullTribeRoster()
    {
        var preset = this.NewStore().GetDefault();

        Assert.Equal("Default", preset.Name);
        // Buckets: one per manifest variant row plus the Unknown row.
        Assert.Equal(2, preset.Buckets.Count);
        Assert.Equal("bm_lewis", Assert.Single(preset.Buckets["6|Male"]).Id);
        Assert.Equal("em_alex", Assert.Single(preset.Buckets[CastingDefaults.UnknownBucketKey]).Id);
        // Tribes: every known tribe in roster order, pools seeded from the parked sets,
        // no gender splits, no bindings.
        Assert.Equal(CastingDefaults.Tribes.Select(t => t.Key), preset.BeastTribes.Select(t => t.Key));
        var pixie = preset.BeastTribes.Single(t => t.Key == "pixie");
        Assert.Equal("bf_lily", Assert.Single(pixie.Voices).Id);
        Assert.Equal(1.12f, pixie.Voices[0].Pitch);
        Assert.Equal(1.05f, pixie.Voices[0].Speed);
        Assert.Empty(pixie.MaleVoices);
        Assert.Empty(pixie.FemaleVoices);
        Assert.All(preset.BeastTribes, tribe => Assert.Empty(tribe.ModelIds));
        // A tribe without a parked set still gets a (empty) row.
        Assert.Empty(preset.BeastTribes.Single(t => t.Key == "dragon").Voices);
    }

    [Fact]
    public void Save_Activate_RoundTripAcrossRecreation()
    {
        var store = this.NewStore();
        store.Save(SamplePreset());
        store.Activate("Mine");

        var reloaded = this.NewStore();
        Assert.Equal("Mine", reloaded.ActivePresetName);
        Assert.Equal(["Default", "Mine"], reloaded.PresetNames);

        var preset = reloaded.Get("Mine");
        Assert.Equal("zf_xiaoyi", Assert.Single(preset.Buckets["6|Male"]).Id);
        Assert.Equal(1.18f, preset.Buckets["6|Male"][0].Pitch);
        var ixal = preset.BeastTribes.Single(t => t.Key == "ixal");
        Assert.Equal([1106], ixal.ModelIds);
        Assert.Equal("pm_santa", Assert.Single(ixal.Voices).Id);
        Assert.Equal("bm_george", Assert.Single(ixal.MaleVoices).Id);
        Assert.Empty(ixal.FemaleVoices);
    }

    [Fact]
    public void Save_DefaultName_Throws()
    {
        var store = this.NewStore();
        Assert.Throws<CastingPresetException>(() => store.Save(SamplePreset("Default")));
        Assert.Throws<CastingPresetException>(() => store.Save(SamplePreset("  ")));
    }

    [Fact]
    public void Get_DefaultName_Throws()
    {
        Assert.Throws<CastingPresetException>(() => this.NewStore().Get("Default"));
        Assert.Throws<CastingPresetException>(() => this.NewStore().Get("Nope"));
    }

    [Fact]
    public void Delete_DefaultName_Throws_AndActiveResetsOnActiveDelete()
    {
        var store = this.NewStore();
        Assert.Throws<CastingPresetException>(() => store.Delete("Default"));

        store.Save(SamplePreset());
        store.Activate("Mine");
        store.Delete("Mine");

        Assert.Equal("Default", store.ActivePresetName);
        Assert.Equal(["Default"], store.PresetNames);

        var reloaded = this.NewStore();
        Assert.Equal("Default", reloaded.ActivePresetName);
    }

    [Fact]
    public void Activate_MissingName_Throws()
    {
        Assert.Throws<CastingPresetException>(() => this.NewStore().Activate("Nope"));
    }

    [Fact]
    public void Pre_v0_0_26_PresetEntries_AreDiscarded_OnLoad()
    {
        Directory.CreateDirectory(this.directory);
        // v1 shape: sets/variants/modelOverrides, no buckets — never migrated.
        File.WriteAllText(this.FilePath, """
            {
              "activePreset": "Old",
              "presets": {
                "Old": {
                  "sets": { "custom-1": [ { "id": "af_heart" } ] },
                  "variants": { "6|Male": "custom-1" },
                  "modelOverrides": {}
                }
              }
            }
            """);
        var log = new FakeLogSink();

        var store = new JsonCastingPresetStore(this.FilePath, BaseMap, log);
        Assert.Equal("Default", store.ActivePresetName);
        Assert.Equal(["Default"], store.PresetNames);
        Assert.Contains(
            log.Snapshot(),
            call => call.Level == "Info" && call.Message.Contains("discarded pre-0.0.26 preset 'Old'"));
    }

    [Fact]
    public void CorruptFile_IsBackedUp_AndStoreStartsFresh()
    {
        Directory.CreateDirectory(this.directory);
        File.WriteAllText(this.FilePath, "{ definitely not json ]]");
        var log = new FakeLogSink();

        var store = new JsonCastingPresetStore(this.FilePath, BaseMap, log);
        Assert.Equal("Default", store.ActivePresetName);
        Assert.True(File.Exists(this.FilePath + ".bak"));
        Assert.Contains(log.Snapshot(), call => call.Level == "Error");

        // The recovered store keeps working.
        store.Save(SamplePreset());
        Assert.Equal(["Default", "Mine"], store.PresetNames);
    }
}
