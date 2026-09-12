namespace AIVoiceActing.Tests;

using AIVoiceActing.Domain;
using AIVoiceActing.UI.State;
using Xunit;

public sealed class TestBenchModelTests
{
    [Fact]
    public void AutoEmotion_DefersToRulesPlan()
    {
        var model = new TestBenchModel { Text = "WHERE IS SHE?!", EmotionIndex = 0 };
        Assert.Equal(EmotionRules.Plan("WHERE IS SHE?!"), model.RulesPlan());
        Assert.False(model.EmotionForced);
    }

    [Fact]
    public void ForcedEmotion_BuildsPlanFromSlider()
    {
        var model = new TestBenchModel
        {
            EmotionIndex = TestBenchModel.EmotionIndexOf("sad"),
            Exaggeration = 0.7f,
        };
        Assert.True(model.EmotionForced);
        var plan = model.ForcedPlan();
        Assert.Equal("sad", plan.Emotion);
        Assert.Equal(0.7f, plan.Exaggeration);
        Assert.Empty(plan.Tags);
    }

    [Fact]
    public void ForcedEmotion_ClampsExaggeration()
    {
        var model = new TestBenchModel
        {
            EmotionIndex = 1,
            Exaggeration = 4f,
        };
        Assert.Equal(1f, model.ForcedPlan().Exaggeration);
        Assert.Equal(1f, model.BuildDirectRequest("default", bias: 0.5f).Exaggeration);
    }

    [Fact]
    public void DirectRequest_CarriesBiasAndVoice()
    {
        var model = new TestBenchModel
        {
            Text = "Hello!",
            EmotionIndex = TestBenchModel.EmotionIndexOf("excited"),
            Exaggeration = 0.75f,
        };
        var request = model.BuildDirectRequest("default", bias: 0.1f);
        Assert.Equal("default", request.ReferenceVoiceId);
        Assert.Equal("Hello!", request.Text);
        Assert.Equal(0.85f, request.Exaggeration, precision: 3);
        Assert.Empty(request.Tags);
    }

    [Fact]
    public void BuildSpeaker_MyCharacterUsesPlayerKey()
    {
        var model = new TestBenchModel { UseMyCharacter = true };
        var speaker = model.BuildSpeaker("Minfilia Warde", 66);
        Assert.Equal("pc:Minfilia Warde@66", speaker.Key);
        Assert.Equal("Minfilia Warde", speaker.DisplayName);
        Assert.Equal((ushort)66, speaker.World);
    }

    [Fact]
    public void BuildSpeaker_MyCharacterWithoutLocalPlayerFallsBack()
    {
        var model = new TestBenchModel { UseMyCharacter = true };
        var speaker = model.BuildSpeaker(null, null);
        Assert.Equal("pc:Unknown Player@0", speaker.Key);
    }

    [Fact]
    public void BuildSpeaker_RacePickerCarriesCustomizeData()
    {
        var model = new TestBenchModel
        {
            RaceIndex = Races.All.ToList().FindIndex(r => r.Name == "Roegadyn"),
            TribeIndex = 1,
            Sex = 1,
        };
        var speaker = model.BuildSpeaker(null, null);
        Assert.StartsWith("npc:test roegadyn", speaker.Key, StringComparison.Ordinal);
        Assert.Equal((byte)5, speaker.Race);
        Assert.Equal((byte)2, speaker.Tribe);
        Assert.Equal((byte)1, speaker.Sex);
        // Group resolution drives the deep/ungendered sets through the real resolver.
        Assert.Equal(
            VoiceGroup.Female,
            VoiceGroupResolver.Resolve(speaker.Race, speaker.Tribe, speaker.Sex, null, null));
    }

    [Fact]
    public void VoiceTestRequest_SpeaksTheCourtesyLine()
    {
        var request = TestBenchModel.BuildVoiceTestRequest("default", 0.6f);
        Assert.Equal(TestBenchModel.VoiceTestLine, request.Text);
        Assert.Equal("This is the voice I will use.", request.Text);
        Assert.Equal(0.6f, request.Exaggeration);
        Assert.Equal("default", request.ReferenceVoiceId);
    }

    [Fact]
    public void EmotionList_HasAutoPlusSix()
    {
        Assert.Equal(["auto", "excited", "angry", "sad", "amused", "curious", "neutral"], TestBenchModel.Emotions);
    }
}
