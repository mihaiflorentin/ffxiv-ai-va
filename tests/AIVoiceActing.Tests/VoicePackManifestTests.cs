namespace AIVoiceActing.Tests;

using AIVoiceActing.Domain;
using AIVoiceActing.Ports;
using AIVoiceActing.Tests.Mock;
using Xunit;

public sealed class VoicePackManifestTests
{
    [Fact]
    public void ValidManifest_ParsesAndNormalizes()
    {
        var manifest = VoicePackManifest.Parse(PackFixture.Manifest(
            "aiva-isles", [("isles.ie_m_01", "style/a.npy"), ("isles.ie_f_01", "style/b.npy")], accent: "en-ie"));

        Assert.Equal("aiva-isles", manifest.Id);
        Assert.Equal("CC-BY-SA-4.0", manifest.License.Spdx);
        Assert.Equal(2, manifest.Voices.Count);
        var voice = manifest.Voices[0];
        Assert.Equal("en-IE", voice.Accent);
        Assert.Equal(VoiceQuality.Experimental, voice.Quality);
        Assert.Equal("adult", voice.AgeBand);
        var tensor = Assert.Single(voice.Realizations);
        Assert.Equal(RealizationKind.StyleTensor, tensor.Kind);
        Assert.Equal("kokoro", tensor.Engine);
        Assert.Equal("en-gb", tensor.Phonemizer);
    }

    [Fact]
    public void MissingPhonemizer_FollowsTheAccentDefault()
    {
        var british = VoicePackManifest.Parse(PackFixture.Manifest("p", [("v.one", "s/a.npy")], accent: "en-GB-SCT", phonemizer: null));
        var american = VoicePackManifest.Parse(PackFixture.Manifest("p", [("v.one", "s/a.npy")], accent: "en-US", phonemizer: null));
        Assert.Equal("en-gb", british.Voices[0].Realizations[0].Phonemizer);
        Assert.Equal("en-us", american.Voices[0].Realizations[0].Phonemizer);
    }

    [Theory]
    [InlineData("a", "en-us")]
    [InlineData("B", "en-gb")]
    [InlineData("en-US", "en-us")]
    public void Phonemizer_AcceptsKokoroLettersAndLanguageTags(string written, string expected)
    {
        var manifest = VoicePackManifest.Parse(PackFixture.Manifest("p", [("v.one", "s/a.npy")], phonemizer: written));
        Assert.Equal(expected, manifest.Voices[0].Realizations[0].Phonemizer);
    }

    [Fact]
    public void UnknownPhonemizer_Rejected() =>
        Assert.Throws<VoicePackInvalidException>(() =>
            VoicePackManifest.Parse(PackFixture.Manifest("p", [("v.one", "s/a.npy")], phonemizer: "klingon")));

    [Fact]
    public void NotJson_Rejected() =>
        Assert.Throws<VoicePackInvalidException>(() => VoicePackManifest.Parse("{ nope"));

    [Theory]
    [InlineData("2")]
    [InlineData("0")]
    public void UnknownSchemaVersion_Rejected(string schema)
    {
        var json = PackFixture.Manifest("p", [("v.one", "s/a.npy")]).Replace("\"schema\": 1", $"\"schema\": {schema}");
        var ex = Assert.Throws<VoicePackInvalidException>(() => VoicePackManifest.Parse(json));
        Assert.Contains("schema", ex.Message);
    }

    [Fact]
    public void MissingSchema_Rejected()
    {
        var json = PackFixture.Manifest("p", [("v.one", "s/a.npy")]).Replace("\"schema\": 1,", string.Empty);
        Assert.Throws<VoicePackInvalidException>(() => VoicePackManifest.Parse(json));
    }

    [Fact]
    public void DuplicateVoiceIds_Rejected()
    {
        var json = PackFixture.Manifest("p", [("v.one", "s/a.npy"), ("v.one", "s/b.npy")]);
        var ex = Assert.Throws<VoicePackInvalidException>(() => VoicePackManifest.Parse(json));
        Assert.Contains("v.one", ex.Message);
    }

    [Theory]
    [InlineData("../evil.npy")]
    [InlineData("style/../../evil.npy")]
    [InlineData("/etc/passwd.npy")]
    [InlineData("C:/x/evil.npy")]
    [InlineData("style\\\\evil.npy")]
    [InlineData("./a.npy")]
    [InlineData("style//a.npy")]
    public void PathTraversal_Rejected(string file)
    {
        var json = PackFixture.Manifest("p", [("v.one", file)]);
        Assert.Throws<VoicePackInvalidException>(() => VoicePackManifest.Parse(json));
    }

    [Fact]
    public void PreviewAndTranscriptPaths_AreCheckedToo()
    {
        var preview = PackFixture.Manifest("p", [("v.one", "s/a.npy")]).Replace("\"quality\"", "\"previewFile\": \"../p.wav\", \"quality\"");
        Assert.Throws<VoicePackInvalidException>(() => VoicePackManifest.Parse(preview));

        var transcript = """
            { "schema": 1, "id": "p", "name": "P", "version": "1", "license": { "spdx": "MIT" },
              "voices": [ { "id": "v.one", "name": "V", "accent": "en-US", "sex": "male",
                "realizations": [ { "kind": "ReferenceClip", "file": "clips/a.wav", "transcript": "../a.txt" } ] } ] }
            """;
        Assert.Throws<VoicePackInvalidException>(() => VoicePackManifest.Parse(transcript));
    }

    [Fact]
    public void MissingLicense_Rejected()
    {
        var json = PackFixture.Manifest("p", [("v.one", "s/a.npy")], license: string.Empty);
        var ex = Assert.Throws<VoicePackInvalidException>(() => VoicePackManifest.Parse(json));
        Assert.Contains("license", ex.Message);
    }

    [Fact]
    public void LicenseWithoutSpdx_Rejected()
    {
        var json = PackFixture.Manifest("p", [("v.one", "s/a.npy")], license: "\"license\": { \"attribution\": \"x\" },");
        Assert.Throws<VoicePackInvalidException>(() => VoicePackManifest.Parse(json));
    }

    [Theory]
    [InlineData("Upper.Case")]
    [InlineData("has space")]
    [InlineData("slash/id")]
    [InlineData("tilde~")]
    public void BadVoiceIdCharset_Rejected(string id)
    {
        var json = PackFixture.Manifest("p", [(id, "s/a.npy")]);
        Assert.Throws<VoicePackInvalidException>(() => VoicePackManifest.Parse(json));
    }

    [Fact]
    public void BadPackIdCharset_Rejected()
    {
        var json = PackFixture.Manifest("Bad Pack", [("v.one", "s/a.npy")]);
        Assert.Throws<VoicePackInvalidException>(() => VoicePackManifest.Parse(json));
    }

    [Fact]
    public void UnknownAccent_Rejected()
    {
        var json = PackFixture.Manifest("p", [("v.one", "s/a.npy")], accent: "en-ATLANTIS");
        Assert.Throws<VoicePackInvalidException>(() => VoicePackManifest.Parse(json));
    }

    [Fact]
    public void PackVoiceCannotClaimStockQuality()
    {
        var json = PackFixture.Manifest("p", [("v.one", "s/a.npy")]).Replace("\"experimental\"", "\"stock\"");
        Assert.Throws<VoicePackInvalidException>(() => VoicePackManifest.Parse(json));
    }

    [Fact]
    public void StyleTensor_MustBeNpy_AndClip_MustBeWav()
    {
        Assert.Throws<VoicePackInvalidException>(() =>
            VoicePackManifest.Parse(PackFixture.Manifest("p", [("v.one", "s/a.pt")])));
        var clip = """
            { "schema": 1, "id": "p", "name": "P", "version": "1", "license": { "spdx": "MIT" },
              "voices": [ { "id": "v.one", "name": "V", "accent": "en-US", "sex": "male",
                "realizations": [ { "kind": "ReferenceClip", "file": "clips/a.mp3" } ] } ] }
            """;
        Assert.Throws<VoicePackInvalidException>(() => VoicePackManifest.Parse(clip));
    }

    [Fact]
    public void ReferenceClip_KeepsTranscript_AndAnyCloningEngine()
    {
        var json = """
            { "schema": 1, "id": "p", "name": "P", "version": "1", "license": { "spdx": "MIT" },
              "voices": [ { "id": "v.one", "name": "V", "accent": "en-IE", "sex": "female",
                "realizations": [ { "kind": "ReferenceClip", "file": "clips/a.wav", "transcript": "clips/a.txt" } ] } ] }
            """;
        var clip = VoicePackManifest.Parse(json).Voices[0].Realizations[0];
        Assert.Equal(RealizationKind.ReferenceClip, clip.Kind);
        Assert.Equal("*", clip.Engine);
        Assert.Equal("clips/a.txt", clip.Transcript);
        Assert.Null(clip.Phonemizer);
    }

    [Fact]
    public void NoRealizations_Rejected()
    {
        var json = """
            { "schema": 1, "id": "p", "name": "P", "version": "1", "license": { "spdx": "MIT" },
              "voices": [ { "id": "v.one", "name": "V", "accent": "en-US", "sex": "male", "realizations": [] } ] }
            """;
        Assert.Throws<VoicePackInvalidException>(() => VoicePackManifest.Parse(json));
    }

    [Fact]
    public void FindMissingFiles_ReportsEveryReferencedMissingFile()
    {
        var manifest = VoicePackManifest.Parse(PackFixture.Manifest(
            "p", [("v.one", "style/a.npy"), ("v.two", "style/b.npy")]));
        var missing = manifest.FindMissingFiles(file => file == "style/a.npy");
        Assert.Equal([("v.two", "style/b.npy")], missing);
    }

    [Fact]
    public void ControlCharactersInNames_Rejected()
    {
        var json = PackFixture.Manifest("p", [("v.one", "s/a.npy")]).Replace("v.one name", "bad\\u0007name");
        Assert.Throws<VoicePackInvalidException>(() => VoicePackManifest.Parse(json));
    }
}
