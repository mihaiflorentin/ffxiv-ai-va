namespace AIVoiceActing.Tests;

using System.Text.Json;
using AIVoiceActing.Container;
using AIVoiceActing.Domain;
using AIVoiceActing.Ports;
using AIVoiceActing.Tests.Mock;
using Xunit;

/// <summary>
/// The active casting preset overlays the base voice map: a preset variant row wins over
/// the shipped race casting, the ungendered row re-points the fallback set, and preset
/// model overrides replace the embedded model table. Invalidating the map makes the next
/// resolution pick up preset mutations.
/// </summary>
public sealed class ContainerCastingPresetTests : IDisposable
{
    private readonly string root = Path.Combine(
        Path.GetTempPath(), "aiva-tests", Guid.NewGuid().ToString("N"));

    public ContainerCastingPresetTests() => Directory.CreateDirectory(this.root);

    public void Dispose()
    {
        if (Directory.Exists(this.root))
        {
            Directory.Delete(this.root, recursive: true);
        }
    }

    private string ManifestPath => Path.Combine(this.root, "voices.json");

    private string PresetStorePath => Path.Combine(this.root, "casting-presets.json");

    private void WriteManifest() => File.WriteAllText(
        this.ManifestPath,
        JsonSerializer.Serialize(new
        {
            sets = new Dictionary<string, object[]>
            {
                ["male"] = [new { id = "bm_lewis", exaggerationBias = 0.1 }],
                ["female"] = [new { id = "af_heart" }],
                ["ungendered"] = [new { id = "em_alex" }],
            },
            raceVariants = new Dictionary<string, string>
            {
                ["6|Male"] = "male",
                ["6|Female"] = "female",
            },
        }));

    private void WritePresetFile(string active, string voiceId) => File.WriteAllText(
        this.PresetStorePath,
        JsonSerializer.Serialize(new
        {
            activePreset = active,
            presets = new Dictionary<string, object>
            {
                [active] = new
                {
                    sets = new Dictionary<string, object[]>
                    {
                        ["custom-1"] = new[] { new { id = voiceId, exaggerationBias = 0.3, pitch = 1.1, speed = 1.05, volume = 1.2 } },
                    },
                    variants = new Dictionary<string, string>
                    {
                        ["6|Male"] = "custom-1",
                    },
                    modelOverrides = new Dictionary<string, object>(),
                },
            },
        }));

    private ServiceContainer NewContainer() => new(
        logSinkOverride: new FakeLogSink(),
        profileStorePathFactory: () => Path.Combine(this.root, "voice-assignments.json"),
        castingPresetsPathFactory: () => this.PresetStorePath,
        modelsDirFactory: () => Path.Combine(this.root, "models"),
        voicesManifestFactory: () => this.ManifestPath,
        speechQueueFactory: () => new FakeSpeechQueue());

    private static SpeakerIdentity AuRaMale() =>
        new("npc:preset-test", "Preset Test", Race: 6, Tribe: 1, Sex: 0, World: null);

    [Fact]
    public void ActivePreset_OverridesVariantCasting_ForNewSpeakers()
    {
        this.WriteManifest();
        this.WritePresetFile("Mine", "zm_yunjian");
        using var container = this.NewContainer();
        var profile = container.ResolveProfile(AuRaMale());

        Assert.Equal("zm_yunjian", profile.ReferenceVoiceId);
        Assert.Equal(1.1f, profile.Pitch);
        var slots = container.VoiceMap.SlotsFor(VoiceGroup.Male, 6);
        Assert.Equal("zm_yunjian", slots.Single().Id);
    }

    [Fact]
    public void DefaultPreset_KeepsShippedCasting()
    {
        this.WriteManifest();

        using var container = this.NewContainer();
        var profile = container.ResolveProfile(AuRaMale());

        Assert.Equal("bm_lewis", profile.ReferenceVoiceId);
    }

    [Fact]
    public void InvalidateVoiceMap_PicksUpPresetMutation()
    {
        this.WriteManifest();
        this.WritePresetFile("Mine", "zm_yunjian");

        using var container = this.NewContainer();
        Assert.Equal("zm_yunjian", container.ResolveProfile(AuRaMale()).ReferenceVoiceId);

        // The UI mutates presets through the port (the store caches in memory), then
        // invalidates the container's voice-map cache.
        var store = container.CastingPresetStore;
        store.Save(store.Get("Mine") with
        {
            Sets = new Dictionary<string, VoiceSlotDto[]>(StringComparer.Ordinal)
            {
                ["custom-1"] = [new VoiceSlotDto("bm_fable", 0.3f, 1.1f, 1.05f, 1.2f)],
            },
        });
        container.InvalidateVoiceMap();

        Assert.Equal("bm_fable", container.ResolveProfile(AuRaMale()).ReferenceVoiceId);
    }

    [Fact]
    public void PresetModelOverrides_ReplaceEmbeddedTable()
    {
        this.WriteManifest();
        File.WriteAllText(
            this.PresetStorePath,
            JsonSerializer.Serialize(new
            {
                activePreset = "Mine",
                presets = new Dictionary<string, object>
                {
                    ["Mine"] = new
                    {
                        sets = new Dictionary<string, object[]>
                        {
                            ["setCustom"] = new[] { new { id = "hf_alpha" } },
                        },
                        variants = new Dictionary<string, string>(),
                        modelOverrides = new Dictionary<string, object>
                        {
                            ["748"] = new { name = "Custom", setKey = "setCustom" },
                            ["749"] = new { name = "Dropped", setKey = "" },
                        },
                    },
                },
            }));

        using var container = this.NewContainer();
        var map = container.ResolveProfile(
            new SpeakerIdentity("npc:model-test", "Model Test", Race: 5, Tribe: 1, Sex: 0, World: null, ModelCharaId: 748));

        Assert.Equal("hf_alpha", map.ReferenceVoiceId);
    }
}
