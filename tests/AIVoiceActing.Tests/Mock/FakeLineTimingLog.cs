namespace AIVoiceActing.Tests.Mock;

using AIVoiceActing.Ports;

/// <summary>Fake ILineTimingLog (census fake pattern): keeps every recorded timing in call order.</summary>
public sealed class FakeLineTimingLog : ILineTimingLog
{
    private readonly object gate = new();

    public List<LineTiming> Recorded { get; } = [];

    public void Record(LineTiming timing)
    {
        lock (this.gate)
        {
            this.Recorded.Add(timing);
        }
    }

    public IReadOnlyList<LineTiming> Recent(int max)
    {
        lock (this.gate)
        {
            return [.. Enumerable.Reverse(this.Recorded).Take(max)];
        }
    }
}
