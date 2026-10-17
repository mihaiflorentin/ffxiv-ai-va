namespace AIVoiceActing.Tests;

using AIVoiceActing.Domain;
using AIVoiceActing.Domain.Pipeline;
using AIVoiceActing.Ports;
using Xunit;

/// <summary>NPC-sheet name index, the chat-echo speaker memory, and age colouring.</summary>
public sealed class SpeakerFactsTests
{
    private static NpcSheetIdentity Npc(byte? race, byte? sex, byte? body = BodyTypes.Adult, int? model = null, byte? tribe = 1) =>
        new(race, tribe, sex, body, model);

    [Fact]
    public void Index_FindsUniqueNames_CaseInsensitive()
    {
        var index = new NpcIdentityIndex();
        index.Add("Alphinaud", Npc(2, 0));
        Assert.True(index.TryGet("alphinaud", out var found));
        Assert.Equal((byte?)2, found.Race);
        Assert.Equal((byte?)0, found.Sex);
    }

    [Fact]
    public void Index_DropsNamesWhoseRowsDisagreeOnRaceOrSex()
    {
        var index = new NpcIdentityIndex();
        index.Add("Ul'dahn merchant", Npc(1, 0));
        index.Add("Ul'dahn merchant", Npc(3, 1));
        index.Add("Ul'dahn merchant", Npc(1, 0));
        Assert.False(index.TryGet("Ul'dahn merchant", out _));
    }

    [Fact]
    public void Index_KeepsAgreedFacts_DropsDisagreeingAge()
    {
        var index = new NpcIdentityIndex();
        index.Add("Gridanian guard", Npc(1, 0, BodyTypes.Adult));
        index.Add("Gridanian guard", Npc(1, 0, BodyTypes.Elderly));
        Assert.True(index.TryGet("Gridanian guard", out var found));
        Assert.Equal((byte?)1, found.Race);
        Assert.Null(found.BodyType);
    }

    [Fact]
    public void Index_IgnoresRowsWithoutRaceOrModel()
    {
        var index = new NpcIdentityIndex();
        index.Add("Destination", Npc(null, 0, model: null));
        Assert.False(index.TryGet("Destination", out _));
        index.Add("Toraal Ja", Npc(null, 0, model: 4062));
        Assert.True(index.TryGet("Toraal Ja", out var tribe));
        Assert.Equal(4062, tribe.ModelCharaId);
    }

    [Fact]
    public void RecentSpeakers_MatchesNormalizedText_WithinTheWindow()
    {
        var recent = new RecentDialogueSpeakers();
        recent.Record("We must  press on.", "Alisaie", 1_000);
        Assert.Equal("Alisaie", recent.Find("we must press on.", 2_000));
        Assert.Null(recent.Find("We must press on.", 1_000 + RecentDialogueSpeakers.WindowMs + 1));
        Assert.Null(recent.Find("Something else.", 2_000));
    }

    [Fact]
    public void RecentSpeakers_LatestSpeakerWins()
    {
        var recent = new RecentDialogueSpeakers();
        recent.Record("Indeed.", "Thancred", 1_000);
        recent.Record("Indeed.", "Urianger", 2_000);
        Assert.Equal("Urianger", recent.Find("Indeed.", 2_500));
    }

    private static VoiceProfile Profile(bool custom = false) =>
        new("npc:x", "bf_emma", 0.2f, DateTimeOffset.UnixEpoch, custom, Pitch: 1.1f, Speed: 1f);

    [Fact]
    public void Age_ChildIsHigherAndQuicker_ElderLowerAndSlower()
    {
        var child = AgeCasting.Apply(Profile(), BodyTypes.Child);
        Assert.Equal(1.1f * AgeCasting.ChildPitch, child.Pitch, 4);
        Assert.Equal(AgeCasting.ChildSpeed, child.Speed, 4);

        var elder = AgeCasting.Apply(Profile(), BodyTypes.Elderly);
        Assert.Equal(1.1f * AgeCasting.ElderPitch, elder.Pitch, 4);
        Assert.Equal(AgeCasting.ElderSpeed, elder.Speed, 4);
    }

    [Fact]
    public void Age_LeavesAdultsUnknownAndOverridesAlone()
    {
        Assert.Equal(Profile(), AgeCasting.Apply(Profile(), BodyTypes.Adult));
        Assert.Equal(Profile(), AgeCasting.Apply(Profile(), null));
        Assert.Equal(Profile(custom: true), AgeCasting.Apply(Profile(custom: true), BodyTypes.Child));
    }

    [Fact]
    public void Age_ChildPitchIsCapped()
    {
        var high = Profile() with { Pitch = 1.35f };
        Assert.Equal(AgeCasting.MaxPitch, AgeCasting.Apply(high, BodyTypes.Child).Pitch, 4);
    }
}
