namespace AIVoiceActing.Tests.Mock;

using AIVoiceActing.Ports;

/// <summary>Fake ISpeechQueue (census fake pattern): records enqueued items and control calls.</summary>
public sealed class FakeSpeechQueue : ISpeechQueue
{
    public List<SpeechItem> Enqueued { get; } = [];

    public int CancelCurrentCalls { get; private set; }

    public int ClearCalls { get; private set; }

    public void Enqueue(SpeechItem item) => this.Enqueued.Add(item);

    public void CancelCurrent() => this.CancelCurrentCalls++;

    public void Clear() => this.ClearCalls++;

    public int Depth => this.Enqueued.Count;
}
