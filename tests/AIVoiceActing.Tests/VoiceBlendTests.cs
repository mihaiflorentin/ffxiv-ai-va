namespace AIVoiceActing.Tests;

using AIVoiceActing.Domain;
using AIVoiceActing.Ports;
using Xunit;

public sealed class VoiceBlendTests
{
    private static BlendCandidate Us(string id, string sex = "female") => new(id, Phonemizers.EnglishUs, sex);

    private static BlendCandidate Gb(string id, string sex = "female") => new(id, Phonemizers.EnglishGb, sex);

    private static readonly BlendCandidate[] FemaleUs = [Us("af_a"), Us("af_b"), Us("af_c"), Us("af_d")];

    [Fact]
    public void Plan_IsDeterministic_ForTheSameKeySaltAndSet()
    {
        var first = VoiceBlend.Plan("pc:Ada Lovelace@42", 0, FemaleUs, [], "female")!;
        var second = VoiceBlend.Plan("pc:Ada Lovelace@42", 0, [.. FemaleUs.Reverse()], [], "female")!;

        Assert.Equal(first.VoiceId, second.VoiceId);
        Assert.Equal(first.Parts, second.Parts, (a, b) => a.VoiceId == b.VoiceId && a.Weight == b.Weight);
    }

    [Fact]
    public void Plan_DiffersBetweenSpeakersAndSalts()
    {
        var ids = Enumerable.Range(0, 20)
            .Select(i => VoiceBlend.Plan($"npc:person {i}", 0, FemaleUs, [], "female")!.VoiceId)
            .ToHashSet();
        Assert.Equal(20, ids.Count);

        var salted = VoiceBlend.Plan("npc:person 0", 1, FemaleUs, [], "female")!;
        Assert.NotEqual(VoiceBlend.Plan("npc:person 0", 0, FemaleUs, [], "female")!.VoiceId, salted.VoiceId);
        Assert.Equal(1, salted.Salt);
    }

    [Fact]
    public void Plan_WeightsAreNormalisedAndNoVoiceDominates()
    {
        for (var i = 0; i < 200; i++)
        {
            var plan = VoiceBlend.Plan($"pc:Person {i}@1", 0, FemaleUs, [], null)!;
            Assert.InRange(plan.Parts.Count, 2, 3);
            Assert.Equal(1.0, plan.Parts.Sum(p => (double)p.Weight), 4);
            Assert.All(plan.Parts, p => Assert.InRange(p.Weight, 0.16f, 0.72f));
            Assert.Equal(plan.Parts.Count, plan.Parts.Select(p => p.VoiceId).Distinct().Count());
        }
    }

    [Fact]
    public void Plan_UsesTwoVoicesWhenOnlyTwoExist_AndOnlyBothOfThem()
    {
        var plan = VoiceBlend.Plan("npc:x", 0, [Us("af_a"), Us("af_b")], [], null)!;
        Assert.Equal(["af_a", "af_b"], plan.Parts.Select(p => p.VoiceId).Order());
    }

    [Fact]
    public void Plan_NeverMixesPhonemizersOrSexes()
    {
        BlendCandidate[] cast =
        [
            Us("af_a"), Us("af_b"), Gb("bf_a"), Gb("bf_b"), Gb("bf_c"), Us("am_a", "male"), Us("am_b", "male"),
        ];
        for (var i = 0; i < 100; i++)
        {
            var plan = VoiceBlend.Plan($"npc:{i}", 0, cast, [], "female")!;
            var used = plan.Parts.Select(p => cast.Single(c => c.Id == p.VoiceId)).ToList();
            Assert.Single(used.Select(c => c.Phonemizer).Distinct());
            Assert.Single(used.Select(c => c.Sex).Distinct());
            Assert.Equal("female", plan.Sex);
            Assert.Equal(used[0].Phonemizer, plan.Phonemizer);
        }
    }

    [Fact]
    public void Plan_PrefersTheRequestedSex()
    {
        BlendCandidate[] cast = [Us("af_a"), Us("af_b"), Us("af_c"), Us("am_a", "male"), Us("am_b", "male")];
        Assert.Equal("male", VoiceBlend.Plan("npc:x", 0, cast, [], "male")!.Sex);
        Assert.Equal("female", VoiceBlend.Plan("npc:x", 0, cast, [], "female")!.Sex);
    }

    [Fact]
    public void Plan_WidensAThinCastWithMatchingEnglishVoices()
    {
        BlendCandidate[] cast = [Us("af_a")];
        BlendCandidate[] widen = [Us("af_a"), Us("af_z"), Us("am_z", "male"), Gb("bf_z")];

        var plan = VoiceBlend.Plan("npc:x", 0, cast, widen, "female")!;

        Assert.Equal(["af_a", "af_z"], plan.Parts.Select(p => p.VoiceId).Order());
    }

    [Fact]
    public void Plan_IsNullWhenNoTwoVoicesMatch()
    {
        Assert.Null(VoiceBlend.Plan("npc:x", 0, [], [Us("af_a"), Us("af_b")], null));
        Assert.Null(VoiceBlend.Plan("npc:x", 0, [Us("af_a")], [Us("af_a"), Gb("bf_a")], null));
    }

    [Fact]
    public void GeneratedIds_HaveTheShapeKokoroNeeds()
    {
        var plan = VoiceBlend.Plan("npc:x", 0, [Gb("bm_a", "male"), Gb("bm_b", "male")], [], null)!;

        Assert.StartsWith("bm_gen_", plan.VoiceId);
        Assert.Equal('_', plan.VoiceId[2]);
        Assert.Equal(12, plan.VoiceId.Length - "bm_gen_".Length);
        Assert.True(VoiceBlend.IsGeneratedId(plan.VoiceId));
        Assert.True(VoiceBlend.IsGeneratedId("af_gen_0123456789ab"));
        Assert.False(VoiceBlend.IsGeneratedId("af_heart"));
        Assert.False(VoiceBlend.IsGeneratedId("zf_gen_0123456789ab"));
        Assert.False(VoiceBlend.IsGeneratedId(null));
        Assert.Equal(plan.VoiceId, Phonemizers.InternalName(plan.VoiceId, Phonemizers.EnglishGb, "male"));
    }

    [Fact]
    public void Hash_IsStableAcrossRuns()
    {
        // Pinned: a change here would silently re-voice every character.
        Assert.Equal("f8ec6ab4144b", VoiceBlend.Hash("npc:feo ul", 0));
    }

    [Fact]
    public void Mix_IsAWeightedAverage_AndRenormalisesWeights()
    {
        var a = new float[] { 1f, 2f, 3f };
        var b = new float[] { 3f, 2f, 1f };

        Assert.Equal([2f, 2f, 2f], VoiceBlend.Mix([a, b], [0.5f, 0.5f]));
        Assert.Equal([1.5f, 2f, 2.5f], VoiceBlend.Mix([a, b], [3f, 1f]));
        Assert.Equal(a, VoiceBlend.Mix([a], [0.4f]));
        Assert.Throws<ArgumentException>(() => VoiceBlend.Mix([a, b], [1f]));
        Assert.Throws<ArgumentException>(() => VoiceBlend.Mix([a, new float[2]], [1f, 1f]));
    }

    [Fact]
    public void CandidateFor_KeepsEnglishPhonemizersOnly()
    {
        Assert.Equal(Phonemizers.EnglishGb, VoiceBlend.CandidateFor(Voice("bf_x", "en-GB", "female", Phonemizers.EnglishGb))!.Phonemizer);
        Assert.Equal("male", VoiceBlend.CandidateFor(Voice("isles.x", "en-IE", "male", Phonemizers.EnglishUs))!.Sex);
        Assert.Null(VoiceBlend.CandidateFor(Voice("ff_x", "fr-FR", "female", Phonemizers.French)));
        Assert.Null(VoiceBlend.CandidateFor(Voice("zf_x", "zh-CN", "female", Phonemizers.Chinese)));
        Assert.Null(VoiceBlend.CandidateFor(Voice("af_x", "en-US", "female", Phonemizers.EnglishUs, available: false)));
    }

    [Theory]
    [InlineData("af_heart", "en-US", "female")]
    [InlineData("am_michael", "en-US", "male")]
    [InlineData("af_other", "en-GB", "female")] // a stock American id, whatever its metadata says
    [InlineData("pack.texan", "en-US", "male")] // a third-party pack voice filed under American
    public void CandidateFor_NeverOffersAnAmericanVoice(string id, string accent, string sex)
    {
        var voice = Voice(id, accent, sex, Phonemizers.EnglishUs);

        Assert.True(VoiceBlend.IsAmerican(voice));
        Assert.Null(VoiceBlend.CandidateFor(voice));
        Assert.Null(VoiceBlend.WidenCandidateFor(voice));
    }

    [Theory]
    [InlineData("isles.ie_m_01", "en-IE")]
    [InlineData("isles.sc_m_01", "en-GB-SCT")]
    [InlineData("isles.wl_m_01", "en-GB-WLS")]
    [InlineData("isles.nth_m_01", "en-GB-NTH")]
    [InlineData("bm_george", "en-GB")]
    public void CandidateFor_JudgesTheAccentNotThePhonemizer_SoIslesVoicesPassOnTheAmericanG2p(string id, string accent)
    {
        var voice = Voice(id, accent, "male", Phonemizers.EnglishUs);

        Assert.False(VoiceBlend.IsAmerican(voice));
        Assert.NotNull(VoiceBlend.CandidateFor(voice));
    }

    [Fact]
    public void CandidateFor_SkipsGeneratedVoices_SoABlendNeverHidesAnAmericanAncestor()
    {
        var generated = Voice("bf_gen_0123456789ab", "en-GB", "female", Phonemizers.EnglishGb) with { PackId = VoiceBlend.GeneratedPackId };

        Assert.Null(VoiceBlend.CandidateFor(generated));
    }

    [Fact]
    public void WidenCandidateFor_KeepsBritishAndRegionalVoices_ButNotForeignAccentedOnes()
    {
        Assert.NotNull(VoiceBlend.WidenCandidateFor(Voice("bf_x", "en-GB", "female", Phonemizers.EnglishGb)));
        Assert.NotNull(VoiceBlend.WidenCandidateFor(Voice("isles.sc_f_01", "en-GB-SCT", "female", Phonemizers.EnglishUs)));
        var japanese = Voice("accented.ja_f", "en-x-ja", "female", Phonemizers.EnglishUs);
        Assert.NotNull(VoiceBlend.CandidateFor(japanese)); // fine when the casting itself names it
        Assert.Null(VoiceBlend.WidenCandidateFor(japanese));
    }

    [Fact]
    public void CandidateFor_CountsAForeignAccentWithAnEnglishPhonemizerAsEnglish()
    {
        var candidate = VoiceBlend.CandidateFor(Voice("pack.fr_f", "fr-FR", "female", Phonemizers.EnglishUs));

        Assert.NotNull(candidate);
        Assert.Equal(Phonemizers.EnglishUs, candidate!.Phonemizer);
    }

    [Fact]
    public void CandidateFor_IgnoresClipOnlyVoices()
    {
        var clipOnly = Voice("clip", "en-US", "female", Phonemizers.EnglishUs) with
        {
            Realizations = [new VoiceRealization(RealizationKind.ReferenceClip, "kokoro", "x.wav", null, null)],
        };
        Assert.Null(VoiceBlend.CandidateFor(clipOnly));
    }

    private static VoiceInfo Voice(string id, string accent, string sex, string phonemizer, bool available = true) =>
        new(
            id, id, accent, sex, "adult", string.Empty, "MIT", null, [], VoiceQuality.Stock, null, "pack",
            [new VoiceRealization(RealizationKind.StyleTensor, "kokoro", string.Empty, phonemizer, null)],
            available);
}
