namespace AIVoiceActing.Tests;

using AIVoiceActing.Domain;
using AIVoiceActing.Infrastructure.Storage;
using AIVoiceActing.Ports;
using AIVoiceActing.Tests.Mock;
using Xunit;

public sealed class JsonProfileStoreTests : IDisposable
{
    private readonly string directory = Path.Combine(
        Path.GetTempPath(), "aiva-tests", Guid.NewGuid().ToString("N"));

    private string FilePath => Path.Combine(this.directory, "voice-assignments.json");

    private JsonProfileStore NewStore(FakeLogSink? log = null) =>
        new(this.FilePath, log ?? new FakeLogSink());

    private static readonly Func<VoiceSlot[]> Candidates = () =>
    [
        new VoiceSlot("default", 0.05f),
        new VoiceSlot("default", 0.10f),
        new VoiceSlot("default", 0.15f),
    ];

    private static readonly byte?[] AnyDemographics = [3, 33, 1];

    public void Dispose()
    {
        if (Directory.Exists(this.directory))
        {
            Directory.Delete(this.directory, recursive: true);
        }
    }

    [Fact]
    public void GetOrCreate_CreatesAndPersists_OnFirstSight()
    {
        var store = this.NewStore();
        var profile = store.GetOrCreate("pc:Mihai Testa@66", Candidates, AnyDemographics[0], AnyDemographics[1], AnyDemographics[2]);

        // A random first-sight pick: the chosen (voiceId, bias) pair is one of the slots.
        Assert.Contains(
            (profile.ReferenceVoiceId, profile.ExaggerationBias),
            Candidates().Select(slot => (slot.Id, slot.ExaggerationBias)));
        Assert.False(profile.Custom);

        Assert.True(File.Exists(this.FilePath));
        Assert.Contains("pc:Mihai Testa@66", File.ReadAllText(this.FilePath));
        Assert.Single(store.Entries);
    }

    [Fact]
    public void GetOrCreate_PersistsChosenSlotAsUnit_AcrossReload()
    {
        // Same demographics on purpose (all-null race/tribe/sex): the only source of
        // variety is the random pick. The chosen (voiceId, bias) pair persists as a unit.
        var store = this.NewStore();
        var a = store.GetOrCreate("npc:alpha npc", Candidates, null, null, null);
        var b = store.GetOrCreate("npc:gamma npc", Candidates, null, null, null);

        Assert.Contains(
            (a.ReferenceVoiceId, a.ExaggerationBias),
            Candidates().Select(slot => (slot.Id, slot.ExaggerationBias)));
        Assert.Contains(
            (b.ReferenceVoiceId, b.ExaggerationBias),
            Candidates().Select(slot => (slot.Id, slot.ExaggerationBias)));

        // And the (voiceId, bias) pairs persist across store instances as one unit.
        var reloaded = this.NewStore();
        Assert.Equal(
            (a.ReferenceVoiceId, a.ExaggerationBias),
            (reloaded.GetOrCreate("npc:alpha npc", Candidates, null, null, null).ReferenceVoiceId,
             reloaded.GetOrCreate("npc:alpha npc", Candidates, null, null, null).ExaggerationBias));
    }

    [Fact]
    public void GetOrCreate_NeverReassigns_ExistingKey_EvenWithOtherCandidates()
    {
        var store = this.NewStore();
        var first = store.GetOrCreate("npc:feo ul", () => [new VoiceSlot("alpha", 0.1f)], null, null, null);
        var again = store.GetOrCreate(
            "npc:feo ul", () => [new VoiceSlot("totally", 0.2f), new VoiceSlot("different", 0.3f)], null, null, null);
        Assert.Equal(first, again);
    }

    [Fact]
    public void Store_SurvivesInstanceRecreation_WithIdenticalAssignments()
    {
        var keys = Enumerable.Range(0, 20).Select(i => $"npc:npc-{i}").ToArray();
        var firstPass = keys.ToDictionary(
            key => key,
            key => this.NewStore().GetOrCreate(key, Candidates, null, null, null));

        // Fresh store instances over the same file (simulating a game restart).
        var secondStore = this.NewStore();
        foreach (var key in keys)
        {
            var second = secondStore.GetOrCreate(key, Candidates, null, null, null);
            Assert.Equal(firstPass[key].ReferenceVoiceId, second.ReferenceVoiceId);
            Assert.Equal(firstPass[key].ExaggerationBias, second.ExaggerationBias);
            Assert.Equal(firstPass[key].CreatedUtc, second.CreatedUtc);
        }
    }

    [Fact]
    public void SetOverride_Wins_Persists_AndIsCustom()
    {
        var store = this.NewStore();
        store.GetOrCreate("pc:A B@66", Candidates, null, null, null);

        store.SetOverride("pc:A B@66", "warm", 0.35f);
        var overridden = store.GetOrCreate("pc:A B@66", Candidates, null, null, null);
        Assert.Equal(("warm", 0.35f, true), (overridden.ReferenceVoiceId, overridden.ExaggerationBias, overridden.Custom));

        var reloaded = this.NewStore().GetOrCreate("pc:A B@66", Candidates, null, null, null);
        Assert.Equal(("warm", 0.35f, true), (reloaded.ReferenceVoiceId, reloaded.ExaggerationBias, reloaded.Custom));
    }

    [Fact]
    public void SetOverride_PersistsSpeedAndVolume_AcrossReload()
    {
        var store = this.NewStore();

        store.SetOverride("npc:feo ul", "warm", 0.2f, volume: 1.5f, pitch: 1.18f, speed: 1.1f);
        var reloaded = this.NewStore().Entries.Single(e => e.SpeakerKey == "npc:feo ul");

        Assert.Equal(1.5f, reloaded.Volume);
        Assert.Equal(1.18f, reloaded.Pitch);
        Assert.Equal(1.1f, reloaded.Speed);
    }

    [Fact]
    public void SetOverride_KeepsPreviousPitchAndSpeed_WhenNotSupplied()
    {
        var store = this.NewStore();
        store.SetOverride("npc:feo ul", "warm", 0f, volume: 1f, pitch: 1.18f, speed: 1.1f);

        store.SetOverride("npc:feo ul", "brisk", 0.4f);
        var entry = store.Entries.Single(e => e.SpeakerKey == "npc:feo ul");

        Assert.Equal("brisk", entry.ReferenceVoiceId);
        Assert.Equal(1.18f, entry.Pitch);
        Assert.Equal(1.1f, entry.Speed);
    }

    [Fact]
    public void Clear_EmptiesAllEntries_AndPersists()
    {
        var store = this.NewStore();
        store.SetOverride("npc:feo ul", "warm", 0f);
        store.SetOverride("pc:A B@66", "warm", 0f);

        store.Clear();

        Assert.Empty(store.Entries);
        Assert.Empty(this.NewStore().Entries);
    }

    [Fact]
    public void CorruptFile_IsBackedUp_AndStoreStartsFresh()
    {
        Directory.CreateDirectory(this.directory);
        File.WriteAllText(this.FilePath, "{ not valid json !!");
        var log = new FakeLogSink();

        var store = this.NewStore(log);
        Assert.Empty(store.Entries);
        Assert.True(File.Exists(this.FilePath + ".bak"), "corrupt file was not backed up");
        Assert.Contains("{ not valid json !!", File.ReadAllText(this.FilePath + ".bak"));
        Assert.Contains(log.Snapshot(), call => call.Level == "Error");

        // Store still works after recovery.
        var recovered = store.GetOrCreate("npc:feo ul", Candidates, null, null, null);
        Assert.Contains(recovered.ReferenceVoiceId, Candidates().Select(slot => slot.Id));
        Assert.Single(store.Entries);
    }

    [Fact]
    public void EmptyCandidates_ThrowProfileStoreException()
    {
        var store = this.NewStore();
        Assert.Throws<ProfileStoreException>(
            () => store.GetOrCreate("npc:x", () => [], null, null, null));
    }

    [Fact]
    public void Entries_IsDefensiveCopy()
    {
        var store = this.NewStore();
        store.GetOrCreate("npc:x", Candidates, null, null, null);
        var copy = store.Entries;
        store.SetOverride("npc:x", "warm", 0f);
        Assert.Single(copy);
        Assert.Single(store.Entries);
        Assert.NotEqual(copy, store.Entries);
    }
}
