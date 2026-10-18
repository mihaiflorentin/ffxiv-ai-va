namespace AIVoiceActing.Infrastructure.Diagnostics;

using AIVoiceActing.Ports;

/// <summary>In-memory <see cref="ILineTimingLog"/>: the last <see cref="Capacity"/> lines and nothing else (not saved, never grows).</summary>
public sealed class RingLineTimingLog : ILineTimingLog
{
    public const int DefaultCapacity = 20;

    private readonly object gate = new();
    private readonly List<LineTiming> entries = [];

    public RingLineTimingLog(int capacity = DefaultCapacity)
    {
        this.Capacity = Math.Max(1, capacity);
    }

    public int Capacity { get; }

    public void Record(LineTiming timing)
    {
        ArgumentNullException.ThrowIfNull(timing);
        lock (this.gate)
        {
            var index = this.entries.FindIndex(e => e.Id == timing.Id);
            if (index >= 0)
            {
                this.entries[index] = timing;
                return;
            }

            this.entries.Add(timing);
            if (this.entries.Count > this.Capacity)
            {
                this.entries.RemoveAt(0);
            }
        }
    }

    public IReadOnlyList<LineTiming> Recent(int max)
    {
        lock (this.gate)
        {
            return [.. Enumerable.Reverse(this.entries).Take(Math.Max(0, max))];
        }
    }
}
