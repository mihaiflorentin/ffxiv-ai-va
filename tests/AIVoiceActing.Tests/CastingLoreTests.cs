namespace AIVoiceActing.Tests;

using System.Text.Json;
using AIVoiceActing.Container;
using AIVoiceActing.Domain;
using AIVoiceActing.Infrastructure.Storage;
using AIVoiceActing.Ports;
using AIVoiceActing.Tests.Mock;
using System.Text.RegularExpressions;
using Xunit;

/// <summary>
/// The shipped casting (<c>voices.json</c>, documented row by row in docs/casting-lore.md): English
/// only, no American accent anywhere, voices of the right sex, and every race, clan group and
/// beast tribe cast. Accent and sex come from the voice library (stock manifest plus the bundled
/// packs), never from the phonemizer: the Isles pack speaks through the American phonemizer but is
/// Irish, Welsh and so on.
/// </summary>
public sealed class CastingLoreTests
{
    private static readonly FileVoiceLibrary Library = new(() => [PacksRoot()]);

    private static string PacksRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "src", "AIVoiceActing", "voicepacks");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new DirectoryNotFoundException("src/AIVoiceActing/voicepacks not found above the test output.");
    }

    private static RaceVoiceMap Map => RaceVoiceMapTests.LoadDefault();

    /// <summary>Every voice id the built-in casting uses: active sets, race variants, and the tribe lists.</summary>
    private static IEnumerable<(string Where, VoiceSlot[] Slots)> CastingLists()
    {
        var map = Map;
        foreach (var (key, slots) in map.Sets)
        {
            yield return ($"set {key}", slots);
        }

        foreach (var tribe in CastingDefaults.Tribes)
        {
            foreach (var suffix in new[] { string.Empty, "Male", "Female" })
            {
                if (map.Disabled!.Sets.TryGetValue(tribe.ManifestSetKey + suffix, out var slots))
                {
                    yield return ($"tribe {tribe.Key}{suffix}", slots);
                }
            }
        }
    }

    private static VoiceInfo Voice(string where, string id) =>
        Library.Find(id) ?? throw new Xunit.Sdk.XunitException($"{where}: voice \"{id}\" is not in the voice library.");

    private static string Phonemizer(VoiceInfo voice) =>
        Phonemizers.Normalize(voice.Realizations.First(r => r.Kind == RealizationKind.StyleTensor).Phonemizer)!;

    /// <summary>
    /// American voices the owner chose by ear for a set, in spite of the rule (docs/casting-lore.md).
    /// Generated blends still never mix them in (<see cref="VoiceBlend.IsAmerican"/>).
    /// </summary>
    private static readonly HashSet<(string Set, string Id)> OwnerPicks = [("set lalafellFemale", "af_nova")];

    [Fact]
    public void NoAmericanAccentVoice_AnywhereInVoicesJson()
    {
        // Textual guard over the whole file (parked sets included): no stock af_/am_ id at all, apart
        // from the owner's hand-picked exceptions.
        var text = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "voices.json"));
        Assert.Equal(OwnerPicks.Count, Regex.Matches(text, "\"id\":\\s*\"a[fm]_").Count);

        // Semantic guard over everything the casting references: accent en-US is American, the
        // phonemizer is irrelevant (Isles voices use en-us G2P and are not American).
        foreach (var (where, slots) in CastingLists())
        {
            foreach (var slot in slots.Where(s => !OwnerPicks.Contains((where, s.Id))))
            {
                var voice = Voice(where, slot.Id);
                Assert.False(VoiceBlend.IsAmerican(voice), $"{where}: {slot.Id} is American");
                Assert.NotEqual("en-US", voice.Accent);
                Assert.DoesNotMatch("^a[fm]_", slot.Id);
            }
        }
    }

    [Fact]
    public void EveryVoice_SpeaksEnglishThroughAnEnglishPhonemizer()
    {
        foreach (var (where, slots) in CastingLists())
        {
            foreach (var slot in slots)
            {
                var voice = Voice(where, slot.Id);
                Assert.True(VoiceBlend.IsEnglishPhonemizer(Phonemizer(voice)), $"{where}: {slot.Id} uses a non-English phonemizer");
                // Stock ids: only the British bank (b*); non-English stock banks (e/f/h/i/j/p/z) never.
                if (!slot.Id.Contains('.') && !OwnerPicks.Contains((where, slot.Id)))
                {
                    Assert.StartsWith("b", slot.Id, StringComparison.Ordinal);
                }
            }
        }
    }

    [Fact]
    public void RaceAndTribeRows_GetVoicesOfTheirSex()
    {
        var map = Map;
        foreach (var (variant, setKey) in map.RaceVariants)
        {
            var sex = variant.EndsWith("|Male", StringComparison.Ordinal) ? "male" : "female";
            AssertSex(variant, map.Sets[setKey], sex);
        }

        AssertSex("male", map.Sets["male"], "male");
        AssertSex("female", map.Sets["female"], "female");
        foreach (var tribe in CastingDefaults.Tribes)
        {
            if (map.Disabled!.Sets.TryGetValue(tribe.ManifestSetKey + "Male", out var male) && tribe.Key != "mamooljafemale")
            {
                AssertSex(tribe.Key + " male", male, "male");
            }

            if (map.Disabled.Sets.TryGetValue(tribe.ManifestSetKey + "Female", out var female))
            {
                AssertSex(tribe.Key + " female", female, "female");
            }
        }

        // Pairs of #10 sets: the Male one is male and the Female one is female.
        foreach (var (key, slots) in map.Sets.Where(p => p.Key.EndsWith("Male", StringComparison.Ordinal)))
        {
            AssertSex(key, slots, "male");
        }

        foreach (var (key, slots) in map.Sets.Where(p => p.Key.EndsWith("Female", StringComparison.Ordinal)))
        {
            AssertSex(key, slots, "female");
        }
    }

    private static void AssertSex(string where, IEnumerable<VoiceSlot> slots, string sex) =>
        Assert.All(slots, slot => Assert.Equal(sex, Voice(where, slot.Id).Sex));

    [Fact]
    public void EveryPlayableRaceAndSex_HasItsOwnCast_WithSeveralVoicesToSpreadAcross()
    {
        var map = Map;
        for (byte race = 1; race <= 8; race++)
        {
            foreach (var group in new[] { VoiceGroup.Male, VoiceGroup.Female })
            {
                Assert.True(map.RaceVariants.ContainsKey($"{race}|{group}"), $"{race}|{group}");
                var slots = map.SlotsFor(group, race);
                Assert.True(slots.Length >= 3, $"{race}|{group} has only {slots.Length} voice(s)");
            }
        }
    }

    [Fact]
    public void EveryBeastTribe_IsCast_AndSplitsByGender()
    {
        var map = Map;
        foreach (var tribe in CastingDefaults.Tribes)
        {
            Assert.True(map.Disabled!.Sets.TryGetValue(tribe.ManifestSetKey, out var pool) && pool.Length > 0, tribe.Key);
            if (tribe.Key == "mamooljafemale")
            {
                continue; // the female Mamool Ja model: one female pool
            }

            Assert.True(map.Disabled.Sets.ContainsKey(tribe.ManifestSetKey + "Male"), tribe.Key + " male list");
            Assert.True(map.Disabled.Sets.ContainsKey(tribe.ManifestSetKey + "Female")
                || tribe.Key == "mamoolja", tribe.Key + " female list");
        }

        // The Default preset exposes the lists on the tribe rows.
        var preset = new JsonCastingPresetStore(
            Path.Combine(Path.GetTempPath(), $"aiva-lore-{Guid.NewGuid():N}.json"), () => map, new FakeLogSink()).GetDefault();
        var goblin = preset.BeastTribes.Single(t => t.Key == "goblin");
        Assert.NotEmpty(goblin.MaleVoices);
        Assert.NotEmpty(goblin.FemaleVoices);
        Assert.Equal(goblin.MaleVoices.Count + goblin.FemaleVoices.Count, goblin.Voices.Count);
    }

    // ---- lore sentinels ----

    private static string[] AccentsOf(string setKey) =>
        [.. Map.Sets[setKey].Select(slot => Voice(setKey, slot.Id).Accent)];

    private static string[] TribeAccents(string tribeKey, string suffix = "") =>
        [.. Map.Disabled!.Sets[CastingDefaults.Tribes.Single(t => t.Key == tribeKey).ManifestSetKey + suffix]
            .Select(slot => Voice(tribeKey, slot.Id).Accent)];

    [Fact]
    public void Elezen_AreFormalBritish_StockRp()
    {
        foreach (var set in new[] { "elezenMale", "elezenFemale" })
        {
            Assert.All(AccentsOf(set), accent => Assert.Equal("en-GB", accent));
        }
    }

    [Fact]
    public void Hyur_MixMidlandersHighlandersAndDoman()
    {
        var male = AccentsOf("hyurMale");
        Assert.Contains("en-GB-SCT", male); // Ala Mhigan Highlanders
        Assert.Contains("en-GB-NTH", male);
        Assert.Contains("en-x-ja", male); // Doman and Hingan Hyur
        Assert.Contains("en-x-ja", AccentsOf("hyurFemale"));
    }

    [Fact]
    public void AuRa_ArePacificRimAndSteppeAccented()
    {
        foreach (var set in new[] { "auRaMale", "auRaFemale" })
        {
            var accents = AccentsOf(set);
            Assert.Contains("en-x-ja", accents); // Raen
            Assert.Contains("en-x-zh", accents); // Xaela steppe tribes
            Assert.Contains("en-x-hi", accents);
        }
    }

    [Fact]
    public void Lalafell_AreLighter_HigherAndBrisker()
    {
        foreach (var set in new[] { "lalafellMale", "lalafellFemale" })
        {
            Assert.All(Map.Sets[set], slot =>
            {
                Assert.True(slot.Pitch >= 1.1f, set);
                Assert.True(slot.Speed >= 1.05f, set);
            });
        }
    }

    [Fact]
    public void Roegadyn_AndHrothgar_AreDeeper_AndHrothgarDeepest()
    {
        foreach (var set in new[] { "roegadynMale", "roegadynFemale", "hrothgarMale", "hrothgarFemale" })
        {
            Assert.All(Map.Sets[set], slot => Assert.True(slot.Pitch < 1f, set));
        }

        Assert.True(Map.Sets["hrothgarMale"].Average(s => s.Pitch) < Map.Sets["roegadynMale"].Average(s => s.Pitch));
        Assert.Contains("en-GB-NTH", AccentsOf("roegadynMale")); // Sea Wolves: Northern for the Nordic flavour
        Assert.True(Map.Sets["roegadynMale"].Any(s => s.Pitch <= 0.88f)); // Hellsguard: gruffer
    }

    [Fact]
    public void Miqote_SplitSeekersFromKeepers()
    {
        var accents = AccentsOf("miqoteMale");
        Assert.Contains("en-GB-STH", accents); // Seekers
        Assert.Contains("en-GB-WLS", accents); // Keepers
    }

    [Fact]
    public void Viera_AreOtherworldly_NotPlainBritish()
    {
        Assert.Contains("en-x-fr", AccentsOf("vieraFemale"));
        Assert.Contains("en-GB-WLS", AccentsOf("vieraFemale"));
    }

    [Fact]
    public void BeastTribes_FollowTheirLore()
    {
        Assert.All(TribeAccents("kojin"), a => Assert.Equal("en-x-ja", a));
        Assert.All(TribeAccents("namazu"), a => Assert.Equal("en-x-ja", a));
        Assert.All(TribeAccents("ananta"), a => Assert.Equal("en-x-hi", a));
        Assert.All(TribeAccents("garlean", "Male"), a => Assert.True(a is "en-x-it" or "en-GB"));
        Assert.Contains("en-IE", TribeAccents("pixie", "Male"));
        Assert.All(TribeAccents("dwarf"), a => Assert.True(a is "en-GB-SCT" or "en-GB-WLS"));
        Assert.All(TribeAccents("kobold"), a => Assert.True(a is "en-GB-WLS" or "en-GB-NTH"));
        // The small folk are pitched up, the big ones down.
        Assert.All(Map.Disabled!.Sets["setMoogle"], slot => Assert.True(slot.Pitch >= 1.2f));
        Assert.All(Map.Disabled.Sets["setLoporrit"], slot => Assert.True(slot.Pitch >= 1.15f));
        Assert.All(Map.Disabled.Sets["setDragon"], slot => Assert.True(slot.Pitch <= 0.86f));
    }

    // ---- the container: Hrothgar women, and users keeping what they chose ----

    private static ServiceContainer NewContainer(string root) => new(
        logSinkOverride: new FakeLogSink(),
        profileStorePathFactory: () => Path.Combine(root, "voice-assignments.json"),
        modelsDirFactory: () => Path.Combine(root, "models"),
        voicesManifestFactory: () => Path.Combine(AppContext.BaseDirectory, "voices.json"),
        speechQueueFactory: () => new FakeSpeechQueue(),
        voicePackRootsFactory: () => [PacksRoot()]);

    private static string NewRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "aiva-lore", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    [Fact]
    public void HrothgarWomen_TakeTheHrothgarFemaleCast_NotTheMixedUnknownRow()
    {
        var root = NewRoot();
        try
        {
            using var container = NewContainer(root);
            var female = new HashSet<string>(Map.Sets["hrothgarFemale"].Select(s => s.Id));

            for (var i = 0; i < 20; i++)
            {
                var profile = container.ResolveProfile(new SpeakerIdentity($"npc:hroth {i}", $"Hroth {i}", Race: 7, Tribe: 1, Sex: 1, World: null));
                Assert.Contains(profile.ReferenceVoiceId, female);
                Assert.True(profile.Pitch < 1f);
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void HandPickedVoices_SurviveTheNewCasting_AutomaticOldPicksAreRecast()
    {
        var root = NewRoot();
        try
        {
            using var container = NewContainer(root);
            var store = container.ProfileStore;
            // A hand-picked voice outside the built-in casting (even an American one) is the user's choice.
            store.SetOverride("npc:mine", "am_michael", 0.4f);
            // An automatic pick from the old casting (a Hindi-bank voice) is recast from the new one.
            store.GetOrCreate("npc:old", () => [new VoiceSlot("hm_omega")], 1, 1, 0);

            var mine = container.ResolveProfile(new SpeakerIdentity("npc:mine", "Mine", Race: 1, Tribe: 1, Sex: 0, World: null));
            var old = container.ResolveProfile(new SpeakerIdentity("npc:old", "Old", Race: 1, Tribe: 1, Sex: 0, World: null));

            Assert.Equal("am_michael", mine.ReferenceVoiceId);
            Assert.True(mine.Custom);
            Assert.NotEqual("hm_omega", old.ReferenceVoiceId);
            Assert.Contains(old.ReferenceVoiceId, Map.Sets["hyurMale"].Select(s => s.Id));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void SavedPresets_AreNotTouchedByTheBuiltInCasting()
    {
        // A user's own preset keeps its American voices; only the derived Default preset changed.
        var path = Path.Combine(Path.GetTempPath(), $"aiva-lore-{Guid.NewGuid():N}.json");
        try
        {
            var store = new JsonCastingPresetStore(path, () => Map, new FakeLogSink());
            var mine = new CastingPreset(
                "Mine",
                new Dictionary<string, VoiceSlotDto[]> { ["1|Male"] = [new VoiceSlotDto("am_michael")] },
                []);
            store.Save(mine);

            var reloaded = new JsonCastingPresetStore(path, () => Map, new FakeLogSink());

            Assert.Equal("am_michael", reloaded.Get("Mine").Buckets["1|Male"].Single().Id);
            Assert.DoesNotContain(reloaded.GetDefault().Buckets.Values.SelectMany(b => b), slot => slot.Id == "am_michael");
        }
        finally
        {
            File.Delete(path);
        }
    }
}
