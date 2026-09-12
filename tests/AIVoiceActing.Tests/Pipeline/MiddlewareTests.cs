namespace AIVoiceActing.Tests.Pipeline;

using AIVoiceActing.Domain;
using AIVoiceActing.Domain.Chat;
using AIVoiceActing.Domain.Pipeline;
using AIVoiceActing.Ports;
using Xunit;

public sealed class MiddlewareTests
{
    private static SpeakerAnnouncer Announcer(
        bool enableNameWithSay = true,
        bool nameNpcWithSay = true,
        bool disallowMultipleSay = false,
        bool sayPartialName = false,
        FirstOrLastName part = FirstOrLastName.First) =>
        new(() => enableNameWithSay, () => nameNpcWithSay, () => disallowMultipleSay,
            () => sayPartialName, () => part);

    [Fact]
    public void ShouldProcessSpeaker_OffWhenNameWithSayDisabled()
    {
        Assert.False(Announcer(enableNameWithSay: false).ShouldProcessSpeaker("Yda"));
    }

    [Fact]
    public void ShouldProcessSpeaker_TalkPath_RequiresNameNpcWithSay()
    {
        Assert.False(Announcer(nameNpcWithSay: false).ShouldProcessSpeaker("Yda"));
        Assert.True(Announcer().ShouldProcessSpeaker("Yda"));
    }

    [Fact]
    public void ShouldProcessSpeaker_EmptySpeakerNeverAnnounced()
    {
        Assert.False(Announcer().ShouldProcessSpeaker(""));
        Assert.False(Announcer().ShouldProcessSpeaker(null));
    }

    [Fact]
    public void DisallowMultipleSay_SuppressesSameSpeaker_AllowsChanged()
    {
        var announcer = Announcer(disallowMultipleSay: true);
        Assert.True(announcer.ShouldProcessSpeaker("Yda"));
        announcer.SetLastSpeaker("Yda");
        Assert.False(announcer.ShouldProcessSpeaker("Yda"));
        Assert.True(announcer.ShouldProcessSpeaker("Thancred"));
    }

    [Fact]
    public void ChatVariant_NamesNonNpcDialogue_RegardlessOfNameNpcWithSay()
    {
        var announcer = Announcer(nameNpcWithSay: false);
        Assert.True(announcer.ShouldSaySender(isNpcDialogue: false));
        Assert.False(announcer.ShouldSaySender(isNpcDialogue: true));
    }

    [Fact]
    public void PartialName_FirstAndLast()
    {
        Assert.Equal("Mihai", Announcer(sayPartialName: true).PartialName("Mihai Testa"));
        Assert.Equal("Testa",
            Announcer(sayPartialName: true, part: FirstOrLastName.Last).PartialName("Mihai Testa"));
        Assert.Equal("Solo",
            Announcer(sayPartialName: true, part: FirstOrLastName.Last).PartialName("Solo"));
        Assert.Null(Announcer(sayPartialName: true).PartialName("  "));
        Assert.Equal("Mihai Testa", Announcer(sayPartialName: false).PartialName("Mihai Testa"));
    }

    [Fact]
    public void ComposeSaid_Port()
    {
        Assert.Equal("Yda says Hello", SpeakerAnnouncer.ComposeSaid("Yda", "Hello"));
    }

    [Fact]
    public void FromYouGate_EmptySpeakerAlwaysPasses()
    {
        var gate = new FromYouGate(() => true, () => true, () => "Mihai Testa");
        Assert.True(gate.ShouldSayFromYou(null));
        Assert.True(gate.OnlyMessagesFromYou(""));
    }

    [Fact]
    public void FromYouGate_SkipFromYou()
    {
        var gate = new FromYouGate(() => true, () => false, () => "Mihai Testa");
        Assert.False(gate.ShouldSayFromYou("Mihai Testa"));
        Assert.True(gate.ShouldSayFromYou("Yda"));
    }

    [Fact]
    public void FromYouGate_OnlyFromYou()
    {
        var gate = new FromYouGate(() => false, () => true, () => "Mihai Testa");
        Assert.False(gate.OnlyMessagesFromYou("Yda"));
        Assert.True(gate.OnlyMessagesFromYou("Mihai Testa"));
    }

    [Fact]
    public void FromYouGate_UnreadableLocalPlayer_PortBehavior()
    {
        // TTT: Contains("") matches everything, so both gates lose discriminating power.
        var gate = new FromYouGate(() => true, () => true, () => null);
        Assert.False(gate.ShouldSayFromYou("Yda"));
        Assert.True(gate.OnlyMessagesFromYou("Yda"));
    }

    [Fact]
    public void ChatChannelGate_PresetSemantics()
    {
        var enabled = new List<int> { 57 };
        var gate = new ChatChannelGate(() => enabled, () => false);
        Assert.True(gate.IsEnabled(57));
        Assert.False(gate.IsEnabled(28));

        Assert.True(new ChatChannelGate(() => null, () => true).IsEnabled(1));
        Assert.False(new ChatChannelGate(() => null, () => false).IsEnabled(1));
    }

    [Fact]
    public void AdditionalChatType_PortValues_AreStable()
    {
        Assert.Equal(2874, (int)AdditionalChatType.EnemyDefeatedByYou);
        Assert.Equal(10929, (int)AdditionalChatType.DetrimentalEffectOnEnemyEnded);
    }

    [Fact]
    public void TextEmitEvent_EquivalenceIsSpeakerAndText()
    {
        var hint = new SpeakerHint("Yda", null, null, null, null, null, null);
        var a = new TextEmitEvent(TextSource.Talk, "Yda", "Hi", "Hi", hint, 0);
        var b = new TextEmitEvent(TextSource.Chat, "Yda", "Hi", "Hi", hint, 57);
        Assert.True(TextEmitEventComparer.Instance.Equals(a, b));
        Assert.False(a.IsEquivalent(b with { Text = "Bye" }));
        Assert.False(a.IsEquivalent(null));
    }
}
