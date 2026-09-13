namespace AIVoiceActing.Infrastructure.Audio;

using System.Threading.Channels;
using AIVoiceActing.Domain;
using AIVoiceActing.Ports;

/// <summary>
/// Real speech playback queue (the Step-5 NoopSpeechQueue fallback's replacement): a single
/// background worker pulls items in order and plays each through the audio sink, blocking
/// inside <see cref="IAudioSink.Play"/> until the item finishes or the sink is cancelled —
/// which is how <see cref="CancelCurrent"/> (the game's text-advance / voiced-line
/// courtesy) stops the in-flight line while keeping the backlog, and how <see cref="Clear"/>
/// stops it and drops the backlog (port of TTT's SoundQueue.CancelAllSounds). When the sink
/// cannot play (e.g. non-Windows) items still dequeue and complete so the pipeline's state
/// machine stays identical on every OS — only the audio is skipped. Volume is read live per
/// item (0..2 linear multiplier) so volume commands apply at once.
/// </summary>
public sealed class PlaybackSpeechQueue : ISpeechQueue, IDisposable
{
    private readonly IAudioSink sink;
    private readonly Func<float> volume;
    private readonly Func<long>? staleAfterMsFactory;
    private readonly ILogSink? log;
    private readonly Channel<SpeechItem> channel =
        Channel.CreateUnbounded<SpeechItem>(new UnboundedChannelOptions { SingleReader = true });
    private readonly CancellationTokenSource disposal = new();
    private int depth;
    private readonly Task worker;
    private bool disposed;

    public PlaybackSpeechQueue(
        IAudioSink sink,
        Func<float>? volumeFactory = null,
        ILogSink? log = null,
        Func<long>? staleAfterMsFactory = null)
    {
        this.sink = sink ?? throw new ArgumentNullException(nameof(sink));
        this.volume = volumeFactory ?? (() => 1f);
        this.staleAfterMsFactory = staleAfterMsFactory;
        this.log = log;
        this.worker = Task.Run(this.PlayLoopAsync);
    }

    public void Enqueue(SpeechItem item)
    {
        if (item is null || this.disposed)
        {
            return;
        }

        if (this.channel.Writer.TryWrite(item))
        {
            Interlocked.Increment(ref this.depth);
        }
    }

    public int Depth => Volatile.Read(ref this.depth);


    /// <summary>Stops the item currently being spoken; queued items are kept.</summary>
    public void CancelCurrent() => this.sink.Cancel();

    /// <summary>
    /// Clears all queued items, including any current playback. The backlog is drained
    /// BEFORE cancelling: the worker is blocked inside the in-flight play at that point,
    /// so it cannot race ahead and dequeue an item the caller asked to drop. (Cancel-first
    /// left that window open — the woken worker could pull the next item mid-Clear.) An
    /// item the worker has ALREADY dequeued may still complete its play — the in-flight
    /// stop comes from <see cref="IAudioSink.Cancel"/> — and the production caller of
    /// Clear during teardown is <see cref="Dispose"/>, which then stops the worker
    /// (bounded 2 s wait).
    /// </summary>
    public void Clear()
    {
        while (this.channel.Reader.TryRead(out _))
        {
        }

        this.sink.Cancel();
    }

    private async Task PlayLoopAsync()
    {
        try
        {
            while (await this.channel.Reader.WaitToReadAsync(this.disposal.Token).ConfigureAwait(false))
            {
                while (this.channel.Reader.TryRead(out var item))
                {
                    Interlocked.Decrement(ref this.depth);
                    this.PlayItem(item);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Dispose: stop the loop; in-flight playback was cancelled with the sink.
        }
    }

    private void PlayItem(SpeechItem item)
    {
        var staleAfterMs = this.staleAfterMsFactory?.Invoke() ?? 0;
        if (item.RequestedAtTicks != 0 && staleAfterMs > 0)
        {
            var ageMs = Environment.TickCount64 - item.RequestedAtTicks;
            if (ageMs > staleAfterMs)
            {
                this.log?.Info(
                    $"Dropping stale line for \"{item.Speaker.Key}\" ({ageMs / 1000.0:0}s old; limit {staleAfterMs / 1000.0:0}s).");
                return;
            }
        }

        if (!this.sink.IsSupported)
        {
            // mac / no output device: dequeue without audio so the pipeline keeps flowing.
            return;
        }

        try
        {
            this.sink.Play(item.Audio, Math.Clamp(this.volume(), 0f, 2f));
        }
        catch (Exception ex)
        {
            this.log?.Error($"Playback failed for \"{item.Speaker.Key}\".", ex);
        }
    }

    public void Dispose()
    {
        if (this.disposed)
        {
            return;
        }

        this.disposed = true;
        this.channel.Writer.TryComplete();
        this.Clear();
        this.disposal.Cancel();
        try
        {
            this.worker.Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException)
        {
            // Worker faulted mid-playback: nothing further to do at disposal time.
        }

        this.disposal.Dispose();
    }
}
