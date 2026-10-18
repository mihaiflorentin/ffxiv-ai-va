namespace AIVoiceActing.Tests;

using AIVoiceActing.Domain;
using Xunit;

/// <summary>RaceVoiceMap skips slots whose voice the library has demoted, and falls back when a set empties.</summary>
public sealed class RaceVoiceMapAvailabilityTests
{
    private static RaceVoiceMap Map(Func<string, bool>? available = null) => new(
        new Dictionary<string, VoiceSlot[]>
        {
            ["male"] = [new VoiceSlot("am_adam"), new VoiceSlot("am_echo")],
            ["female"] = [new VoiceSlot("af_heart")],
            ["ungendered"] = [new VoiceSlot("af_heart")],
            ["3|Male"] = [new VoiceSlot("pack.ie_m_01"), new VoiceSlot("pack.ie_m_02", Pitch: 0.9f)],
            ["tribe_pool"] = [new VoiceSlot("pack.ie_m_01")],
        },
        new Dictionary<string, string> { ["3|Male"] = "3|Male" },
        isAvailable: available);

    [Fact]
    public void WithoutPredicate_NothingIsFiltered()
    {
        var map = Map();
        Assert.Equal(["pack.ie_m_01", "pack.ie_m_02"], map.VoicesFor(VoiceGroup.Male, 3));
    }

    [Fact]
    public void UnavailableSlot_IsSkipped_KeepingTheSlotKnobsOfTheRest()
    {
        var map = Map(id => id != "pack.ie_m_01");

        var slots = map.SlotsFor(VoiceGroup.Male, 3);

        var slot = Assert.Single(slots);
        Assert.Equal("pack.ie_m_02", slot.Id);
        Assert.Equal(0.9f, slot.Pitch);
        Assert.Equal(["pack.ie_m_02"], map.VoicesFor(VoiceGroup.Male, 3));
    }

    [Fact]
    public void RaceSetThatBecomesEmpty_FallsBackToTheGroupBaseSet()
    {
        var map = Map(id => !id.StartsWith("pack.", StringComparison.Ordinal));

        Assert.Equal(["am_adam", "am_echo"], map.VoicesFor(VoiceGroup.Male, 3));
    }

    [Fact]
    public void BaseSetFilteredToo_AndWhenItEmptiesItIsReturnedWhole()
    {
        var some = Map(id => id != "am_adam");
        Assert.Equal(["am_echo"], some.VoicesFor(VoiceGroup.Male, race: null));

        // Nothing usable anywhere: never an empty list, the engine's own fallback covers it.
        var none = Map(_ => false);
        Assert.Equal(["am_adam", "am_echo"], none.VoicesFor(VoiceGroup.Male, race: null));
    }

    [Fact]
    public void SlotsForSet_FiltersAndFallsBackToTheWholeSet()
    {
        Assert.Equal(["am_echo"], Map(id => id != "am_adam").SlotsForSet("male").Select(s => s.Id));
        Assert.Equal(["pack.ie_m_01"], Map(_ => false).SlotsForSet("tribe_pool").Select(s => s.Id));
    }

    [Fact]
    public void TrySlotsForSet_ReportsFailureWhenEverySlotIsUnavailable()
    {
        var map = Map(id => !id.StartsWith("pack.", StringComparison.Ordinal));

        Assert.False(map.TrySlotsForSet("tribe_pool", out var slots));
        Assert.Empty(slots);
        Assert.True(map.TrySlotsForSet("male", out var male));
        Assert.Equal(2, male.Length);
    }

    [Fact]
    public void Predicate_IsEvaluatedPerLookup_SoALibraryReloadTakesEffect()
    {
        var gone = false;
        var map = Map(id => !(gone && id == "pack.ie_m_01"));
        Assert.Equal(2, map.SlotsFor(VoiceGroup.Male, 3).Length);

        gone = true;

        Assert.Equal(["pack.ie_m_02"], map.VoicesFor(VoiceGroup.Male, 3));
    }

    [Fact]
    public void WithAvailability_ReturnsAFilteredViewOfTheSameMap()
    {
        var map = LoadDefaultMap();
        var filtered = map.WithAvailability(id => id != "bm_lewis");

        Assert.Contains("bm_lewis", map.VoicesFor(VoiceGroup.Male, 2));
        Assert.DoesNotContain("bm_lewis", filtered.VoicesFor(VoiceGroup.Male, 2));
        Assert.NotNull(filtered.Disabled);
        Assert.Equal(map.Sets.Count, filtered.Sets.Count);
    }

    private static RaceVoiceMap LoadDefaultMap() => RaceVoiceMapTests.LoadDefault();
}
