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

    /// <summary>What the next play reports as measured; tests set it.</summary>
    public PlaybackReport? LastPlayback { get; set; }

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

    /// <summary>Audio read from streamed lines, one entry per PlayStream call.</summary>
    public List<float[]> Streamed { get; } = [];

    public List<int> StreamedSampleRates { get; } = [];

    /// <summary>Set to make PlayStream treat the sink as unable to stream (falls back to the whole-line default).</summary>
    public bool StreamingDisabled { get; set; }

    public void PlayStream(IPcmSource source, float volume)
    {
        if (this.StreamingDisabled)
        {
            // Same as the interface default: wait for the whole line, then Play it.
            var all = new List<float>();
            var chunk = new float[1024];
            while (!source.IsFinished)
            {
                var n = source.Read(chunk);
                if (n > 0)
                {
                    all.AddRange(chunk.AsSpan(0, n).ToArray());
                }
                else
                {
                    source.WaitForData(20);
                }
            }

            if (all.Count > 0)
            {
                this.Play(new SynthesisResult([.. all], source.SampleRate), volume);
            }

            return;
        }

        var heard = new List<float>();
        var buffer = new float[1024];
        while (!source.IsFinished)
        {
            var n = source.Read(buffer);
            if (n > 0)
            {
                heard.AddRange(buffer.AsSpan(0, n).ToArray());
            }
            else
            {
                source.WaitForData(20);
            }
        }

        lock (this.gate)
        {
            this.Streamed.Add([.. heard]);
            this.StreamedSampleRates.Add(source.SampleRate);
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
