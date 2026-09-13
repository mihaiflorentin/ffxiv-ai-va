namespace AIVoiceActing.Tests;

using AIVoiceActing.Infrastructure.Text;
using AIVoiceActing.Tests.Mock;
using Xunit;

/// <summary>
/// The .pls-style lexicon file loader: word → replacement pairs from user-configured
/// file paths; missing/invalid files warn and are skipped (never fatal), and parsed
/// content is cached per file so unchanged files reuse the same entry instance.
/// </summary>
public sealed class LexiconFileLoaderTests : IDisposable
{
    private readonly string directory =
        Directory.CreateTempSubdirectory("aiva-lexicon-tests").FullName;

    public void Dispose()
    {
        try
        {
            Directory.Delete(this.directory, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }
    }

    private string WriteFile(string name, string content)
    {
        var path = Path.Combine(this.directory, name);
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public void ParsesGraphemePhonemePairs()
    {
        var path = this.WriteFile("main.pls", """
            <?xml version="1.0" encoding="UTF-8"?>
            <lexicon version="1.0" alphabet="ipa" xml:lang="en-US">
              <lexeme>
                <grapheme>Feo Ul</grapheme>
                <phoneme> Fay Oh Ul </phoneme>
              </lexeme>
              <lexeme>
                <grapheme>sine</grapheme>
                <phoneme>sign</phoneme>
              </lexeme>
            </lexicon>
            """);

        var entries = LexiconFileLoader.Load([path]);

        Assert.Equal(2, entries.Count);
        Assert.Equal("Fay Oh Ul", entries["Feo Ul"]);
        Assert.Equal("sign", entries["sine"]);
    }

    [Fact]
    public void ParsesFilesWithoutXmlNamespace()
    {
        var path = this.WriteFile("plain.pls", """
            <lexicon>
              <lexeme>
                <grapheme>rogue</grapheme>
                <phoneme>roh</phoneme>
              </lexeme>
            </lexicon>
            """);

        var pair = Assert.Single(LexiconFileLoader.Load([path]));
        Assert.Equal("rogue", pair.Key);
        Assert.Equal("roh", pair.Value);
    }

    [Fact]
    public void MergesFilesInOrder_AndLaterFilesWin()
    {
        var first = this.WriteFile("first.pls",
            "<lexicon><lexeme><grapheme>wraith</grapheme><phoneme>ray</phoneme></lexeme></lexicon>");
        var second = this.WriteFile("second.pls",
            "<lexicon><lexeme><grapheme>wraith</grapheme><phoneme>wrath</phoneme></lexeme>" +
            "<lexeme><grapheme>acumen</grapheme><phoneme>AK-yu-men</phoneme></lexeme></lexicon>");

        var entries = LexiconFileLoader.Load([first, second]);

        Assert.Equal(2, entries.Count);
        Assert.Equal("wrath", entries["wraith"]);
        Assert.Equal("AK-yu-men", entries["acumen"]);
    }

    [Fact]
    public void MissingFile_WarnsAndIsSkipped()
    {
        var log = new FakeLogSink();
        var good = this.WriteFile("good.pls",
            "<lexicon><lexeme><grapheme>kupo</grapheme><phoneme>coo-po</phoneme></lexeme></lexicon>");

        var entries = LexiconFileLoader.Load(
            [Path.Combine(this.directory, "nope.pls"), good], log);

        var only = Assert.Single(entries);
        Assert.Equal("kupo", only.Key);
        Assert.Equal("coo-po", only.Value);
        Assert.Contains(log.Snapshot(), c => c.Level == "Warn");
    }

    [Fact]
    public void InvalidXml_WarnsYieldsEmptyAndDoesNotThrow()
    {
        var log = new FakeLogSink();
        var broken = this.WriteFile("broken.pls", "<lexicon><lexeme><grapheme>unclosed");

        var entries = LexiconFileLoader.Load([broken], log);

        Assert.Empty(entries);
        Assert.Contains(log.Snapshot(), c => c.Level == "Warn");
    }

    [Fact]
    public void LexemesWithoutGraphemeAreIgnored()
    {
        var path = this.WriteFile("partial.pls", """
            <lexicon>
              <lexeme><phoneme>orphan</phoneme></lexeme>
              <lexeme><grapheme>kept</grapheme><phoneme>yes</phoneme></lexeme>
              <lexeme><grapheme>   </grapheme><phoneme>blank</phoneme></lexeme>
            </lexicon>
            """);

        var kept = Assert.Single(LexiconFileLoader.Load([path]));
        Assert.Equal("kept", kept.Key);
        Assert.Equal("yes", kept.Value);
    }

    [Fact]
    public void UnchangedFile_IsCachedByLastWrite()
    {
        var path = this.WriteFile("cached.pls",
            "<lexicon><lexeme><grapheme>a</grapheme><phoneme>b</phoneme></lexeme></lexicon>");

        var first = LexiconFileLoader.Load([path]);
        var second = LexiconFileLoader.Load([path]);

        Assert.Same(first, second);

        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(1));
        File.WriteAllText(path,
            "<lexicon><lexeme><grapheme>c</grapheme><phoneme>d</phoneme></lexeme></lexicon>");

        var third = LexiconFileLoader.Load([path]);

        Assert.NotSame(first, third);
        Assert.Equal("d", third["c"]);
    }
}
