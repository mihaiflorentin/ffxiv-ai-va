namespace AIVoiceActing.Tests;

using AIVoiceActing.Domain;
using AIVoiceActing.Ports;
using AIVoiceActing.UI.State;
using Xunit;

public sealed class CharacterVoicesModelTests
{
    private static VoiceProfile Profile(string key, string voice) => new(
        key, voice, 0.1f, DateTimeOffset.UnixEpoch, Custom: true, 1.18f, 1.1f, 1.4f);

    [Fact]
    public void BuildView_PlayersFirst_ThenNpcByName()
    {
        var view = CharacterVoicesModel.BuildView(
        [
            Profile("npc:z: sibold", "bm_lewis"),
            Profile("pc:adam smith@66", "af_heart"),
            Profile("npc:alice", "bf_emma"),
            Profile("pc:bert jones@1177", "zf_xiaoyi"),
        ]);

        Assert.Equal(
            ["pc:adam smith@66", "pc:bert jones@1177", "npc:alice", "npc:z: sibold"],
            view.Select(p => p.SpeakerKey).ToArray());
    }

    [Fact]
    public void ShareText_ParseShare_RoundTripsThroughAssignmentShare()
    {
        var entries = new[]
        {
            Profile("npc:feo ul", "bm_fable"),
            Profile("pc:mu hlack@1177", "zf_xiaoyi"),
        };

        var decoded = CharacterVoicesModel.ParseShare(CharacterVoicesModel.ShareText(entries));

        Assert.Equal(2, decoded.Count);
        Assert.Equal(entries[0].SpeakerKey, decoded[0].SpeakerKey);
        Assert.Equal(entries[0].ReferenceVoiceId, decoded[0].ReferenceVoiceId);
        Assert.Equal(entries[0].ExaggerationBias, decoded[0].ExaggerationBias);
        Assert.Equal((entries[0].Pitch, entries[0].Speed, entries[0].Volume), (decoded[0].Pitch, decoded[0].Speed, decoded[0].Volume));
        Assert.Equal(entries[1].SpeakerKey, decoded[1].SpeakerKey);
    }
}
