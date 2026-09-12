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
    private readonly ILogSink? log;
    private readonly Channel<SpeechItem> channel =
        Channel.CreateUnbounded<SpeechItem>(new UnboundedChannelOptions { SingleReader = true });
    private readonly CancellationTokenSource disposal = new();
    private readonly Task worker;
    private bool disposed;

    public PlaybackSpeechQueue(IAudioSink sink, Func<float>? volumeFactory = null, ILogSink? log = null)
    {
        this.sink = sink ?? throw new ArgumentNullException(nameof(sink));
        this.volume = volumeFactory ?? (() => 1f);
        this.log = log;
        this.worker = Task.Run(this.PlayLoopAsync);
    }

    public void Enqueue(SpeechItem item)
    {
        if (item is null || this.disposed)
        {
            return;
        }

        this.channel.Writer.TryWrite(item);
    }

    /// <summary>Stops the item currently being spoken; queued items are kept.</summary>
    public void CancelCurrent() => this.sink.Cancel();

    /// <summary>
    /// Clears all queued items, including any current playback. Bound to the channel
    /// backlog: an item the worker has already dequeued may still complete its play —
    /// the in-flight stop comes from <see cref="IAudioSink.Cancel"/>, and the production
    /// caller of Clear during teardown is <see cref="Dispose"/>, which then stops the
    /// worker (bounded 2 s wait).
    /// </summary>
    public void Clear()
    {
        this.sink.Cancel();
        while (this.channel.Reader.TryRead(out _))
        {
        }
    }

    private async Task PlayLoopAsync()
    {
        try
        {
            while (await this.channel.Reader.WaitToReadAsync(this.disposal.Token).ConfigureAwait(false))
            {
                while (this.channel.Reader.TryRead(out var item))
                {
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
