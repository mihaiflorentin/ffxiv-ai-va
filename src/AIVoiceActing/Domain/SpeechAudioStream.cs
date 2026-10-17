namespace AIVoiceActing.Domain;

/// <summary>
/// Thread-safe single-producer, single-consumer PCM buffer for a line that is still being synthesized:
/// the synthesizer appends chunks as they decode, the playback side reads them as the device asks.
/// <see cref="Cancel"/> is how playback tells the producer to stop (the player skipped the line); the
/// producer watches <see cref="Cancelled"/>. Unread samples are dropped on cancel.
/// </summary>
public sealed class SpeechAudioStream
{
    private readonly object gate = new();
    private readonly CancellationTokenSource cancelled = new();
    private float[] buffer = new float[1 << 15];
    private int readPosition;
    private int writePosition;
    private bool completed;
    private long totalAppended;

    public SpeechAudioStream(int sampleRate)
    {
        if (sampleRate <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sampleRate));
        }

        this.SampleRate = sampleRate;
    }

    public int SampleRate { get; }

    /// <summary>Signalled when the consumer gave up on this stream.</summary>
    public CancellationToken Cancelled => this.cancelled.Token;

    public bool IsCancelled => this.cancelled.IsCancellationRequested;

    /// <summary>Total samples ever appended (read or not).</summary>
    public long TotalAppended
    {
        get
        {
            lock (this.gate)
            {
                return this.totalAppended;
            }
        }
    }

    /// <summary>Samples appended but not read yet.</summary>
    public int Buffered
    {
        get
        {
            lock (this.gate)
            {
                return this.writePosition - this.readPosition;
            }
        }
    }

    /// <summary>The producer finished (or failed); no more samples will be appended.</summary>
    public bool IsCompleted
    {
        get
        {
            lock (this.gate)
            {
                return this.completed;
            }
        }
    }

    /// <summary>Completed and fully read, or cancelled.</summary>
    public bool IsFinished
    {
        get
        {
            lock (this.gate)
            {
                return this.cancelled.IsCancellationRequested || (this.completed && this.writePosition == this.readPosition);
            }
        }
    }

    /// <summary>Adds samples; false (and nothing added) once the stream is cancelled or completed.</summary>
    public bool Append(ReadOnlySpan<float> samples)
    {
        lock (this.gate)
        {
            if (this.completed || this.cancelled.IsCancellationRequested)
            {
                return false;
            }

            if (samples.IsEmpty)
            {
                return true;
            }

            var unread = this.writePosition - this.readPosition;
            if (this.readPosition > 0 && this.writePosition + samples.Length > this.buffer.Length)
            {
                Array.Copy(this.buffer, this.readPosition, this.buffer, 0, unread);
                this.readPosition = 0;
                this.writePosition = unread;
            }

            if (this.writePosition + samples.Length > this.buffer.Length)
            {
                Array.Resize(ref this.buffer, Math.Max(this.buffer.Length * 2, this.writePosition + samples.Length));
            }

            samples.CopyTo(this.buffer.AsSpan(this.writePosition));
            this.writePosition += samples.Length;
            this.totalAppended += samples.Length;
            Monitor.PulseAll(this.gate);
            return true;
        }
    }

    /// <summary>The producer is done; readers drain what is buffered and then see <see cref="IsFinished"/>.</summary>
    public void Complete()
    {
        lock (this.gate)
        {
            this.completed = true;
            Monitor.PulseAll(this.gate);
        }
    }

    /// <summary>The consumer abandons the stream: pending samples are dropped, producers are signalled.</summary>
    public void Cancel()
    {
        lock (this.gate)
        {
            this.readPosition = this.writePosition = 0;
            this.completed = true;
            Monitor.PulseAll(this.gate);
        }

        this.cancelled.Cancel();
    }

    /// <summary>Copies up to <paramref name="destination"/>.Length unread samples; never blocks.</summary>
    public int Read(Span<float> destination)
    {
        lock (this.gate)
        {
            var count = Math.Min(destination.Length, this.writePosition - this.readPosition);
            if (count > 0)
            {
                this.buffer.AsSpan(this.readPosition, count).CopyTo(destination);
                this.readPosition += count;
            }

            return count;
        }
    }

    /// <summary>Copies up to <paramref name="destination"/>.Length unread samples without consuming them.</summary>
    public int Peek(Span<float> destination)
    {
        lock (this.gate)
        {
            var count = Math.Min(destination.Length, this.writePosition - this.readPosition);
            this.buffer.AsSpan(this.readPosition, count).CopyTo(destination);
            return count;
        }
    }

    /// <summary>Blocks until samples are buffered, the stream ended, or the timeout passed.</summary>
    public bool WaitForData(int timeoutMs)
    {
        lock (this.gate)
        {
            var deadline = Environment.TickCount64 + timeoutMs;
            while (this.writePosition == this.readPosition && !this.completed)
            {
                var left = deadline - Environment.TickCount64;
                if (left <= 0)
                {
                    return false;
                }

                Monitor.Wait(this.gate, (int)Math.Min(left, int.MaxValue));
            }

            return true;
        }
    }

    /// <summary>Waits for the producer to finish and returns everything unread (for sinks that cannot stream).</summary>
    public float[] ReadAllBlocking(CancellationToken cancellationToken = default)
    {
        var all = new List<float>();
        var chunk = new float[4096];
        while (!cancellationToken.IsCancellationRequested)
        {
            var n = this.Read(chunk);
            if (n > 0)
            {
                all.AddRange(chunk.AsSpan(0, n).ToArray());
                continue;
            }

            if (this.IsFinished)
            {
                break;
            }

            this.WaitForData(200);
        }

        return [.. all];
    }
}
