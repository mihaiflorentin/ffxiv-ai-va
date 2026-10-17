namespace AIVoiceActing.Tests;

using AIVoiceActing.Domain;
using AIVoiceActing.Ports;
using AIVoiceActing.Tests.Mock;
using Xunit;

public sealed class CoreVoiceManifestTests
{
    private static readonly (VoicePackInfo Pack, IReadOnlyList<VoiceInfo> Voices) Core = CoreVoiceManifest.Load();

    [Fact]
    public void EveryVoiceAgreesWithTheLegacyPrefixRules()
    {
        Assert.NotEmpty(Core.Voices);
        foreach (var voice in Core.Voices)
        {
            var legacy = VoiceCatalog.Describe(voice.Id);
            Assert.NotEqual(VoiceLanguage.Unknown, legacy.Language);
            Assert.Equal(Accents.ForKokoroLanguage(legacy.Language), voice.Accent);
            Assert.Equal(legacy.Sex == VoiceSex.Male ? "male" : "female", voice.Sex);
            Assert.Equal(VoiceQuality.Stock, voice.Quality);
            Assert.True(voice.Available);
            Assert.False(string.IsNullOrWhiteSpace(voice.DisplayName));
            Assert.False(string.IsNullOrWhiteSpace(voice.Description));
            Assert.True(Accents.IsKnown(voice.Accent));
        }
    }

    [Fact]
    public void IdsAreUnique_AndKeepTheBankNames()
    {
        var ids = Core.Voices.Select(v => v.Id).ToList();
        Assert.Equal(ids.Count, ids.Distinct().Count());
        Assert.All(ids, id => Assert.Matches("^[a-z]{2}_[a-z]+$", id));
        Assert.Contains("af_heart", ids);
        Assert.DoesNotContain("am_irishtest", ids);
    }

    [Fact]
    public void ShippedCasting_OnlyUsesVoicesTheCoreManifestDescribes()
    {
        var map = RaceVoiceMapTests.LoadDefault();
        var ids = Core.Voices.Select(v => v.Id).ToHashSet();
        // Pack voices ("isles.ie_m_01") are checked against their manifest by BundledIslesPackTests.
        Assert.All(map.DistinctVoiceIds().Where(id => !id.Contains('.')), id => Assert.Contains(id, ids));
    }

    [Fact]
    public void StockRealizations_AreByNameWithThePrefixPhonemizer()
    {
        var british = Core.Voices.Single(v => v.Id == "bm_george").Realizations.Single();
        Assert.Equal(RealizationKind.StyleTensor, british.Kind);
        Assert.Equal("kokoro", british.Engine);
        Assert.Equal(string.Empty, british.Path);
        Assert.Equal("en-gb", british.Phonemizer);
        Assert.Equal("en-us", Core.Voices.Single(v => v.Id == "am_adam").Realizations.Single().Phonemizer);
        Assert.Equal("zh", Core.Voices.Single(v => v.Id == "zf_xiaobei").Realizations.Single().Phonemizer);
    }

    [Fact]
    public void Pack_DescribesTheStockBank()
    {
        Assert.Equal("aiva-kokoro-stock", Core.Pack.Id);
        Assert.Equal(Core.Voices.Count, Core.Pack.VoiceCount);
        Assert.True(Core.Pack.Enabled);
        Assert.All(Core.Voices, v => Assert.Equal(Core.Pack.Id, v.PackId));
    }

    [Fact]
    public void Accents_CoverTheRequiredRegionsAndKokoroLanguages()
    {
        foreach (var label in new[]
                 {
                     "American", "British", "Irish", "Scottish", "Welsh", "Northern English",
                     "Midlands English", "Southern English", "Spanish", "French", "Italian",
                     "Brazilian Portuguese", "Hindi", "Japanese", "Mandarin Chinese",
                 })
        {
            Assert.Contains(Accents.All, a => a.Label == label);
        }

        Assert.Equal(Accents.All.Count, Accents.All.Select(a => a.Code).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Equal("Irish", Accents.LabelFor("en-ie"));
        Assert.Equal("en-IE", Accents.Canonical("EN-ie"));
        Assert.False(Accents.IsKnown("en-XX"));
    }

    [Theory]
    [InlineData("en-us", 'a')]
    [InlineData("en-gb", 'b')]
    [InlineData("fr", 'f')]
    [InlineData("zh", 'z')]
    public void Phonemizer_MapsToKokoroLanguageLetter(string phonemizer, char letter) =>
        Assert.Equal(letter, Phonemizers.PrefixFor(phonemizer));

    [Fact]
    public void InternalName_HasTheShapeKokoroSharpParses()
    {
        var name = Phonemizers.InternalName("isles.ie_m_01", "en-gb", "male");
        Assert.Equal("bm_pk_isles.ie_m_01", name);
        Assert.Equal('_', name[2]);
        Assert.Equal("af_pk_x.y", Phonemizers.InternalName("x.y", "en-us", "neutral"));
        // Never collides with a stock bank id.
        Assert.DoesNotContain(Core.Voices, v => v.Id == name);
    }

    [Fact]
    public void NpyHeader_AcceptsTheStyleShapeAndRejectsOthers()
    {
        Assert.Null(NpyHeader.ValidateStyleTensor(PackFixture.Npy()));
        Assert.NotNull(NpyHeader.ValidateStyleTensor(PackFixture.Npy("<f4", 10, 1, 256)));
        Assert.NotNull(NpyHeader.ValidateStyleTensor(PackFixture.Npy("<f8")));
        Assert.NotNull(NpyHeader.ValidateStyleTensor("garbage"u8));
        Assert.NotNull(NpyHeader.ValidateStyleTensor([]));
    }
}
