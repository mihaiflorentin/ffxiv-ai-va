namespace AIVoiceActing.Tests;

using AIVoiceActing.Infrastructure.Text;
using Xunit;

public sealed class LexiconProcessorTests
{
    [Fact]
    public void Apply_SubstitutesWholeWordsCaseInsensitively()
    {
        var lexicon = new LexiconProcessor(new Dictionary<string, string>
        {
            ["chocobo"] = "kweh",
        });

        Assert.Equal("The kweh runs.", lexicon.Apply("The CHOCOBO runs."));
        Assert.Equal("The kweh runs.", lexicon.Apply("The Chocobo runs."));
    }

    [Fact]
    public void Apply_DoesNotMatchInsideWords()
    {
        var lexicon = new LexiconProcessor(new Dictionary<string, string>
        {
            ["chocobo"] = "kweh",
        });

        Assert.Equal("A chocobos feather", lexicon.Apply("A chocobos feather"));
    }

    [Fact]
    public void Apply_AppliesMultipleEntriesInInsertionOrder()
    {
        var lexicon = new LexiconProcessor(new Dictionary<string, string>
        {
            ["garlean"] = "imperial",
            ["imperial"] = "citizen",
        });

        // "garlean" rewrites first; the rewritten text is then seen by the later entry.
        Assert.Equal("a citizen scout", lexicon.Apply("a Garlean scout"));
    }

    [Fact]
    public void Apply_NoMatch_PassesThrough()
    {
        var lexicon = new LexiconProcessor(new Dictionary<string, string>
        {
            ["chocobo"] = "kweh",
        });

        var text = "Nothing to substitute here.";
        Assert.Equal(text, lexicon.Apply(text));
    }

    [Fact]
    public void Apply_EmptyLexicon_PassesThrough()
    {
        var lexicon = LexiconProcessor.Empty;

        Assert.Equal("Unchanged.", lexicon.Apply("Unchanged."));
    }
}
