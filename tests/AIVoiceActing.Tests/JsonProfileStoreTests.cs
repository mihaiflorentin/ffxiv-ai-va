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

    private JsonProfileStore NewStore(
        Func<byte?, byte?, byte?, float>? biasFor = null,
        FakeLogSink? log = null) =>
        new(this.FilePath, log ?? new FakeLogSink(), biasFor);

    private static readonly Func<string[]> Candidates = () => ["alpha", "beta", "gamma"];

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
        var store = this.NewStore(biasFor: (_, _, _) => 0.2f);
        var profile = store.GetOrCreate("pc:Mihai Testa@66", Candidates, race: 3, tribe: 33, sex: 1);

        Assert.Equal(VoiceAssigner.AssignIndex("pc:Mihai Testa@66", 3) switch
        {
            0 => "alpha",
            1 => "beta",
            _ => "gamma",
        }, profile.ReferenceVoiceId);
        Assert.Equal(0.2f, profile.ExaggerationBias);
        Assert.False(profile.Custom);

        Assert.True(File.Exists(this.FilePath));
        Assert.Contains("pc:Mihai Testa@66", File.ReadAllText(this.FilePath));
        Assert.Single(store.Entries);
    }

    [Fact]
    public void GetOrCreate_NeverReassigns_ExistingKey_EvenWithOtherCandidates()
    {
        var store = this.NewStore();
        var first = store.GetOrCreate("npc:feo ul", () => ["alpha"], race: null, tribe: null, sex: null);
        var again = store.GetOrCreate("npc:feo ul", () => ["totally", "different"], race: null, tribe: null, sex: null);
        Assert.Equal(first, again);
    }

    [Fact]
    public void Store_SurvivesInstanceRecreation_WithIdenticalAssignments()
    {
        var keys = Enumerable.Range(0, 20).Select(i => $"npc:npc-{i}").ToArray();
        var firstPass = keys.ToDictionary(
            key => key,
            key => this.NewStore().GetOrCreate(key, Candidates, race: null, tribe: null, sex: null));

        // Fresh store instances over the same file (simulating a game restart).
        var secondStore = this.NewStore();
        foreach (var key in keys)
        {
            var second = secondStore.GetOrCreate(key, Candidates, race: null, tribe: null, sex: null);
            Assert.Equal(firstPass[key].ReferenceVoiceId, second.ReferenceVoiceId);
            Assert.Equal(firstPass[key].ExaggerationBias, second.ExaggerationBias);
            Assert.Equal(firstPass[key].CreatedUtc, second.CreatedUtc);
        }
    }

    [Fact]
    public void SetOverride_Wins_Persists_AndIsCustom()
    {
        var store = this.NewStore();
        store.GetOrCreate("pc:A B@66", Candidates, race: null, tribe: null, sex: null);

        store.SetOverride("pc:A B@66", "warm", 0.35f);
        var overridden = store.GetOrCreate("pc:A B@66", Candidates, race: null, tribe: null, sex: null);
        Assert.Equal(("warm", 0.35f, true), (overridden.ReferenceVoiceId, overridden.ExaggerationBias, overridden.Custom));

        var reloaded = this.NewStore().GetOrCreate("pc:A B@66", Candidates, race: null, tribe: null, sex: null);
        Assert.Equal(("warm", 0.35f, true), (reloaded.ReferenceVoiceId, reloaded.ExaggerationBias, reloaded.Custom));
    }

    [Fact]
    public void CorruptFile_IsBackedUp_AndStoreStartsFresh()
    {
        Directory.CreateDirectory(this.directory);
        File.WriteAllText(this.FilePath, "{ not valid json !!");
        var log = new FakeLogSink();

        var store = this.NewStore(log: log);
        Assert.Empty(store.Entries);
        Assert.True(File.Exists(this.FilePath + ".bak"), "corrupt file was not backed up");
        Assert.Contains("{ not valid json !!", File.ReadAllText(this.FilePath + ".bak"));
        Assert.Contains(log.Snapshot(), call => call.Level == "Error");

        var recovered = store.GetOrCreate("npc:feo ul", Candidates, race: null, tribe: null, sex: null);
        Assert.Contains(recovered.ReferenceVoiceId, Candidates());
        Assert.Single(store.Entries);
    }

    [Fact]
    public void Bias_IsClampedToUnitRange() =>
        Assert.Equal(1f, this.NewStore(biasFor: (_, _, _) => 7f)
            .GetOrCreate("npc:x", Candidates, race: null, tribe: null, sex: null).ExaggerationBias);

    [Fact]
    public void EmptyCandidates_ThrowProfileStoreException()
    {
        var store = this.NewStore();
        Assert.Throws<ProfileStoreException>(
            () => store.GetOrCreate("npc:x", () => [], race: null, tribe: null, sex: null));
    }

    [Fact]
    public void Entries_IsDefensiveCopy()
    {
        var store = this.NewStore();
        store.GetOrCreate("npc:x", Candidates, race: null, tribe: null, sex: null);
        var copy = store.Entries;
        store.SetOverride("npc:x", "warm", 0f);
        Assert.Single(copy);
        Assert.Equal(1, store.Entries.Count);
        Assert.NotEqual(copy, store.Entries);
    }
}
