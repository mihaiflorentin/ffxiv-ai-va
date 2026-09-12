namespace AIVoiceActing.Domain.Pipeline;

/// <summary>
/// Minimal push-based event stream (plain-C# stand-in for TextToTalk's R3 observables, per
/// controller ruling): sources emit on their own thread and operators forward synchronously
/// on the emitting thread. Subscriptions are wired once at pipeline construction and live
/// for the plugin's lifetime, so there is no per-subscription unsubscribe.
/// </summary>
public sealed class PipelineSource<T>
{
    private event Action<T>? OnEmit;

    /// <summary>Pushes an item to every downstream operator.</summary>
    public void Emit(T item) => this.OnEmit?.Invoke(item);

    /// <summary>Wires a handler; pipeline composition only (no unsubscribe by design).</summary>
    public void Subscribe(Action<T> handler) => this.OnEmit += handler;
}

/// <summary>Composition operators over <see cref="PipelineSource{T}"/> (R3 semantics subset).</summary>
public static class Pipeline
{
    /// <summary>Forwards every item from every source, in source order per emission.</summary>
    public static PipelineSource<T> Merge<T>(params IReadOnlyList<PipelineSource<T>> sources)
    {
        var merged = new PipelineSource<T>();
        foreach (var source in sources)
        {
            source.Subscribe(merged.Emit);
        }

        return merged;
    }

    /// <summary>Forwards only items matching the predicate (R3 Where).</summary>
    public static PipelineSource<T> Where<T>(PipelineSource<T> source, Func<T, bool> predicate)
    {
        var filtered = new PipelineSource<T>();
        source.Subscribe(item =>
        {
            if (predicate(item))
            {
                filtered.Emit(item);
            }
        });
        return filtered;
    }

    /// <summary>
    /// Suppresses consecutive duplicates per the comparer (R3 DistinctUntilChanged):
    /// identical/equivalent adjacent events collapse, while a repeat that arrives after a
    /// different item passes through.
    /// </summary>
    public static PipelineSource<T> DistinctUntilChanged<T>(
        PipelineSource<T> source,
        IEqualityComparer<T> comparer)
    {
        var distinct = new PipelineSource<T>();
        T? last = default;
        var hasLast = false;
        source.Subscribe(item =>
        {
            if (hasLast && comparer.Equals(last!, item))
            {
                return;
            }

            last = item;
            hasLast = true;
            distinct.Emit(item);
        });
        return distinct;
    }
}
