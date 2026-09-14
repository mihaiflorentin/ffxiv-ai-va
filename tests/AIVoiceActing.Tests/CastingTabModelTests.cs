namespace AIVoiceActing.Tests;

using AIVoiceActing.Domain;
using AIVoiceActing.Ports;
using AIVoiceActing.UI.State;
using Xunit;

public sealed class CastingTabModelTests
{
    private static CastingPreset Loaded() => new(
        "Mine",
        new Dictionary<string, VoiceSlotDto[]>(StringComparer.Ordinal)
        {
            ["1|Male"] = [new VoiceSlotDto("bm_lewis")],
            [CastingDefaults.UnknownBucketKey] = [],
        },
        [new BeastTribeCast("pixie", "Pixie", [748], [new VoiceSlotDto("bf_lily")], [], [])]);

    private static CastingTabModel NewModel()
    {
        var model = new CastingTabModel();
        model.Load("Mine", Loaded());
        return model;
    }

    [Fact]
    public void SetSlot_ReplacesInPlace_AndMarksDirty()
    {
        var model = NewModel();
        model.SetSlot("1|Male", 0, new VoiceSlotDto("bm_fable", 0.1f, 1.2f));

        Assert.True(model.Dirty);
        Assert.Equal("bm_fable", model.EditBuffer.Buckets["1|Male"][0].Id);
        Assert.Equal(1.2f, model.EditBuffer.Buckets["1|Male"][0].Pitch);
    }

    [Fact]
    public void AddVoice_Appends_AndRemoveVoice_Deletes()
    {
        var model = NewModel();
        model.AddVoice(CastingDefaults.UnknownBucketKey, "em_alex");
        Assert.Equal(["em_alex"], model.EditBuffer.Buckets[CastingDefaults.UnknownBucketKey].Select(s => s.Id));

        model.AddVoice(CastingDefaults.UnknownBucketKey, "hf_alpha");
        model.RemoveVoice(CastingDefaults.UnknownBucketKey, 0);
        Assert.Equal(["hf_alpha"], model.EditBuffer.Buckets[CastingDefaults.UnknownBucketKey].Select(s => s.Id));
    }

    [Fact]
    public void BeastLists_MutateIndependently()
    {
        var model = NewModel();
        model.AddBeastVoice("pixie", BeastVoiceList.Male, "bm_george");
        model.AddBeastVoice("pixie", BeastVoiceList.Female, "zf_xiaobei");
        model.SetBeastVoice("pixie", BeastVoiceList.Pool, 0, new VoiceSlotDto("bf_alice"));
        model.RemoveBeastVoice("pixie", BeastVoiceList.Pool, 0);

        var pixie = model.EditBuffer.BeastTribes.Single(t => t.Key == "pixie");
        Assert.Empty(pixie.Voices);
        Assert.Equal("bm_george", Assert.Single(pixie.MaleVoices).Id);
        Assert.Equal("zf_xiaobei", Assert.Single(pixie.FemaleVoices).Id);
        Assert.True(model.Dirty);
    }

    [Fact]
    public void BindModelId_MovesIdBetweenTribes()
    {
        var model = NewModel();
        model.Load("Mine", model.EditBuffer with
        {
            BeastTribes =
            [
                .. model.EditBuffer.BeastTribes,
                new BeastTribeCast("qitari", "Qitari", [999], [], [], []),
            ],
        });

        model.BindModelId("qitari", 748);

        Assert.DoesNotContain(748, model.EditBuffer.BeastTribes.Single(t => t.Key == "pixie").ModelIds);
        Assert.Equal([999, 748], model.EditBuffer.BeastTribes.Single(t => t.Key == "qitari").ModelIds);
    }

    [Fact]
    public void UnbindModelId_RemovesBinding()
    {
        var model = NewModel();
        model.UnbindModelId("pixie", 748);
        Assert.Empty(model.EditBuffer.BeastTribes.Single(t => t.Key == "pixie").ModelIds);
    }

    [Fact]
    public void NewScratch_BuildsSeventeenBuckets_AndAllTribes()
    {
        var model = new CastingTabModel();
        model.NewScratch("Scratch");

        var buckets = model.EditBuffer.Buckets;
        Assert.Equal(17, buckets.Count);
        foreach (var race in Races.All)
        {
            Assert.Contains(CastingDefaults.RaceBucketKey(race.Id, female: false), buckets);
            Assert.Contains(CastingDefaults.RaceBucketKey(race.Id, female: true), buckets);
        }

        Assert.Contains(CastingDefaults.UnknownBucketKey, buckets);
        Assert.All(buckets, kv => Assert.Empty(kv.Value));
        Assert.Equal(CastingDefaults.Tribes.Select(t => t.Key), model.EditBuffer.BeastTribes.Select(t => t.Key));
        Assert.All(model.EditBuffer.BeastTribes, tribe =>
        {
            Assert.Empty(tribe.Voices);
            Assert.Empty(tribe.MaleVoices);
            Assert.Empty(tribe.FemaleVoices);
            Assert.Empty(tribe.ModelIds);
        });
        Assert.True(model.Dirty);
    }

    [Fact]
    public void Fork_DeepCopiesBucketsAndTribes()
    {
        var model = new CastingTabModel();
        var source = Loaded();
        model.Fork("Mine", source, "Copy");

        model.AddVoice("1|Male", "bm_fable");
        model.BindModelId("pixie", 123);

        Assert.Single(source.Buckets["1|Male"]);
        Assert.Equal([748], source.BeastTribes[0].ModelIds);
        Assert.Equal("Copy", model.SelectedName);
        Assert.True(model.Dirty);
    }
}
