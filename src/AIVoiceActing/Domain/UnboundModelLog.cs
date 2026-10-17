namespace AIVoiceActing.Domain;

/// <summary>One non-human model heard this session that no beast tribe claims yet.</summary>
public sealed record UnboundModel(int ModelId, int Lines, string LastSpeaker, DateTimeOffset LastHeard);

/// <summary>
/// Session inbox of model ids heard without a tribe binding, so the casting UI can offer a
/// one-click "assign to tribe" instead of reading the harvest log. Human NPCs carry model
/// id 0 and never appear. Thread-safe (speech worker writes, UI reads).
/// </summary>
public sealed class UnboundModelLog
{
    private const int Capacity = 64;
    private readonly object gate = new();
    private readonly Dictionary<int, UnboundModel> models = [];
    private readonly HashSet<int> dismissed = [];

    public void Record(int modelId, string speakerName, DateTimeOffset at)
    {
        if (modelId <= 0)
        {
            return;
        }

        lock (this.gate)
        {
            if (this.dismissed.Contains(modelId))
            {
                return;
            }

            this.models[modelId] = this.models.TryGetValue(modelId, out var seen)
                ? seen with { Lines = seen.Lines + 1, LastSpeaker = speakerName, LastHeard = at }
                : new UnboundModel(modelId, 1, speakerName, at);
            if (this.models.Count > Capacity)
            {
                var oldest = this.models.Values.MinBy(m => m.LastHeard)!;
                this.models.Remove(oldest.ModelId);
            }
        }
    }

    /// <summary>Most recently heard first.</summary>
    public IReadOnlyList<UnboundModel> Entries
    {
        get
        {
            lock (this.gate)
            {
                return [.. this.models.Values.OrderByDescending(m => m.LastHeard)];
            }
        }
    }

    /// <summary>Drops a model from the inbox for the rest of the session (bound or ignored).</summary>
    public void Dismiss(int modelId)
    {
        lock (this.gate)
        {
            this.models.Remove(modelId);
            this.dismissed.Add(modelId);
        }
    }
}
