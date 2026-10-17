namespace AIVoiceActing.Tests;

using AIVoiceActing.Domain;
using AIVoiceActing.Infrastructure.Storage;
using AIVoiceActing.Ports;
using AIVoiceActing.Tests.Mock;
using Xunit;

public sealed class RaceVoiceMapTests
{
    internal static RaceVoiceMap LoadDefault()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "voices.json");
        Assert.True(File.Exists(path), $"Missing copied voices.json at {path}");
        return RaceVoiceMap.FromJson(File.ReadAllText(path));
    }

    [Theory]
    [InlineData(VoiceGroup.Male)]
    [InlineData(VoiceGroup.Female)]
    [InlineData(VoiceGroup.Ungendered)]
    [InlineData(VoiceGroup.Unknown)]
    public void EveryGroup_HasAtLeastOneSlot(VoiceGroup group)
    {
        var slots = LoadDefault().SlotsFor(group, race: null);
        Assert.NotEmpty(slots);
        Assert.All(slots, slot => Assert.False(string.IsNullOrWhiteSpace(slot.Id)));
    }

    [Theory]
    [InlineData(VoiceGroup.Male)]
    [InlineData(VoiceGroup.Female)]
    [InlineData(VoiceGroup.Ungendered)]
    [InlineData(VoiceGroup.Unknown)]
    public void SlotIds_AreDistinctWithinSet_AndAreKokoroVoices(VoiceGroup group)
    {
        var ids = LoadDefault().SlotsFor(group, race: null).Select(slot => slot.Id).ToArray();
        Assert.Equal(ids.Length, ids.Distinct().Count());
        Assert.All(ids, id => Assert.Matches("^([a-z]{2}_[a-z]+|(isles|accented)\\.[a-z0-9_]+)$", id));
    }

    [Fact]
    public void Unknown_SharesUngenderedSet()
    {
        var map = LoadDefault();
        Assert.Equal(map.SlotsFor(VoiceGroup.Ungendered, race: null), map.SlotsFor(VoiceGroup.Unknown, race: null));
    }

    [Fact]
    public void Elezen_AreFormalBritish_StockVoicesOnly()
    {
        var map = LoadDefault();
        // Ishgardian Elezen: formal British. Stock RP voices only, a touch below natural pitch (tall).
        foreach (var group in new[] { VoiceGroup.Male, VoiceGroup.Female })
        {
            var slots = map.SlotsFor(group, 2);
            Assert.True(slots.Length >= 3);
            Assert.All(slots, slot => Assert.StartsWith("b", slot.Id, StringComparison.Ordinal));
            Assert.All(slots, slot => Assert.True(slot.Pitch < 1f));
        }
    }

    [Fact]
    public void LalafellSets_CarryChildPitchAndSpeed_BaseSetsStayNatural()
    {
        var map = LoadDefault();
        Assert.All(map.SlotsFor(VoiceGroup.Male, 3), slot =>
        {
            Assert.True(slot.Pitch > 1.1f);
            Assert.True(slot.Speed > 1f);
        });
        Assert.All(map.SlotsFor(VoiceGroup.Female, 3), slot =>
        {
            Assert.True(slot.Pitch > 1.1f);
            Assert.True(slot.Speed > 1f);
        });
        Assert.All(map.SlotsFor(VoiceGroup.Male, race: null), slot => Assert.Equal(1f, slot.Pitch));
        Assert.All(map.SlotsFor(VoiceGroup.Male, race: null), slot => Assert.Equal(1f, slot.Speed));
    }

    [Fact]
    public void DisabledSets_StayParsedButOutOfTheUI()
    {
        var map = LoadDefault();
        Assert.NotNull(map.Disabled);
        Assert.NotEmpty(map.Disabled!.Sets);
        Assert.Contains("icelandicMale", map.Disabled.Sets.Keys);
        Assert.Contains("setMoogle", map.Disabled.Sets.Keys);
        Assert.Contains("setMoogleMale", map.Disabled.Sets.Keys);
        Assert.Contains("setMoogleFemale", map.Disabled.Sets.Keys);

        // Parked SET NAMES must never collide with active set names or variant targets:
        // the picker (DistinctVoiceIds) reads active sets only, so parked casts stay
        // out of the UI; 0.0.26 seeds the beast-tribe pools from these parked sets
        // instead of activating them via model-id overrides.
        Assert.All(map.Disabled.Sets.Keys, key => Assert.DoesNotContain(key, map.Sets.Keys));
        Assert.All(map.RaceVariants.Values, variant => Assert.Contains(variant, map.Sets.Keys));
    }

    [Fact]
    public void VoicesFor_MatchesSlotIds() =>
        Assert.Equal(
            LoadDefault().SlotsFor(VoiceGroup.Male, race: null).Select(slot => slot.Id).ToArray(),
            LoadDefault().VoicesFor(VoiceGroup.Male, race: null));

    [Fact]
    public void FromJson_NullManifest_Throws()
    {
        Assert.Throws<InvalidOperationException>(() => RaceVoiceMap.FromJson("null"));
    }

    [Fact]
    public void MissingBaseSet_Throws()
    {
        var map = new RaceVoiceMap(
            new Dictionary<string, VoiceSlot[]>(),
            new Dictionary<string, string>());
        Assert.Throws<InvalidOperationException>(() => map.SlotsFor(VoiceGroup.Female, race: null));
    }
}

/// <summary>
/// The shipped manifest's Default casting shape (0.0.26): 17 buckets derived from the
/// variant rows plus Unknown, all 23 beast tribes with seeded pools, and zero American
/// or Japanese-bank voice ids anywhere the Default preset exposes.
/// </summary>
public sealed class DefaultPresetShapeTests
{
    private static JsonCastingPresetStore NewStore() => new(
        Path.Combine(Path.GetTempPath(), $"aiva-default-{Guid.NewGuid():N}.json"),
        RaceVoiceMapTests.LoadDefault,
        new FakeLogSink());

    [Fact]
    public void Default_HasSeventeenBuckets_AndTwentyThreeSeededTribes()
    {
        var preset = NewStore().GetDefault();

        Assert.Equal(17, preset.Buckets.Count);
        Assert.Contains("6|Female", preset.Buckets.Keys);
        Assert.Contains(CastingDefaults.UnknownBucketKey, preset.Buckets.Keys);
        Assert.Equal(CastingDefaults.Tribes.Select(t => t.Key), preset.BeastTribes.Select(t => t.Key));
        Assert.All(
            preset.BeastTribes,
            tribe => Assert.NotEmpty(tribe.Voices));
        // Gendered lists come from setXMale / setXFemale in the parked block.
        Assert.All(preset.BeastTribes, tribe => Assert.True(tribe.MaleVoices.Count + tribe.FemaleVoices.Count > 0, tribe.Key));
        Assert.NotEmpty(preset.BeastTribes.Single(t => t.Key == "pixie").MaleVoices);
        Assert.NotEmpty(preset.BeastTribes.Single(t => t.Key == "pixie").FemaleVoices);
        // Shipped bindings flow from the manifest's beastTribes section, per tribe.
        var manifestBindings = RaceVoiceMapTests.LoadDefault().BeastTribeBindings;
        Assert.All(
            preset.BeastTribes,
            tribe => Assert.Equal(
                manifestBindings.GetValueOrDefault(tribe.Key, []),
                tribe.ModelIds));
        // Sentinels: pixie = Feo Ul's base model (fairies), goblin = shared goblin
        // base (sex bytes male), amaljaa = the Amalj'aa base model.
        Assert.Equal([2520], preset.BeastTribes.Single(t => t.Key == "pixie").ModelIds);
        Assert.Equal([6], preset.BeastTribes.Single(t => t.Key == "goblin").ModelIds);
        Assert.Equal([3], preset.BeastTribes.Single(t => t.Key == "amaljaa").ModelIds);
        Assert.Equal([1740, 2178], preset.BeastTribes.Single(t => t.Key == "ananta").ModelIds);
        Assert.Equal([3263], preset.BeastTribes.Single(t => t.Key == "loporrit").ModelIds);
        Assert.Equal([1101], preset.BeastTribes.Single(t => t.Key == "vanuvanu").ModelIds);
        Assert.Equal([832], preset.BeastTribes.Single(t => t.Key == "vath").ModelIds);
        Assert.Equal([1699], preset.BeastTribes.Single(t => t.Key == "kojin").ModelIds);
        Assert.Equal([2226], preset.BeastTribes.Single(t => t.Key == "namazu").ModelIds);
        Assert.Equal([14], preset.BeastTribes.Single(t => t.Key == "qitari").ModelIds);
        Assert.Equal([2519], preset.BeastTribes.Single(t => t.Key == "numou").ModelIds);
        Assert.Equal([3293], preset.BeastTribes.Single(t => t.Key == "arkasodara").ModelIds);
        Assert.Equal([4065], preset.BeastTribes.Single(t => t.Key == "yokhuy").ModelIds);
        Assert.Equal([1251, 1252, 1253, 1254, 1255, 1256, 1257],
            preset.BeastTribes.Single(t => t.Key == "dragon").ModelIds);
        Assert.Equal([3264, 3313, 1892],
            preset.BeastTribes.Single(t => t.Key == "omicron").ModelIds);
    }

    [Fact]
    public void BeastTribeBindings_Parse_AndDefaultToEmpty()
    {
        const string withBindings = """
            {"sets":{"ungendered":[{"id":"bm_daniel"}]},"raceVariants":{},"beastTribes":{"pixie":[2520,748]}}
            """;
        Assert.Equal([2520, 748], RaceVoiceMap.FromJson(withBindings).BeastTribeBindings["pixie"]);

        const string withoutBindings = """
            {"sets":{"ungendered":[{"id":"bm_daniel"}]},"raceVariants":{}}
            """;
        Assert.Empty(RaceVoiceMap.FromJson(withoutBindings).BeastTribeBindings);
    }
}
