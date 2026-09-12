namespace AIVoiceActing.Tests.Pipeline;

using AIVoiceActing.Domain.Pipeline;
using Xunit;

public sealed class TriggerGateTests
{
    [Fact]
    public void Trigger_PlainText_IsCaseSensitiveContains()
    {
        var trigger = new TriggerSpec("hello", IsRegex: false);
        Assert.True(trigger.Match("say hello now"));
        Assert.False(trigger.Match("say HELLO now"));
        Assert.False(trigger.Match(null));
    }

    [Fact]
    public void Trigger_Regex_Matches()
    {
        var trigger = new TriggerSpec(@"\bh[ae]y\b", IsRegex: true);
        Assert.True(trigger.Match("hey you"));
        Assert.False(trigger.Match("hxy you"));
    }

    [Fact]
    public void Trigger_InvalidRegex_NeverMatches()
    {
        var trigger = new TriggerSpec("([bad", IsRegex: true);
        Assert.False(trigger.Match("anything"));
    }

    [Fact]
    public void TextGate_EmptyTriggers_AdmitEverything()
    {
        var gate = new TextGate(() => [], () => []);
        Assert.True(gate.IsTextGood("anything"));
    }

    [Fact]
    public void TextGate_BlankOnlyTriggers_StillGateMatching()
    {
        // TTT port: blank entries are skipped for matching, but a non-empty trigger list
        // still requires a (non-blank) match — blank-only lists admit nothing.
        var gate = new TextGate(() => [new TriggerSpec("  ", false)], () => []);
        Assert.False(gate.IsTextGood("anything"));
    }

    [Fact]
    public void TextGate_BlankEntries_AreSkippedForMatching()
    {
        var gate = new TextGate(
            () => [new TriggerSpec("  ", false), new TriggerSpec("gift", false)], () => []);
        Assert.True(gate.IsTextGood("a gift"));
        Assert.False(gate.IsTextGood("no match"));
    }

    [Fact]
    public void Exclusion_WinsOverTrigger_EvenWhenBothMatch()
    {
        var good = new List<TriggerSpec> { new("gift", false) };
        var bad = new List<TriggerSpec> { new("gift", false) };
        var gate = new TextGate(() => good, () => bad);

        Assert.True(gate.IsTextGood("a gift"));  // trigger matches
        Assert.True(gate.IsTextBad("a gift"));   // and the exclusion matches (checked first)
    }

    [Fact]
    public void Exclusions_ReadLiveFromConfig()
    {
        var bad = new List<TriggerSpec>();
        var gate = new TextGate(() => [], () => bad);

        Assert.False(gate.IsTextBad("spoiler"));
        bad.Add(new TriggerSpec("spoiler", false));
        Assert.True(gate.IsTextBad("spoiler"));
    }
}
