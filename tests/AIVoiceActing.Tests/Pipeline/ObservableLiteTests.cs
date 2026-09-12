namespace AIVoiceActing.Tests;

using AIVoiceActing.Domain.Pipeline;
using AIVoiceActing.Ports;
using Dsl = AIVoiceActing.Domain.Pipeline.Pipeline;
using Xunit;

public sealed class ObservableLiteTests
{
    private static TextEmitEvent Event(string speaker, string text, TextSource source = TextSource.Talk) =>
        new(source, speaker, text, text, Hint: new SpeakerHint(speaker, null, null, null, null, null, null), ChatType: 0);

    [Fact]
    public void Merge_ForwardsFromEverySource()
    {
        var a = new PipelineSource<TextEmitEvent>();
        var b = new PipelineSource<TextEmitEvent>();
        var merged = Dsl.Merge([a, b]);

        var received = new List<string>();
        merged.Subscribe(ev => received.Add(ev.Text));

        a.Emit(Event("x", "one"));
        b.Emit(Event("y", "two"));
        Assert.Equal(["one", "two"], received);
    }

    [Fact]
    public void Where_DropsNonMatchingItems()
    {
        var source = new PipelineSource<int>();
        var filtered = Dsl.Where(source, i => i % 2 == 0);

        var received = new List<int>();
        filtered.Subscribe(received.Add);

        source.Emit(1);
        source.Emit(2);
        source.Emit(3);
        source.Emit(4);
        Assert.Equal([2, 4], received);
    }

    [Fact]
    public void DistinctUntilChanged_CollapsesConsecutiveEquivalents()
    {
        var source = new PipelineSource<TextEmitEvent>();
        var distinct = Dsl.DistinctUntilChanged(source, TextEmitEventComparer.Instance);

        var received = new List<string>();
        distinct.Subscribe(ev => received.Add(ev.Text));

        source.Emit(Event("Merlwyb", "Hello"));
        source.Emit(Event("Merlwyb", "Hello")); // exact duplicate collapses (TTT compares exact text values)
        source.Emit(Event("Merlwyb", "Bye"));
        Assert.Equal(["Hello", "Bye"], received);
    }

    [Fact]
    public void DistinctUntilChanged_NonAdjacentDuplicatesPass()
    {
        var source = new PipelineSource<TextEmitEvent>();
        var distinct = Dsl.DistinctUntilChanged(source, TextEmitEventComparer.Instance);

        var received = new List<string>();
        distinct.Subscribe(ev => received.Add(ev.Text));

        source.Emit(Event("A", "line"));
        source.Emit(Event("B", "other"));
        source.Emit(Event("A", "line")); // repeat after a different item must pass
        Assert.Equal(["line", "other", "line"], received);
    }

    [Fact]
    public void DistinctUntilChanged_FirstItemAlwaysPasses()
    {
        var source = new PipelineSource<int>();
        var distinct = Dsl.DistinctUntilChanged(source, EqualityComparer<int>.Default);

        var received = new List<int>();
        distinct.Subscribe(received.Add);
        source.Emit(7);
        Assert.Equal([7], received);
    }
}
