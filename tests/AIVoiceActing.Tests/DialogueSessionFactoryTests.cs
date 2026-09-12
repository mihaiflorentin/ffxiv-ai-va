namespace AIVoiceActing.Tests;

using AIVoiceActing.Domain;
using Xunit;

public sealed class DialogueSessionFactoryTests
{
    private static DialogueLine Line(string text, int minute = 0) =>
        new("npc: test", "Test", text, DateTimeOffset.UnixEpoch.AddMinutes(minute));

    [Fact]
    public void Append_KeepsOrderedHistoryOldestFirst()
    {
        var factory = new DialogueSessionFactory();
        factory.Append("s", Line("one", 1));
        factory.Append("s", Line("two", 2));
        factory.Append("s", Line("three", 3));

        var context = factory.GetContext("s");

        Assert.Equal(3, factory.Count("s"));
        Assert.Equal(["one", "two", "three"], context.Select(l => l.Text));
    }

    [Fact]
    public void Append_CapsWindow_AndDropsOldest()
    {
        var factory = new DialogueSessionFactory();
        for (var i = 0; i < DialogueSessionFactory.WindowCap + 4; i++)
        {
            factory.Append("s", Line($"line-{i}", i));
        }

        var context = factory.GetContext("s");

        Assert.Equal(DialogueSessionFactory.WindowCap, factory.Count("s"));
        Assert.Equal(DialogueSessionFactory.WindowCap, context.Count);
        Assert.Equal("line-4", context[0].Text); // first four dropped, oldest first
        Assert.Equal($"line-{DialogueSessionFactory.WindowCap + 3}", context[^1].Text);
    }

    [Fact]
    public void Sessions_AreIsolated()
    {
        var factory = new DialogueSessionFactory();
        factory.Append("a", Line("a1"));
        factory.Append("b", Line("b1"));
        factory.Append("a", Line("a2"));

        Assert.Equal(["a1", "a2"], factory.GetContext("a").Select(l => l.Text));
        Assert.Equal(["b1"], factory.GetContext("b").Select(l => l.Text));
        Assert.Equal(2, factory.Count("a"));
        Assert.Equal(1, factory.Count("b"));
    }

    [Fact]
    public void EndSession_ClearsTheWindow()
    {
        var factory = new DialogueSessionFactory();
        factory.Append("s", Line("one"));
        factory.Append("s", Line("two"));

        factory.EndSession("s");

        Assert.Equal(0, factory.Count("s"));
        Assert.Empty(factory.GetContext("s"));
    }

    [Fact]
    public void Append_AfterEndSession_StartsAFreshWindow()
    {
        var factory = new DialogueSessionFactory();
        factory.Append("s", Line("old"));
        factory.EndSession("s");
        factory.Append("s", Line("new"));

        Assert.Equal(["new"], factory.GetContext("s").Select(l => l.Text));
    }

    [Fact]
    public void UnknownSession_IsEmpty()
    {
        var factory = new DialogueSessionFactory();

        Assert.Equal(0, factory.Count("missing"));
        Assert.Empty(factory.GetContext("missing"));
    }
}
