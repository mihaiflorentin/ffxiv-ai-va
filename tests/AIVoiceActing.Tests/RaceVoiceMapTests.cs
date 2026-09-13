namespace AIVoiceActing.Tests;

using AIVoiceActing.Domain;
using Xunit;

public sealed class RaceVoiceMapTests
{
    private static RaceVoiceMap LoadDefault()
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
        Assert.All(ids, id => Assert.Matches("^[a-z]{2}_[a-z]+$", id));
    }

    [Fact]
    public void Unknown_SharesUngenderedSet()
    {
        var map = LoadDefault();
        Assert.Equal(map.SlotsFor(VoiceGroup.Ungendered, race: null), map.SlotsFor(VoiceGroup.Unknown, race: null));
    }

    [Fact]
    public void LalafellVieraAndHrothgarFemales_UseHighPitchSet()
    {
        var map = LoadDefault();
        var baseFemaleIds = map.SlotsFor(VoiceGroup.Female, race: null).Select(s => s.Id).ToHashSet();
        foreach (var race in new byte?[] { 3, 8 })
        {
            var ids = map.SlotsFor(VoiceGroup.Female, race).Select(s => s.Id).ToArray();
            Assert.Empty(ids.ToHashSet().Intersect(baseFemaleIds));
            Assert.All(ids, id => Assert.StartsWith("af_", id));
        }
    }

    [Fact]
    public void RoegadynAndHrothgarMales_UseDeepSet_VieraStaysUK()
    {
        var map = LoadDefault();
        var baseMaleIds = map.SlotsFor(VoiceGroup.Male, race: null).Select(s => s.Id).ToHashSet();
        foreach (var race in new byte?[] { 5, 8 })
        {
            var ids = map.SlotsFor(VoiceGroup.Male, race).Select(s => s.Id).ToArray();
            Assert.Empty(ids.ToHashSet().Intersect(baseMaleIds));
            Assert.All(ids, id => Assert.StartsWith("am_", id));
        }

        // Viera (7): the requested Icelandic accent does not exist in Kokoro v1.0 —
        // they ride the standard UK male bank.
        Assert.All(map.SlotsFor(VoiceGroup.Male, 7).Select(s => s.Id), id => Assert.StartsWith("bm_", id));
    }

    [Fact]
    public void LalafellSets_CarryChildPitch_BaseSetsStayNatural()
    {
        var map = LoadDefault();
        Assert.All(map.SlotsFor(VoiceGroup.Male, 3), slot => Assert.True(slot.Pitch > 1.1f));
        Assert.All(map.SlotsFor(VoiceGroup.Female, 3), slot => Assert.True(slot.Pitch > 1.1f));
        Assert.All(map.SlotsFor(VoiceGroup.Male, race: null), slot => Assert.Equal(1f, slot.Pitch));
    }

    [Fact]
    public void Elezen_UseBritishAccentSet()
    {
        var map = LoadDefault();
        Assert.All(map.SlotsFor(VoiceGroup.Male, 2).Select(s => s.Id), id => Assert.StartsWith("bm_", id));
        Assert.All(map.SlotsFor(VoiceGroup.Female, 2).Select(s => s.Id), id => Assert.StartsWith("bf_", id));
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
