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
    public void Elezen_UseFrenchVoice()
    {
        var map = LoadDefault();
        // Ishgardian Elezen: ff_siwis is the bank's French voice; males ride it pitched down.
        Assert.All(map.SlotsFor(VoiceGroup.Male, 2).Select(s => s.Id), id => Assert.Equal("ff_siwis", id));
        Assert.All(map.SlotsFor(VoiceGroup.Female, 2).Select(s => s.Id), id => Assert.Equal("ff_siwis", id));
        Assert.All(map.SlotsFor(VoiceGroup.Male, 2), slot => Assert.True(slot.Pitch < 1f));
    }

    [Fact]
    public void Viera_UsesItalianBank()
    {
        var map = LoadDefault();
        // Icelandic does not exist in Kokoro v1.0; the melodic Italian bank stands in.
        Assert.All(map.SlotsFor(VoiceGroup.Male, 8).Select(s => s.Id), id => Assert.Equal("im_nicola", id));
        Assert.All(map.SlotsFor(VoiceGroup.Female, 8).Select(s => s.Id), id => Assert.Equal("if_sara", id));
    }

    [Fact]
    public void RoegadynAndHrothgar_UseDeepSet()
    {
        var map = LoadDefault();
        foreach (var race in new byte?[] { 5, 7 })
        {
            Assert.All(map.SlotsFor(VoiceGroup.Male, race).Select(s => s.Id), id => Assert.StartsWith("am_", id));
            Assert.All(map.SlotsFor(VoiceGroup.Female, race).Select(s => s.Id), id => Assert.StartsWith("af_", id));
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

        // Parked accents must never leak into the picker or any active slot lookup.
        var activeIds = map.DistinctVoiceIds();
        Assert.Empty(map.Disabled.Sets.Values.SelectMany(slots => slots).Select(s => s.Id)
            .Where(id => activeIds.Contains(id)));

        // A race whose accent is parked (e.g. 9) would fall back to the base set, never
        // to a parked one: every active variant resolves inside the active sets.
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
