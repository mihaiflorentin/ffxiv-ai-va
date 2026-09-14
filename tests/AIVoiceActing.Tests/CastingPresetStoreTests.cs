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

    /// <summary>A stand-in base voice map with an active and a parked set.</summary>
    private static RaceVoiceMap BaseMap() => new(
        new Dictionary<string, VoiceSlot[]>(StringComparer.Ordinal)
        {
            ["male"] = [new VoiceSlot("bm_lewis", 0.1f, 1f, 1f, 1f)],
            ["female"] = [new VoiceSlot("af_heart", 0f, 1f, 1f, 1f)],
            ["ungendered"] = [new VoiceSlot("em_alex", 0f, 1f, 1f, 1f)],
        },
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["6|Male"] = "male",
        },
        new RaceVoiceMap(
            new Dictionary<string, VoiceSlot[]>(StringComparer.Ordinal)
            {
                ["setPixie"] = [new VoiceSlot("bf_emma", 0f, 1.25f, 1f, 1f)],
            },
            []));

    private static CastingPreset SamplePreset(string name = "Mine") => new(
        Name: name,
        Sets: new Dictionary<string, VoiceSlotDto[]>(StringComparer.Ordinal)
        {
            ["custom-1"] = [new VoiceSlotDto("zf_xiaoyi", 0.2f, 1.18f, 1.1f, 1.4f)],
        },
        Variants: new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["6|Male"] = "custom-1",
            [CastingPreset.UngenderedVariantKey] = "custom-1",
        },
        ModelOverrides: new Dictionary<string, ModelOverrideDto>(StringComparer.Ordinal)
        {
            ["1106"] = new ModelOverrideDto("Ixal", "setIxal"),
        });

    [Fact]
    public void FreshStore_ActiveIsDefault_AndNamesListDefaultFirst()
    {
        var store = this.NewStore();
        Assert.Equal("Default", store.ActivePresetName);
        Assert.Equal(["Default"], store.PresetNames);
    }

    [Fact]
    public void GetDefault_DerivesBaseAndParkedSets_WithUngenderedRow()
    {
        var preset = this.NewStore().GetDefault();

        Assert.Equal("Default", preset.Name);
        Assert.Contains("male", preset.Sets.Keys);
        Assert.Contains("setPixie", preset.Sets.Keys);
        Assert.Equal("male", preset.Variants["6|Male"]);
        Assert.Equal("ungendered", preset.Variants[CastingPreset.UngenderedVariantKey]);
        Assert.Empty(preset.ModelOverrides);
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
        Assert.Equal("zf_xiaoyi", preset.Sets["custom-1"].Single().Id);
        Assert.Equal(1.18f, preset.Sets["custom-1"].Single().Pitch);
        Assert.Equal("setIxal", preset.ModelOverrides["1106"].SetKey);
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
