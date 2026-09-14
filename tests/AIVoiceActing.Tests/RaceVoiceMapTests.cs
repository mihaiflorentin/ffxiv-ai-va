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
        // zf_/zm_ verified by ear on the v1.0 model. The jf_ (Japanese) pair left in
        // 0.0.26: the bank garbles English on v1.0; zf_xiaobei/zf_xiaoni replaced it.
        Assert.Equal(
            new HashSet<string> { "zm_yunjian", "zm_yunyang" },
            map.SlotsFor(VoiceGroup.Male, 6).Select(s => s.Id).ToHashSet());
        Assert.Equal(
            new HashSet<string> { "zf_xiaoxiao", "zf_xiaoyi", "zf_xiaobei", "zf_xiaoni" },
            map.SlotsFor(VoiceGroup.Female, 6).Select(s => s.Id).ToHashSet());
    }

    [Fact]
    public void Roegadyn_And_Hrothgar_ShareItalianBankCast()
    {
        var map = LoadDefault();
        // Roegadyn: im_nicola/em_santa/hm_psi on user verdict (am_fenrir disliked).
        // Hrothgar: no suitable voices found, so it shares the Roegadyn cast.
        foreach (var race in new byte?[] { 5, 7 })
        {
            Assert.Equal(
                new HashSet<string> { "im_nicola", "em_santa", "hm_psi" },
                map.SlotsFor(VoiceGroup.Male, race).Select(s => s.Id).ToHashSet());
            Assert.All(map.SlotsFor(VoiceGroup.Female, race).Select(s => s.Id), id => Assert.Equal("if_sara", id));
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
        Assert.All(preset.BeastTribes, tribe =>
        {
            Assert.Empty(tribe.MaleVoices);
            Assert.Empty(tribe.FemaleVoices);
        });
        // The shipped pixie binding: Feo Ul's base model id (user-confirmed fairies).
        Assert.Equal([2520], preset.BeastTribes.Single(t => t.Key == "pixie").ModelIds);
        Assert.All(
            preset.BeastTribes.Where(t => t.Key != "pixie"),
            tribe => Assert.Empty(tribe.ModelIds));
    }

    [Fact]
    public void BeastTribeBindings_Parse_AndDefaultToEmpty()
    {
        const string withBindings = """
            {"sets":{"ungendered":[{"id":"em_alex"}]},"raceVariants":{},"beastTribes":{"pixie":[2520,748]}}
            """;
        Assert.Equal([2520, 748], RaceVoiceMap.FromJson(withBindings).BeastTribeBindings["pixie"]);

        const string withoutBindings = """
            {"sets":{"ungendered":[{"id":"em_alex"}]},"raceVariants":{}}
            """;
        Assert.Empty(RaceVoiceMap.FromJson(withoutBindings).BeastTribeBindings);
    }

    [Fact]
    public void Default_ContainsNoAmericanOrJapaneseVoiceIds()
    {
        var preset = NewStore().GetDefault();

        var ids = preset.Buckets.Values.SelectMany(slots => slots).Select(slot => slot.Id)
            .Concat(preset.BeastTribes.SelectMany(t => t.Voices).Select(slot => slot.Id))
            .ToHashSet();

        Assert.DoesNotContain(ids, id => id.StartsWith("af_", StringComparison.Ordinal));
        Assert.DoesNotContain(ids, id => id.StartsWith("am_", StringComparison.Ordinal));
        Assert.DoesNotContain(ids, id => id.StartsWith("jf_", StringComparison.Ordinal));
    }

    [Fact]
    public void Default_AuRaFemale_UsesChineseBank()
    {
        var preset = NewStore().GetDefault();

        Assert.Equal(
            ["zf_xiaoxiao", "zf_xiaoyi", "zf_xiaobei", "zf_xiaoni"],
            preset.Buckets["6|Female"].Select(slot => slot.Id));
    }

    [Fact]
    public void Default_PixiePool_IsFemaleOnly_AtApprovedKnobs()
    {
        // User-approved pixie sound (Guyf Uin, 2026-09-14): hf_ female pair only,
        // pitched/paced like her tuned override — fairies are genderless.
        var pixie = NewStore().GetDefault().BeastTribes.Single(t => t.Key == "pixie");

        Assert.Equal(["hf_alpha", "hf_beta"], pixie.Voices.Select(slot => slot.Id));
        Assert.All(pixie.Voices, slot =>
        {
            Assert.Equal(1.07f, slot.Pitch);
            Assert.Equal(1.1f, slot.Speed);
        });
    }

}
