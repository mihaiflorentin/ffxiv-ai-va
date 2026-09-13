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
    public void Viera_UsesSpanishMale_PortugueseFemale()
    {
        var map = LoadDefault();
        // Kokoro-only casting (2026-09-14): user-picked from the full-bank audition.
        Assert.All(map.SlotsFor(VoiceGroup.Male, 8).Select(s => s.Id), id => Assert.Equal("em_alex", id));
        Assert.All(map.SlotsFor(VoiceGroup.Female, 8).Select(s => s.Id), id => Assert.Equal("pf_dora", id));
    }

    [Fact]
    public void AuRa_UsesChineseBank()
    {
        var map = LoadDefault();
        // zf_/zm_ voices verified by ear on the v1.0 model; jf_/jm_ stay parked.
        Assert.All(map.SlotsFor(VoiceGroup.Male, 6).Select(s => s.Id), id => Assert.Equal("zm_yunjian", id));
        Assert.All(map.SlotsFor(VoiceGroup.Female, 6).Select(s => s.Id), id => Assert.Equal("zf_xiaoxiao", id));
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
        Assert.Contains("setMoogle", map.Disabled.Sets.Keys);

        // Parked SET NAMES must never collide with active set names or variant targets:
        // the picker (DistinctVoiceIds) reads active sets only, so parked casts stay
        // out of the UI while a model id in overridenModelIds.txt can still activate
        // them via SlotsForSet.
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
