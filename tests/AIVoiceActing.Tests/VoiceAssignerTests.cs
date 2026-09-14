namespace AIVoiceActing.Tests;

using AIVoiceActing.Domain;
using AIVoiceActing.Ports;
using Xunit;

public sealed class VoiceAssignerTests
{
    private static readonly VoiceSlot[] Slots =
    [
        new("alpha", 0.05f, 0.9f, 1.1f, 1.2f),
        new("beta", 0.2f),
        new("gamma", 0f, 1.18f, 1.1f, 1f),
    ];

    [Fact]
    public void SeededRng_IsDeterministic()
    {
        var first = VoiceAssigner.AssignSlot(Slots, new Random(7));
        for (var i = 0; i < 100; i++)
        {
            var again = VoiceAssigner.AssignSlot(Slots, new Random(7));
            Assert.Equal(first.ReferenceVoiceId, again.ReferenceVoiceId);
        }
    }

    [Fact]
    public void Pick_IsAlwaysOneOfTheCandidates()
    {
        var ids = Slots.Select(slot => slot.Id).ToHashSet();
        for (var seed = 0; seed < 50; seed++)
        {
            Assert.Contains(VoiceAssigner.AssignSlot(Slots, new Random(seed)).ReferenceVoiceId, ids);
        }
    }

    [Fact]
    public void DifferentSeeds_CanPickDifferentSlots()
    {
        var picks = new HashSet<string>();
        for (var seed = 0; seed < 200; seed++)
        {
            picks.Add(VoiceAssigner.AssignSlot(Slots, new Random(seed)).ReferenceVoiceId);
        }

        Assert.True(picks.Count > 1, "every seed collapsed onto one candidate");
    }

    [Fact]
    public void SlotKnobs_AreCarriedOntoTheProfile()
    {
        var slot = Slots[0];
        var profile = VoiceAssigner.AssignSlot([slot], new Random(0));

        Assert.Equal(slot.Id, profile.ReferenceVoiceId);
        Assert.Equal(Math.Clamp(slot.ExaggerationBias, 0f, 1f), profile.ExaggerationBias);
        Assert.Equal(slot.Pitch, profile.Pitch);
        Assert.Equal(slot.Speed, profile.Speed);
        Assert.Equal(Math.Clamp(slot.Volume, 0f, 2f), profile.Volume);
        Assert.False(profile.Custom);
    }

    [Fact]
    public void NullRng_StillReturnsACandidate()
    {
        var ids = Slots.Select(slot => slot.Id).ToHashSet();
        for (var i = 0; i < 20; i++)
        {
            Assert.Contains(VoiceAssigner.AssignSlot(Slots).ReferenceVoiceId, ids);
        }
    }

    [Fact]
    public void ReturnedProfile_HasEmptyKey_ForCallerStamping()
    {
        var profile = VoiceAssigner.AssignSlot([Slots[1]], new Random(0));
        Assert.Equal(string.Empty, profile.SpeakerKey);
    }

    [Fact]
    public void SlotBias_ClampsToUnitRange()
    {
        var profile = VoiceAssigner.AssignSlot([new VoiceSlot("default", 7f)], new Random(0));
        Assert.Equal(1f, profile.ExaggerationBias);
    }

    [Fact]
    public void EmptyOrNullCandidates_Throw()
    {
        Assert.Throws<ArgumentException>(() => VoiceAssigner.AssignSlot([]));
        Assert.Throws<ArgumentException>(() => VoiceAssigner.AssignSlot(null!));
    }
}
