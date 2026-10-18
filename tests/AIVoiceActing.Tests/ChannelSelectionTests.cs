namespace AIVoiceActing.Tests;

using AIVoiceActing.UI.State;
using Xunit;

public sealed class ChannelSelectionTests
{
    private static IReadOnlyList<ChannelNames.Channel> Category(string name) =>
        ChannelNames.ByCategory().Single(g => g.Category == name).Channels;

    [Fact]
    public void Totals_CountOnlyKnownChannels()
    {
        var enabled = new List<int> { 10, 61, 999999 };

        var (on, total) = ChannelSelection.Totals(enabled);

        Assert.Equal(2, on);
        Assert.Equal(ChannelNames.All().Count, total);
    }

    [Fact]
    public void CountOn_AndAllOn_ReflectTheGroup()
    {
        var party = Category("Party");
        var enabled = new List<int> { party[0].Id };

        Assert.Equal(1, ChannelSelection.CountOn(party, enabled));
        Assert.False(ChannelSelection.AllOn([.. party], enabled));

        ChannelSelection.SetAll(enabled, party, on: true);
        Assert.True(ChannelSelection.AllOn([.. party], enabled));
        Assert.Equal(party.Count, ChannelSelection.CountOn(party, enabled));
    }

    [Fact]
    public void AllOn_EmptyGroup_IsFalse()
    {
        Assert.False(ChannelSelection.AllOn([], new List<int> { 10 }));
    }

    [Fact]
    public void SetAll_On_AddsOnlyMissingIds_WithoutDuplicates()
    {
        var speech = Category("Speech");
        var enabled = new List<int> { speech[0].Id, 12345 };

        ChannelSelection.SetAll(enabled, speech, on: true);
        ChannelSelection.SetAll(enabled, speech, on: true);

        Assert.Equal(enabled.Count, enabled.Distinct().Count());
        Assert.Contains(12345, enabled);
        Assert.Equal(speech.Count, ChannelSelection.CountOn(speech, enabled));
    }

    [Fact]
    public void SetAll_Off_RemovesTheGroupAndKeepsEverythingElse()
    {
        var party = Category("Party");
        var enabled = new List<int> { 10, 61, 12345 };
        enabled.AddRange(party.Select(c => c.Id));

        ChannelSelection.SetAll(enabled, party, on: false);

        Assert.Equal(0, ChannelSelection.CountOn(party, enabled));
        Assert.Equal([10, 61, 12345], enabled);
    }

    [Fact]
    public void SetAll_OverASearchResult_TouchesOnlyTheShownChannels()
    {
        var shown = ChannelNames.Search("linkshell 3").SelectMany(g => g.Channels).ToArray();
        var enabled = new List<int>();

        ChannelSelection.SetAll(enabled, shown, on: true);

        Assert.Equal(shown.Select(c => c.Id).Order(), enabled.Order());
        Assert.DoesNotContain(ChannelNames.All().First(c => c.Name == "Linkshell 4").Id, enabled);
    }

    [Fact]
    public void Set_SingleChannel_TogglesIdempotently()
    {
        var enabled = new List<int>();

        ChannelSelection.Set(enabled, 10, on: true);
        ChannelSelection.Set(enabled, 10, on: true);
        Assert.Equal([10], enabled);

        ChannelSelection.Set(enabled, 10, on: false);
        ChannelSelection.Set(enabled, 10, on: false);
        Assert.Empty(enabled);
    }
}
