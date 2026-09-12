namespace AIVoiceActing.Tests;

using AIVoiceActing.Domain;
using AIVoiceActing.Ports;
using Xunit;

public sealed class VoiceAssignerTests
{
    private static readonly string[] Candidates = ["alpha", "beta", "gamma"];

    [Fact]
    public void SameKey_YieldsSameVoice_Across100Iterations()
    {
        IReadOnlyDictionary<string, VoiceProfile> existing = new Dictionary<string, VoiceProfile>();
        var first = VoiceAssigner.Assign("pc:Mihai Testa@66", Candidates, existing);
        for (var i = 0; i < 100; i++)
        {
            var again = VoiceAssigner.Assign("pc:Mihai Testa@66", Candidates, existing);
            Assert.Equal(first.ReferenceVoiceId, again.ReferenceVoiceId);
            Assert.Equal(VoiceAssigner.AssignIndex("pc:Mihai Testa@66", Candidates.Length),
                VoiceAssigner.AssignIndex("pc:Mihai Testa@66", Candidates.Length));
        }
    }

    [Fact]
    public void Assignment_IsIndependentOfProcessInstance_Ordering()
    {
        // Hash over the key directly — list order changes mapping, same key never flips within one list.
        var direct = VoiceAssigner.AssignIndex("npc:feo ul", 5);
        Assert.InRange(direct, 0, 4);
        Assert.Equal(direct, VoiceAssigner.AssignIndex("npc:feo ul", 5));
    }

    [Fact]
    public void DifferentKeys_DistributeAcrossCandidates()
    {
        var picks = new HashSet<string>();
        for (var i = 0; i < 200; i++)
        {
            var key = $"npc:npc-{i}";
            picks.Add(Candidates[VoiceAssigner.AssignIndex(key, Candidates.Length)]);
        }

        Assert.True(picks.Count > 1, "all 200 keys collapsed onto one candidate");
    }

    [Fact]
    public void ExistingProfile_IsReturnedUnchanged_NeverReassigned()
    {
        var preserved = new VoiceProfile("pc:Old Key@66", "omega", 0.42f, DateTimeOffset.UtcNow, Custom: false);
        var existing = new Dictionary<string, VoiceProfile> { ["pc:Old Key@66"] = preserved };

        var result = VoiceAssigner.Assign("pc:Old Key@66", ["alpha", "beta", "gamma"], existing);

        Assert.Same(preserved, result);
    }

    [Fact]
    public void AssignSlot_CarriesSlotBias_Deterministically()
    {
        var slots = new[] { new VoiceSlot("default", 0.05f), new VoiceSlot("default", 0.2f) };
        var existing = new Dictionary<string, VoiceProfile>();
        var profile = VoiceAssigner.AssignSlot("npc:feo ul", slots, existing);
        var expectedSlot = slots[VoiceAssigner.AssignIndex("npc:feo ul", slots.Length)];

        Assert.Equal(expectedSlot.Id, profile.ReferenceVoiceId);
        Assert.Equal(expectedSlot.ExaggerationBias, profile.ExaggerationBias);
        Assert.False(profile.Custom);
    }

    [Fact]
    public void EmptyCandidates_Throw()
    {
        var existing = new Dictionary<string, VoiceProfile>();
        Assert.Throws<ArgumentException>(() => VoiceAssigner.Assign("npc:x", [], existing));
        Assert.Throws<ArgumentException>(() => VoiceAssigner.AssignSlot("npc:x", [], existing));
    }

    [Fact]
    public void AssignIndex_RejectsNonPositiveCounts()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => VoiceAssigner.AssignIndex("npc:x", 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => VoiceAssigner.AssignIndex("npc:x", -1));
    }
}
