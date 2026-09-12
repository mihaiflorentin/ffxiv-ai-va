namespace AIVoiceActing.Domain;

/// <summary>
/// Rolling per-session dialogue context (pure, Dalamud-free): keeps the last
/// <see cref="WindowCap"/> lines per session id, dropping the oldest beyond the cap, so
/// the emotion director can read recent exchanges. Capture sources append lines while a
/// cutscene/Talk session progresses; <see cref="EndSession"/> clears when it ends.
/// Thread-safe: capture sources and the speech pipeline run on different threads.
/// </summary>
public sealed class DialogueSessionFactory
{
    /// <summary>Maximum remembered lines per session; older entries drop first.</summary>
    public const int WindowCap = 16;

    private readonly object gate = new();
    private readonly Dictionary<string, Queue<DialogueLine>> sessions = new(StringComparer.Ordinal);

    public void Append(string sessionId, DialogueLine line)
    {
        lock (this.gate)
        {
            if (!this.sessions.TryGetValue(sessionId, out var window))
            {
                window = [];
                this.sessions.Add(sessionId, window);
            }

            window.Enqueue(line);
            while (window.Count > WindowCap)
            {
                window.Dequeue();
            }
        }
    }

    /// <summary>Ordered (oldest first) snapshot of the session window; empty when unknown.</summary>
    public IReadOnlyList<DialogueLine> GetContext(string sessionId)
    {
        lock (this.gate)
        {
            return this.sessions.TryGetValue(sessionId, out var window)
                ? window.ToArray()
                : [];
        }
    }

    public int Count(string sessionId)
    {
        lock (this.gate)
        {
            return this.sessions.TryGetValue(sessionId, out var window) ? window.Count : 0;
        }
    }

    public void EndSession(string sessionId)
    {
        lock (this.gate)
        {
            this.sessions.Remove(sessionId);
        }
    }
}
