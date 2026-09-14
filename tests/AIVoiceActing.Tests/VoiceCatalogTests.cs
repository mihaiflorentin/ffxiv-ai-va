namespace AIVoiceActing.Tests;

using AIVoiceActing.Domain;
using Xunit;

public sealed class VoiceCatalogTests : IDisposable
{
    private readonly string directory = Path.Combine(
        Path.GetTempPath(), "aiva-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(this.directory))
        {
            Directory.Delete(this.directory, recursive: true);
        }
    }

    [Theory]
    [InlineData("af_heart", VoiceLanguage.AmericanEnglish, VoiceSex.Female)]
    [InlineData("am_eric", VoiceLanguage.AmericanEnglish, VoiceSex.Male)]
    [InlineData("bf_emma", VoiceLanguage.BritishEnglish, VoiceSex.Female)]
    [InlineData("ef_dora", VoiceLanguage.Spanish, VoiceSex.Female)]
    [InlineData("ff_siwis", VoiceLanguage.French, VoiceSex.Female)]
    [InlineData("if_sara", VoiceLanguage.Italian, VoiceSex.Female)]
    [InlineData("pf_dora", VoiceLanguage.Portuguese, VoiceSex.Female)]
    [InlineData("hf_alpha", VoiceLanguage.Hindi, VoiceSex.Female)]
    [InlineData("jf_alpha", VoiceLanguage.Japanese, VoiceSex.Female)]
    [InlineData("zm_yunjian", VoiceLanguage.Chinese, VoiceSex.Male)]
    public void Describe_ParsesKokoroPrefixes(string id, VoiceLanguage language, VoiceSex sex)
    {
        var entry = VoiceCatalog.Describe(id);
        Assert.Equal(id, entry.Id);
        Assert.Equal(language, entry.Language);
        Assert.Equal(sex, entry.Sex);
    }

    [Theory]
    [InlineData("")]
    [InlineData("ab")]
    [InlineData("default")]
    [InlineData("xf_short")]
    [InlineData("afheart")]
    public void Describe_UnknownForJunkIds(string id)
    {
        var entry = VoiceCatalog.Describe(id);
        Assert.Equal(VoiceLanguage.Unknown, entry.Language);
        Assert.Equal(VoiceSex.Unknown, entry.Sex);
    }

    [Fact]
    public void FromDirectory_EnumeratesNpyFilesSortedById()
    {
        Directory.CreateDirectory(this.directory);
        foreach (var name in new[] { "zm_yunjian.npy", "af_heart.npy", "bf_emma.npy", "notes.txt" })
        {
            File.WriteAllText(Path.Combine(this.directory, name), "x");
        }

        var entries = VoiceCatalog.FromDirectory(this.directory);

        Assert.Equal(["af_heart", "bf_emma", "zm_yunjian"], entries.Select(e => e.Id).ToArray());
        Assert.Equal(VoiceLanguage.Chinese, entries[2].Language);
    }

    [Fact]
    public void FromDirectory_MissingOrEmptyDir_YieldsEmptyWithoutThrowing()
    {
        Assert.Empty(VoiceCatalog.FromDirectory(Path.Combine(this.directory, "absent")));
        Directory.CreateDirectory(this.directory);
        Assert.Empty(VoiceCatalog.FromDirectory(this.directory));
        Assert.Empty(VoiceCatalog.FromDirectory(string.Empty));
    }

    [Fact]
    public void Filter_CombinesLanguageSexAndCaseInsensitiveContains()
    {
        var catalog = new[]
        {
            VoiceCatalog.Describe("af_heart"),
            VoiceCatalog.Describe("am_eric"),
            VoiceCatalog.Describe("bf_emma"),
            VoiceCatalog.Describe("zm_yunjian"),
        };

        Assert.Equal(2, VoiceCatalog.Filter(catalog, VoiceLanguage.AmericanEnglish, null, null).Count);
        Assert.Equal(["am_eric", "zm_yunjian"], VoiceCatalog.Filter(catalog, null, VoiceSex.Male, null).Select(e => e.Id).ToArray());
        Assert.Single(VoiceCatalog.Filter(catalog, null, null, "HEART"));
        Assert.Single(VoiceCatalog.Filter(catalog, VoiceLanguage.AmericanEnglish, VoiceSex.Female, null));
        Assert.Empty(VoiceCatalog.Filter(catalog, VoiceLanguage.French, null, null));
        Assert.Equal(4, VoiceCatalog.Filter(catalog, null, null, null).Count);
    }
}
