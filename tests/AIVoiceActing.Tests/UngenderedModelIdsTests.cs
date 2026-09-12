namespace AIVoiceActing.Tests;

using AIVoiceActing.Infrastructure.Dalamud;
using Xunit;

public sealed class UngenderedModelIdsTests
{
    [Fact]
    public void VerbatimFile_ParsesAndContains2520()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "overridenModelIds.txt");
        Assert.True(File.Exists(path), $"Missing copied overrides file at {path}");
        var ids = UngenderedModelIds.Parse(File.ReadAllText(path));
        Assert.Contains(2520, ids);
        Assert.Single(ids); // this checkout's file: "2520\t; Feo Ul" only
    }

    [Fact]
    public void Parse_IgnoresCommentsAndBlankLines()
    {
        const string data = """
            # a leading comment
            2520	; Feo Ul

            777 ; some NPC
            
               ; comment-only line
            42
            """;
        var ids = UngenderedModelIds.Parse(data);
        Assert.Equal(new HashSet<int> { 2520, 777, 42 }, ids);
    }

    [Fact]
    public void Parse_HandlesCrLFAndNoTrailingNewline()
    {
        var ids = UngenderedModelIds.Parse("2520\t; Feo Ul\r\n999\n7");
        Assert.Equal(new HashSet<int> { 2520, 999, 7 }, ids);
    }

    [Fact]
    public void Parse_BadModelId_ThrowsAggregateException()
    {
        var ex = Assert.Throws<AggregateException>(() => UngenderedModelIds.Parse("notanid ; oops"));
        Assert.Contains("notanid", ex.Message);
    }
}
