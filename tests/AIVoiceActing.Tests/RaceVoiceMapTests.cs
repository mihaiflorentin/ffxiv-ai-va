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
    public void SlotBiases_AreDistinctWithinSet_AndWithinRange(VoiceGroup group)
    {
        var biases = LoadDefault().SlotsFor(group, race: null).Select(slot => slot.ExaggerationBias).ToArray();
        Assert.Equal(biases.Length, biases.Distinct().Count());
        Assert.All(biases, bias => Assert.InRange(bias, 0.05f, 0.25f));
    }

    [Fact]
    public void Unknown_SharesUngenderedSet()
    {
        var map = LoadDefault();
        Assert.Equal(map.SlotsFor(VoiceGroup.Ungendered, race: null), map.SlotsFor(VoiceGroup.Unknown, race: null));
    }

    [Fact]
    public void LalafellAndVieraFemales_UseHighPitchSet()
    {
        var map = LoadDefault();
        var baseFemale = map.SlotsFor(VoiceGroup.Female, race: null);
        foreach (var race in new byte?[] { 3, 8 })
        {
            // High pitch → biased toward higher exaggeration in v1: min bias above base min.
            Assert.True(
                map.SlotsFor(VoiceGroup.Female, race).Min(slot => slot.ExaggerationBias)
                > baseFemale.Min(slot => slot.ExaggerationBias),
                $"race {race} female should use the high-pitch variant");
        }
    }

    [Fact]
    public void RoegadynAndHrothgarMales_UseDeepSet()
    {
        var map = LoadDefault();
        var baseMale = map.SlotsFor(VoiceGroup.Male, race: null);
        foreach (var race in new byte?[] { 5, 7 })
        {
            // Deep voices → biased toward lower exaggeration in v1: max bias below base max.
            Assert.True(
                map.SlotsFor(VoiceGroup.Male, race).Max(slot => slot.ExaggerationBias)
                < baseMale.Max(slot => slot.ExaggerationBias),
                $"race {race} male should use the deep variant");
        }
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
