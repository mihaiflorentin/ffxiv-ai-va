namespace AIVoiceActing.Tests;

using System.Text.Json;
using AIVoiceActing.Container;
using AIVoiceActing.Domain;
using AIVoiceActing.Ports;
using AIVoiceActing.Tests.Mock;
using Xunit;

/// <summary>
/// The active casting preset overlays the base voice map with its flat grid: race/gender
/// buckets win over the shipped rows, the Unknown row covers unlisted speakers, and
/// bound beast-tribe model ids route by the speaker's sex byte (gendered list first,
/// then the tribe pool). Invalidating the map makes the next resolution pick up preset
/// mutations.
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
                ["female"] = [new { id = "hf_alpha" }],
                ["ungendered"] = [new { id = "em_alex" }],
            },
            raceVariants = new Dictionary<string, string>
            {
                ["6|Male"] = "male",
                ["6|Female"] = "female",
            },
            disabledSets = new
            {
                sets = new Dictionary<string, object[]>
                {
                    ["setPixie"] = new[] { new { id = "bf_lily", pitch = 1.07, speed = 1.1 } },
                },
                raceVariants = new Dictionary<string, string>(),
            },
            beastTribes = new Dictionary<string, int[]>
            {
                ["pixie"] = [748],
            },
        }));

    private void WritePresetFile(string active, string auRaMaleVoiceId) => File.WriteAllText(
        this.PresetStorePath,
        JsonSerializer.Serialize(new
        {
            activePreset = active,
            presets = new Dictionary<string, object>
            {
                [active] = new
                {
                    buckets = new Dictionary<string, object[]>
                    {
                        ["6|Male"] = new[] { new { id = auRaMaleVoiceId, exaggerationBias = 0.3, pitch = 1.1, speed = 1.05, volume = 1.2 } },
                        ["ungendered"] = new[] { new { id = "pf_dora" } },
                    },
                    beastTribes = Array.Empty<object>(),
                },
            },
        }));

    private void WriteBeastPresetFile(string maleVoiceId = "bm_george") => File.WriteAllText(
        this.PresetStorePath,
        JsonSerializer.Serialize(new
        {
            activePreset = "Mine",
            presets = new Dictionary<string, object>
            {
                ["Mine"] = new
                {
                    buckets = new Dictionary<string, object[]>(),
                    beastTribes = new[]
                    {
                        new
                        {
                            key = "pixie",
                            name = "Pixie",
                            modelIds = new[] { 748 },
                            voices = new[] { new { id = "bf_lily" } },
                            maleVoices = maleVoiceId is { Length: > 0 }
                                ? new[] { new { id = maleVoiceId } }
                                : Array.Empty<object>(),
                            femaleVoices = new[] { new { id = "zf_xiaobei" } },
                        },
                    },
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

    private string ResolveBeast(byte? sex, int model, string key) =>
        this.NewContainer().ResolveProfile(
            new SpeakerIdentity(key, "Beast", Race: null, Tribe: 1, Sex: sex, World: null, ModelCharaId: model))
            .ReferenceVoiceId;

    [Fact]
    public void ActivePreset_OverridesBucketCasting_ForNewSpeakers()
    {
        this.WriteManifest();
        this.WritePresetFile("Mine", "zm_yunjian");
        using var container = this.NewContainer();
        var profile = container.ResolveProfile(AuRaMale());

        Assert.Equal("zm_yunjian", profile.ReferenceVoiceId);
        Assert.Equal(1.1f, profile.Pitch);
        var slots = container.VoiceMap.SlotsFor(VoiceGroup.Male, 6);
        Assert.Equal("zm_yunjian", Assert.Single(slots).Id);
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
    public void DefaultPreset_ResolvesShippedBeastBinding()
    {
        // With Default active, the manifest's own beastTribes bindings route the
        // bound model to the tribe's pool — no user preset required.
        this.WriteManifest();

        using var container = this.NewContainer();
        var profile = container.ResolveProfile(
            new SpeakerIdentity(
                "npc:shipped-pixie", "Shipped Pixie", Race: null, Tribe: 1, Sex: null, World: null, ModelCharaId: 748));

        Assert.Equal("bf_lily", profile.ReferenceVoiceId);
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
            Buckets = new Dictionary<string, VoiceSlotDto[]>(StringComparer.Ordinal)
            {
                ["6|Male"] = [new VoiceSlotDto("bm_fable", 0.3f, 1.1f, 1.05f, 1.2f)],
            },
        });
        container.InvalidateVoiceMap();

        Assert.Equal("bm_fable", container.ResolveProfile(AuRaMale()).ReferenceVoiceId);
    }

    [Fact]
    public void BeastTribeBinding_RoutesBySex_AndFallsBackToPool()
    {
        this.WriteManifest();
        this.WriteBeastPresetFile();

        // Sex 1 → female list; sex 0 → male list; unknown sex → the tribe pool.
        Assert.Equal("zf_xiaobei", this.ResolveBeast(sex: 1, model: 748, "npc:beast-f"));
        Assert.Equal("bm_george", this.ResolveBeast(sex: 0, model: 748, "npc:beast-m"));
        Assert.Equal("bf_lily", this.ResolveBeast(sex: null, model: 748, "npc:beast-u"));
    }

    [Fact]
    public void BeastTribeBinding_EmptyGenderList_FallsBackToPool()
    {
        this.WriteManifest();
        this.WriteBeastPresetFile(maleVoiceId: "");

        Assert.Equal("bf_lily", this.ResolveBeast(sex: 0, model: 748, "npc:beast-m"));
        Assert.Equal("zf_xiaobei", this.ResolveBeast(sex: 1, model: 748, "npc:beast-f"));
    }

    [Fact]
    public void BeastTribe_SameSpeaker_ResolvesIdenticallyTwice()
    {
        this.WriteManifest();
        this.WriteBeastPresetFile();

        using var container = this.NewContainer();
        var speaker = new SpeakerIdentity(
            "npc:beast-stable", "Beast Stable", Race: null, Tribe: 1, Sex: null, World: null, ModelCharaId: 748);
        var first = container.ResolveProfile(speaker);
        var second = container.ResolveProfile(speaker);

        Assert.Equal(first.ReferenceVoiceId, second.ReferenceVoiceId);
        Assert.Equal("bf_lily", first.ReferenceVoiceId);
    }
}
