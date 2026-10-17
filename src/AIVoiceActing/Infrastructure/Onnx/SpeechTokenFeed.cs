namespace AIVoiceActing.Infrastructure.Onnx;

using System.Runtime.ExceptionServices;
using AIVoiceActing.Domain;

/// <summary>
/// Hand-over point between the language model (producer: adds speech tokens as it picks them) and the
/// chunked decoder (consumer: asks the <see cref="ChunkSchedule"/> for its next window). The producer
/// ends the stream with <see cref="Finish"/> (which also appends the closing silence tokens) or
/// <see cref="Fail"/>; a failure reaches the consumer from its next wait, so it never decodes on after
/// the model broke.
/// </summary>
internal sealed class SpeechTokenFeed
{
    private readonly object gate = new();
    private readonly List<long> tokens = [];
    private int speechCount;
    private bool finished;
    private Exception? failure;

    /// <summary>Tokens in the stream so far (speech plus, once finished, the silence suffix).</summary>
    public int Count
    {
        get
        {
            lock (this.gate)
            {
                return this.tokens.Count;
            }
        }
    }

    /// <summary>Speech tokens only (no silence suffix); complete once <see cref="IsFinished"/>.</summary>
    public long[] SpeechTokens()
    {
        lock (this.gate)
        {
            return [.. this.tokens.Take(this.speechCount)];
        }
    }

    public bool IsFinished
    {
        get
        {
            lock (this.gate)
            {
                return this.finished;
            }
        }
    }

    public void Add(long token)
    {
        lock (this.gate)
        {
            if (this.finished)
            {
                return;
            }

            this.tokens.Add(token);
            this.speechCount = this.tokens.Count;
            Monitor.PulseAll(this.gate);
        }
    }

    /// <summary>No more speech tokens; <paramref name="suffix"/> (the closing silence) joins the stream.</summary>
    public void Finish(IEnumerable<long> suffix)
    {
        lock (this.gate)
        {
            if (this.finished)
            {
                return;
            }

            this.tokens.AddRange(suffix);
            this.finished = true;
            Monitor.PulseAll(this.gate);
        }
    }

    public void Fail(Exception error)
    {
        lock (this.gate)
        {
            this.failure ??= error;
            Monitor.PulseAll(this.gate);
        }
    }

    /// <summary>Tokens <paramref name="start"/> up to (not including) <paramref name="end"/>.</summary>
    public long[] Slice(int start, int end)
    {
        lock (this.gate)
        {
            return [.. this.tokens.Skip(start).Take(end - start)];
        }
    }

    /// <summary>
    /// Blocks until the schedule yields a window, or returns null once the stream is finished and fully
    /// handed out. Rethrows the producer's failure (original exception type) and honours cancellation.
    /// </summary>
    public ChunkPlan? WaitForPlan(ChunkSchedule schedule, CancellationToken cancellationToken)
    {
        using var wake = cancellationToken.Register(() =>
        {
            lock (this.gate)
            {
                Monitor.PulseAll(this.gate);
            }
        });
        lock (this.gate)
        {
            while (true)
            {
                if (this.failure is { } error)
                {
                    ExceptionDispatchInfo.Capture(error).Throw();
                }

                cancellationToken.ThrowIfCancellationRequested();
                var plan = schedule.TryTake(this.tokens.Count, this.finished);
                if (plan is not null)
                {
                    return plan;
                }

                if (this.finished)
                {
                    return null;
                }

                Monitor.Wait(this.gate, 250);
            }
        }
    }
}
