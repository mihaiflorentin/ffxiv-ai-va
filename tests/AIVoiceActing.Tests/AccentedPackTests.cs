namespace AIVoiceActing.Tests;

using AIVoiceActing.Domain;
using AIVoiceActing.Ports;
using AIVoiceActing.UI.State;
using Xunit;

public sealed class AccentedPackTests
{
    private static readonly string[] AccentedCodes =
        ["en-x-ja", "en-x-zh", "en-x-fr", "en-x-es", "en-x-it", "en-x-pt", "en-x-hi", "en-x-ru", "en-x-de"];

    private static string PackDirectory()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "AIVoiceActing.sln")))
        {
            dir = dir.Parent;
        }

        return Path.Combine(
            dir?.FullName ?? throw new DirectoryNotFoundException("repo root not found above " + AppContext.BaseDirectory),
            "src",
            "AIVoiceActing",
            "voicepacks",
            "aiva-accented");
    }

    private static VoicePackManifest Manifest() =>
        VoicePackManifest.Parse(File.ReadAllText(Path.Combine(PackDirectory(), VoicePackManifest.FileName)));

    [Fact]
    public void Accents_ListEveryAccentedEnglishWithPlainLabels()
    {
        foreach (var code in AccentedCodes)
        {
            Assert.True(Accents.IsKnown(code), code);
            Assert.Equal(Phonemizers.EnglishUs, Accents.Find(code)!.DefaultPhonemizer);
        }

        Assert.Equal("English (Japanese accent)", Accents.LabelFor("en-x-ja"));
        Assert.Equal("English (Russian accent)", Accents.LabelFor("EN-X-RU"));
        Assert.Equal(Accents.All.Count, Accents.All.Select(a => a.Code).Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Theory]
    [InlineData("en-x-ja", "japanese accent")]
    [InlineData("en-x-ja", "japanese")]
    [InlineData("en-x-ru", "russian")]
    [InlineData("en-x-de", "german")]
    [InlineData("en-x-hi", "accented")]
    [InlineData("en-IE", "irish")]
    public void SearchAliases_FindTheAccent(string code, string word) =>
        Assert.True(Accents.MatchesSearch(code, word));

    [Fact]
    public void SearchAliases_DoNotLeakAcrossAccents()
    {
        Assert.False(Accents.MatchesSearch("en-x-ru", "japanese"));
        Assert.False(Accents.MatchesSearch("en-US", "accented"));
    }

    [Fact]
    public void LibrarySearch_FindsAnAccentedVoiceByAccentWords()
    {
        VoiceInfo Accented(string id, string accent) =>
            new(id, id, accent, "female", "adult", "A voice.", "Apache-2.0", null, [], VoiceQuality.Experimental, null, "aiva-accented", []);

        var library = new[] { Accented("accented.ja_f_01", "en-x-ja"), Accented("accented.fr_f_01", "en-x-fr") };
        var result = new VoiceLibraryModel { Search = "japanese accent" }.Compute(library);

        Assert.Equal(["accented.ja_f_01"], result.Rows.Select(r => r.Id));
    }

    [Fact]
    public void Manifest_ParsesWithApacheLicenseAndKokoroAttribution()
    {
        var manifest = Manifest();

        Assert.Equal("aiva-accented", manifest.Id);
        Assert.Equal("Apache-2.0", manifest.License.Spdx);
        Assert.Contains("hexgrad/Kokoro-82M", manifest.License.Attribution);
        Assert.NotEmpty(manifest.Voices);
    }

    [Fact]
    public void Voices_AreAccentedEnglishOnAnEnglishPhonemizer()
    {
        foreach (var voice in Manifest().Voices)
        {
            Assert.Contains(voice.Accent, AccentedCodes, StringComparer.OrdinalIgnoreCase);
            Assert.StartsWith("accented.", voice.Id);
            var tensor = Assert.Single(voice.Realizations);
            Assert.Equal(RealizationKind.StyleTensor, tensor.Kind);
            Assert.Contains(tensor.Phonemizer, new[] { Phonemizers.EnglishUs, Phonemizers.EnglishGb });
            Assert.Equal(VoiceQuality.Experimental, voice.Quality);
            Assert.NotNull(voice.PreviewFile);
        }
    }

    [Fact]
    public void EveryReferencedFileShipsAndTheTensorsAreKokoroStyleVectors()
    {
        var manifest = Manifest();
        var pack = PackDirectory();

        Assert.Empty(manifest.FindMissingFiles(file => File.Exists(Path.Combine(pack, file))));
        Assert.True(File.Exists(Path.Combine(pack, "LICENSE")));
        Assert.True(File.Exists(Path.Combine(pack, "NOTICE")));

        foreach (var voice in manifest.Voices)
        {
            Assert.Null(NpyHeader.ValidateStyleTensor(File.ReadAllBytes(Path.Combine(pack, voice.Realizations[0].File))));
        }
    }

    [Fact]
    public void ReadmeDocumentsTheIntelligibilityGate()
    {
        var readme = File.ReadAllText(Path.Combine(PackDirectory(), "README.md"));

        Assert.Contains("WER", readme);
        Assert.Contains("Russian", readme);
        Assert.Contains("German", readme);
    }
}
