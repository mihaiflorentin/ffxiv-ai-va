namespace AIVoiceActing.Tests.Pipeline;

using AIVoiceActing.Domain.Pipeline;
using Xunit;

public sealed class SessionIdDeriverTests
{
    [Fact]
    public void CutsceneActive_YieldsSharedCutsceneSession()
    {
        Assert.Equal("cutscene", SessionIdDeriver.Derive(cutsceneActive: true, talkVisible: true, "npc:yda"));
        Assert.Equal("cutscene", SessionIdDeriver.Derive(cutsceneActive: true, talkVisible: false, "npc:yda"));
    }

    [Fact]
    public void TalkVisible_ScopesBySpeakerKey()
    {
        Assert.Equal("talk:npc:merlwyb", SessionIdDeriver.Derive(false, true, "npc:merlwyb"));
        Assert.Equal("talk:pc:Mihai Testa@66", SessionIdDeriver.Derive(false, true, "pc:Mihai Testa@66"));
    }

    [Fact]
    public void NeitherActive_YieldsNoSession()
    {
        Assert.Null(SessionIdDeriver.Derive(false, false, "npc:thancred"));
    }
}
