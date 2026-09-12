namespace AIVoiceActing.Tests.Mock;

using AIVoiceActing.Ports;

/// <summary>
/// Fake IAudioSink (census fake pattern): records play calls in order and lets each test
/// decide when a play completes. Play invokes <see cref="OnPlay"/> (default: returns
/// immediately); <see cref="Cancel"/> completes <see cref="CancelSignal"/> so plays can
/// block on it exactly like the real sink blocks on device playback until Cancel().
/// </summary>
public sealed class FakeAudioSink : IAudioSink
{
    private readonly object gate = new();

    /// <summary>Overridable play behavior: (audio, volume, cancelSignal) → completion.</summary>
    public Func<SynthesisResult, float, Task, Task>? OnPlay { get; set; }

    public List<(SynthesisResult Audio, float Volume)> Played { get; } = [];

    public TaskCompletionSource CancelSignal { get; private set; } = NewSignal();

    public int CancelCalls { get; private set; }

    public bool IsSupported { get; set; } = true;

    public void Play(SynthesisResult audio, float volume)
    {
        lock (this.gate)
        {
            this.Played.Add((audio, volume));
            this.CancelSignal = NewSignal();
        }

        var signal = this.CancelSignal;
        if (this.OnPlay is { } behavior)
        {
            behavior(audio, volume, signal.Task).Wait();
        }
    }

    public void Cancel()
    {
        this.CancelCalls++;
        this.CancelSignal.TrySetResult();
    }

    public void Flush() => this.Cancel();

    private static TaskCompletionSource NewSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);
}
