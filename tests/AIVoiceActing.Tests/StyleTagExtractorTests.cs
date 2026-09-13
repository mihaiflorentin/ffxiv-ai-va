namespace AIVoiceActing.Tests;

using AIVoiceActing.Infrastructure.Text;
using Xunit;

/// <summary>
/// Ad-hoc style-tag extraction: tags wrapped in the delimiter pair are stripped from the
/// spoken text and returned as directions; disabled or delimiter-less configurations are
/// a strict passthrough.
/// </summary>
public sealed class StyleTagExtractorTests
{
    // The Configuration default: delimiter "|", derived regex "\|(.*?)\|".
    private const string DefaultRegex = "\\|(.*?)\\|";

    [Theory]
    [InlineData("hello |laughs| world", "hello world", new[] { "laughs" })]
    [InlineData("|sigh| oh well", "oh well", new[] { "sigh" })]
    [InlineData("one |laughs| two |sigh| three", "one two three", new[] { "laughs", "sigh" })]
    [InlineData("plain text", "plain text", new string[] { })]
    [InlineData("unclosed |laughs", "unclosed |laughs", new string[] { })]
    [InlineData("empty || tags", "empty tags", new string[] { })]
    public void ExtractsTagsAndCleansText(string text, string expectedText, string[] expectedTags)
    {
        var (clean, tags) = StyleTagExtractor.Extract(text, DefaultRegex, enabled: true);

        Assert.Equal(expectedText, clean);
        Assert.Equal(expectedTags, tags);
    }

    [Fact]
    public void Disabled_IsAPassthrough()
    {
        var (clean, tags) = StyleTagExtractor.Extract("hi |laughs|", DefaultRegex, enabled: false);

        Assert.Equal("hi |laughs|", clean);
        Assert.Empty(tags);
    }

    [Fact]
    public void NullOrEmptyRegex_IsAPassthrough()
    {
        var (clean, tags) = StyleTagExtractor.Extract("hi |laughs|", matchRegex: null, enabled: true);

        Assert.Equal("hi |laughs|", clean);
        Assert.Empty(tags);
    }

    [Fact]
    public void TagsAreTrimmed()
    {
        var (clean, tags) = StyleTagExtractor.Extract("ok | laughs | then", DefaultRegex, enabled: true);

        Assert.Equal("ok then", clean);
        Assert.Equal(["laughs"], tags);
    }

    [Fact]
    public void DuplicateExtractedTags_Dedupe()
    {
        var (clean, tags) = StyleTagExtractor.Extract("|laughs| a |laughs| b", DefaultRegex, enabled: true);

        Assert.Equal("a b", clean);
        Assert.Equal(["laughs"], tags);
    }
}
