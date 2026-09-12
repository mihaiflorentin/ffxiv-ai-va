namespace AIVoiceActing.Tests.Mock;

using AIVoiceActing.Ports;

/// <summary>Fake ILogSink (census fake pattern): records every call; Func fields could return defaults but recording is the point here.</summary>
public sealed class FakeLogSink : ILogSink
{
    public sealed record Entry(string Level, string Message, Exception? Exception);

    private readonly object gate = new();

    public List<Entry> Calls { get; } = [];

    public IReadOnlyList<Entry> Snapshot()
    {
        lock (this.gate)
        {
            return [.. this.Calls];
        }
    }

    public void Info(string msg) => this.Record("Info", msg, null);

    public void Warn(string msg) => this.Record("Warn", msg, null);

    public void Error(string msg, Exception? ex = null) => this.Record("Error", msg, ex);

    private void Record(string level, string message, Exception? exception)
    {
        lock (this.gate)
        {
            this.Calls.Add(new Entry(level, message, exception));
        }
    }
}
