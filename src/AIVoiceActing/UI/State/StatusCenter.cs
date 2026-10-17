namespace AIVoiceActing.UI.State;

public enum Severity
{
    Info,
    Success,
    Warning,
    Error,
}

/// <summary>A button on a status message. Invoking it runs the callback and dismisses the message.</summary>
public sealed record StatusAction(string Label, Action Run);

/// <summary>
/// One entry in the status centre. <see cref="ExpiresAt"/> is null for sticky entries (errors).
/// <see cref="Count"/> is how many times an identical message was posted while it was active.
/// </summary>
public sealed record StatusMessage(
    long Id,
    string Key,
    Severity Severity,
    string Title,
    string? Detail,
    IReadOnlyList<StatusAction> Actions,
    DateTimeOffset PostedAt,
    DateTimeOffset? ExpiresAt,
    int Count = 1);

/// <summary>
/// Typed user-facing messages: errors stick until dismissed or resolved, everything else expires
/// on an injectable clock, identical messages collapse into one with a count, and an undo
/// message offers a one-shot action. Thread-safe (downloads post from worker threads).
/// </summary>
public sealed class StatusCenter
{
    public static readonly TimeSpan InfoLifetime = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan WarningLifetime = TimeSpan.FromSeconds(8);
    public static readonly TimeSpan UndoLifetime = TimeSpan.FromSeconds(8);

    private readonly object gate = new();
    private readonly Func<DateTimeOffset> clock;
    private readonly List<StatusMessage> messages = new();
    private long nextId = 1;

    public StatusCenter(Func<DateTimeOffset>? clock = null)
    {
        this.clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    /// <summary>True while any sticky error is showing.</summary>
    public bool HasError
    {
        get
        {
            lock (this.gate)
            {
                this.Prune();
                return this.messages.Any(m => m.Severity == Severity.Error);
            }
        }
    }

    /// <summary>
    /// Posts a message. With no <paramref name="key"/> the key is severity + title + detail; a
    /// repeat of an active key updates that entry in place (newest text, count + 1, timer reset).
    /// </summary>
    public StatusMessage Post(
        Severity severity,
        string title,
        string? detail = null,
        IReadOnlyList<StatusAction>? actions = null,
        string? key = null,
        TimeSpan? lifetime = null)
    {
        lock (this.gate)
        {
            this.Prune();
            var now = this.clock();
            var k = key ?? $"{severity}|{title}|{detail}";
            DateTimeOffset? expires = severity == Severity.Error
                ? null
                : now + (lifetime ?? (severity == Severity.Warning ? WarningLifetime : InfoLifetime));
            var acts = actions ?? Array.Empty<StatusAction>();

            var i = this.messages.FindIndex(m => m.Key == k);
            if (i >= 0)
            {
                var old = this.messages[i];
                var updated = old with
                {
                    Severity = severity,
                    Title = title,
                    Detail = detail,
                    Actions = acts,
                    PostedAt = now,
                    ExpiresAt = expires,
                    Count = old.Count + 1,
                };
                this.messages[i] = updated;
                return updated;
            }

            var created = new StatusMessage(this.nextId++, k, severity, title, detail, acts, now, expires);
            this.messages.Add(created);
            return created;
        }
    }

    /// <summary>
    /// Posts a toast with a single "Undo" action, available for <see cref="UndoLifetime"/>.
    /// Running Undo dismisses the message, so it fires at most once.
    /// </summary>
    public StatusMessage PostUndo(string title, Action undo, string? detail = null)
    {
        return this.Post(
            Severity.Success,
            title,
            detail,
            new[] { new StatusAction("Undo", undo) },
            key: "undo|" + Guid.NewGuid().ToString("N"),
            lifetime: UndoLifetime);
    }

    /// <summary>Messages to show now: errors first, then newest first.</summary>
    public IReadOnlyList<StatusMessage> Active()
    {
        lock (this.gate)
        {
            this.Prune();
            return this.messages
                .OrderByDescending(m => m.Severity == Severity.Error)
                .ThenByDescending(m => m.PostedAt)
                .ThenByDescending(m => m.Id)
                .ToList();
        }
    }

    public void Dismiss(long id)
    {
        lock (this.gate)
        {
            this.messages.RemoveAll(m => m.Id == id);
        }
    }

    /// <summary>Clears the message with this key (the problem went away).</summary>
    public void Resolve(string key)
    {
        lock (this.gate)
        {
            this.messages.RemoveAll(m => m.Key == key);
        }
    }

    /// <summary>Runs action <paramref name="actionIndex"/> of message <paramref name="id"/> and dismisses it.</summary>
    public bool Invoke(long id, int actionIndex)
    {
        StatusAction action;
        lock (this.gate)
        {
            this.Prune();
            var m = this.messages.FirstOrDefault(x => x.Id == id);
            if (m is null || actionIndex < 0 || actionIndex >= m.Actions.Count)
            {
                return false;
            }

            action = m.Actions[actionIndex];
            this.messages.Remove(m);
        }

        action.Run();
        return true;
    }

    private void Prune()
    {
        var now = this.clock();
        this.messages.RemoveAll(m => m.ExpiresAt is { } e && e <= now);
    }
}
