namespace AIVoiceActing.Tests;

using AIVoiceActing.Domain;
using AIVoiceActing.Infrastructure.Storage;
using AIVoiceActing.Ports;
using AIVoiceActing.Tests.Mock;
using AIVoiceActing.UI.State;
using Xunit;

public sealed class CastingDraftTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "aiva-tests", Guid.NewGuid().ToString("N"));
    private int invalidations;

    public void Dispose()
    {
        if (Directory.Exists(this.directory))
        {
            Directory.Delete(this.directory, recursive: true);
        }
    }

    private static RaceVoiceMap BaseMap() => new(
        new Dictionary<string, VoiceSlot[]>(StringComparer.Ordinal)
        {
            ["male"] = [new VoiceSlot("bm_lewis", 0.1f, 1f, 1f, 1f)],
            ["female"] = [new VoiceSlot("bf_emma", 0f, 1f, 1f, 1f)],
            ["ungendered"] = [new VoiceSlot("am_michael", 0f, 1f, 1f, 1f)],
        },
        new Dictionary<string, string>(StringComparer.Ordinal) { ["1|Male"] = "male", ["1|Female"] = "female" });

    private (CastingDraft Draft, JsonCastingPresetStore Store) Make()
    {
        var store = new JsonCastingPresetStore(Path.Combine(this.directory, "presets.json"), BaseMap, new FakeLogSink());
        return (new CastingDraft(() => store, () => this.invalidations++), store);
    }

    [Fact]
    public void EnsureLoaded_StartsOnTheActivePreset()
    {
        var (draft, _) = Make();

        draft.EnsureLoaded();

        Assert.Equal(CastingPreset.DefaultPresetName, draft.Name);
        Assert.True(draft.IsActive);
        Assert.False(draft.Dirty);
    }

    [Fact]
    public void Save_OnTheBuiltInCasting_KeepsTheEditsAsANewActiveCasting()
    {
        var (draft, store) = Make();
        draft.EnsureLoaded();
        draft.Model.AddVoice("1|Male", "am_michael");
        Assert.True(draft.Dirty);

        var outcome = draft.Save();

        Assert.True(outcome.CopiedFromDefault);
        Assert.Equal("My casting", outcome.Name);
        Assert.Equal("My casting", draft.Name);
        Assert.Equal("My casting", store.ActivePresetName);
        Assert.False(draft.Dirty);
        Assert.Contains(store.Get("My casting").Buckets["1|Male"], slot => slot.Id == "am_michael");
        Assert.Equal(1, this.invalidations);
        Assert.Equal(CastingPreset.DefaultPresetName, store.PresetNames[0]);
    }

    [Fact]
    public void Save_OnAUserCasting_WritesInPlaceAndRebuildsTheVoiceMap()
    {
        var (draft, store) = Make();
        draft.EnsureLoaded();
        draft.Model.AddVoice("1|Male", "am_michael");
        draft.Save();
        this.invalidations = 0;

        draft.Model.AddVoice("1|Male", "bf_emma");
        var outcome = draft.Save();

        Assert.False(outcome.CopiedFromDefault);
        Assert.Equal("My casting", outcome.Name);
        Assert.Equal(["My casting"], store.PresetNames.Skip(1));
        Assert.Equal(2, store.Get("My casting").Buckets["1|Male"].Count(slot => slot.Id is "am_michael" or "bf_emma"));
        Assert.Equal(1, this.invalidations);
    }

    [Fact]
    public void Save_OnAPresetThatIsNotActive_DoesNotTouchTheVoiceMap()
    {
        var (draft, store) = Make();
        draft.EnsureLoaded();
        draft.Model.AddVoice("1|Male", "am_michael");
        draft.Save();
        store.Activate(CastingPreset.DefaultPresetName);
        draft.Select("My casting");
        this.invalidations = 0;
        draft.Model.AddVoice("1|Male", "bf_emma");

        draft.Save();

        Assert.False(draft.IsActive);
        Assert.Equal(0, this.invalidations);
    }

    [Fact]
    public void Discard_ReloadsFromTheStore()
    {
        var (draft, _) = Make();
        draft.EnsureLoaded();
        var before = draft.Model.EditBuffer.Buckets["1|Male"].Length;
        draft.Model.AddVoice("1|Male", "am_michael");

        draft.Discard();

        Assert.False(draft.Dirty);
        Assert.Equal(before, draft.Model.EditBuffer.Buckets["1|Male"].Length);
    }

    [Theory]
    [InlineData("", "My casting")]
    [InlineData("My casting", "My casting 2")]
    [InlineData("My casting,my casting 2", "My casting 3")]
    public void UniqueName_SkipsTakenNames(string existing, string expected)
    {
        var names = existing.Length == 0 ? [] : existing.Split(',');

        Assert.Equal(expected, CastingDraft.UniqueName(names));
    }
}
