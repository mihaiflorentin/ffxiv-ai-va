namespace AIVoiceActing.Tests;

using AIVoiceActing.UI.State;
using Xunit;

public sealed class ChannelNamesTests
{
    [Fact]
    public void FriendlyName_MapsCoreChannels()
    {
        Assert.Equal("Say", ChannelNames.FriendlyName(10));
        Assert.Equal("NPC Dialogue", ChannelNames.FriendlyName(61));
        Assert.Equal("Free Company", ChannelNames.FriendlyName(24));
    }

    [Fact]
    public void FriendlyName_MapsGmChannelsWithPrefix()
    {
        Assert.Equal("GM Tell", ChannelNames.FriendlyName(80));
        Assert.Equal("GM Say", ChannelNames.FriendlyName(81));
        Assert.Equal("GM Novice Network", ChannelNames.FriendlyName(94));
    }

    [Fact]
    public void FriendlyName_MapsAdditionalChatTypes()
    {
        Assert.Equal("Damage Dealt By You", ChannelNames.FriendlyName(2729));
        Assert.Equal("Action Readied By Engaged Enemy", ChannelNames.FriendlyName(10283));
    }

    [Fact]
    public void FriendlyName_UnknownChannelRendersBracketedId()
    {
        Assert.Equal("[channel 12345]", ChannelNames.FriendlyName(12345));
        Assert.Equal("[channel 0]", ChannelNames.FriendlyName(0));
    }

    [Fact]
    public void All_IsTheXivChatTypePlusAdditionalUnionWithUniqueIds()
    {
        var all = ChannelNames.All();
        Assert.Equal(all.Count, all.Select(c => c.Id).Distinct().Count());
        Assert.Contains(all, c => c.Id == 61); // NPCDialogue — the default preset's channel
        // 17 AdditionalChatType extras (verified against the Domain enum).
        Assert.Equal(17, all.Count(c => c.Id > 2000));
        Assert.Equal(81, all.Count(c => c.Id <= 2000)); // XivChatType coverage minus None(0)
    }
}
