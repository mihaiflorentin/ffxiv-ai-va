namespace AIVoiceActing.Tests.Mock;

using AIVoiceActing.Ports;

/// <summary>Fake ISpeechQueue (census fake pattern): records enqueued items and control calls.</summary>
public sealed class FakeSpeechQueue : ISpeechQueue
{
    public List<SpeechItem> Enqueued { get; } = [];

    public int CancelCurrentCalls { get; private set; }

    public int ClearCalls { get; private set; }

    /// <summary>What the overlay would show as playing; tests set it to simulate playback.</summary>
    public SpeechItem? NowPlaying { get; set; }

    public void Enqueue(SpeechItem item)
    {
        // The pipeline's background worker enqueues off the test thread.
        lock (this.Enqueued)
        {
            this.Enqueued.Add(item);
        }
    }

    public void CancelCurrent() => this.CancelCurrentCalls++;

    public void Clear() => this.ClearCalls++;

    public int Depth => this.Enqueued.Count;

    /// <summary>Whether this fake plays streamed items while they fill (tests flip it).</summary>
    public bool SupportsStreaming { get; set; }
}
