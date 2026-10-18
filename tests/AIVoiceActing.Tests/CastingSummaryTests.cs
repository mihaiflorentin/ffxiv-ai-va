namespace AIVoiceActing.Tests;

using AIVoiceActing.Domain;
using AIVoiceActing.Ports;
using AIVoiceActing.UI.State;
using Xunit;

public sealed class CastingSummaryTests
{
    private static CastingPreset Preset() => new(
        "Test",
        new Dictionary<string, VoiceSlotDto[]>(StringComparer.Ordinal)
        {
            ["1|Male"] = [new VoiceSlotDto("am_michael"), new VoiceSlotDto("af_heart"), new VoiceSlotDto("am_michael", Pitch: 1.2f)],
            [CastingDefaults.UnknownBucketKey] = [new VoiceSlotDto("bf_emma")],
        },
        [new BeastTribeCast("moogle", "Moogle", [], [new VoiceSlotDto("zz_pom")], [new VoiceSlotDto("bm_lewis")], [])]);

    [Fact]
    public void RaceRows_ListEveryRaceTwiceThenTheCatchAll()
    {
        var rows = CastingSummary.RaceRows(Preset());

        Assert.Equal((Races.All.Count * 2) + 1, rows.Count);
        Assert.Equal("Hyur, male", rows[0].Label);
        Assert.Equal("Hyur, female", rows[1].Label);
        Assert.Equal("Miqo'te, female", rows.Single(r => r.Label.StartsWith("Miqo'te, f")).Label);
        Assert.Equal(CastingDefaults.UnknownBucketKey, rows[^1].Key);
        Assert.Equal("Anyone else", rows[^1].Label);
    }

    [Fact]
    public void RaceRows_CarryTheirSlots_AndDistinctVoiceIds()
    {
        var hyurMale = CastingSummary.RaceRows(Preset())[0];

        Assert.Equal(3, hyurMale.Slots.Count);
        Assert.Equal(["am_michael", "af_heart"], hyurMale.VoiceIds);
        Assert.Empty(CastingSummary.RaceRows(Preset())[1].Slots);
    }

    [Fact]
    public void TribeRows_GatherEveryListOfTheTribe()
    {
        var row = Assert.Single(CastingSummary.TribeRows(Preset()));

        Assert.Equal(["zz_pom", "bm_lewis"], row.VoiceIds);
    }

    [Fact]
    public void ReferencedIds_UnionCastingTribesAndCharacters_SortedAndDistinct()
    {
        var profiles = new[]
        {
            new VoiceProfile("npc:a", "zz_gone", 0f, DateTimeOffset.UnixEpoch, true),
            new VoiceProfile("npc:b", "af_heart", 0f, DateTimeOffset.UnixEpoch, true),
        };

        var ids = CastingSummary.ReferencedIds(Preset(), profiles);

        Assert.Equal(["af_heart", "am_michael", "bf_emma", "bm_lewis", "zz_gone", "zz_pom"], ids);
    }

    [Fact]
    public void VoiceNames_SummaryNamesTheFirstVoicesAndCountsTheRest()
    {
        var library = new AIVoiceActing.Infrastructure.Storage.FileVoiceLibrary(() => [], () => []);

        Assert.Equal("No voices set", VoiceNames.Summary(library, []));
        Assert.Equal("Heart, Michael", VoiceNames.Summary(library, ["af_heart", "am_michael"]));
        Assert.Equal(
            "Heart, Michael, Emma and 1 more",
            VoiceNames.Summary(library, ["af_heart", "am_michael", "bf_emma", "bm_lewis"], show: 3));
        Assert.Equal("zz_gone", VoiceNames.Display(library, "zz_gone"));
        Assert.True(VoiceNames.IsMissing(library, "zz_gone"));
        Assert.False(VoiceNames.IsMissing(library, "af_heart"));
    }
}
