namespace AIVoiceActing.Domain;

/// <summary>
/// Counts what a sound device saw of a live line: when real sound first arrived, and how often and how long it
/// asked for more than was ready. One run of silence while the line is still being made is one underrun, however
/// many device reads it spans. The tail of a finished line is not an underrun. Fed from the device callback thread.
/// </summary>
public sealed class PlaybackMeter(int sampleRate, Func<long>? clock = null)
{
    private readonly object gate = new();
    private readonly Func<long> clock = clock ?? (() => Environment.TickCount64);
    private long silentSamples;
    private int underruns;
    private long firstSoundAtTicks;
    private bool inUnderrun;

    public int UnderrunCount
    {
        get
        {
            lock (this.gate)
            {
                return this.underruns;
            }
        }
    }

    public long SilenceMs
    {
        get
        {
            lock (this.gate)
            {
                return sampleRate <= 0 ? 0 : this.silentSamples * 1000 / sampleRate;
            }
        }
    }

    /// <summary><c>Environment.TickCount64</c> of the first read that carried real sound; 0 before that.</summary>
    public long FirstSoundAtTicks
    {
        get
        {
            lock (this.gate)
            {
                return this.firstSoundAtTicks;
            }
        }
    }

    /// <summary>One device read: it wanted <paramref name="requested"/> samples and got <paramref name="delivered"/>.</summary>
    public void OnRead(int requested, int delivered, bool sourceFinished)
    {
        lock (this.gate)
        {
            if (delivered > 0 && this.firstSoundAtTicks == 0)
            {
                this.firstSoundAtTicks = this.clock();
            }

            // Before the first sound there is nothing to run dry of, and a finished source has nothing more to give.
            if (this.firstSoundAtTicks == 0 || sourceFinished || delivered >= requested)
            {
                this.inUnderrun = false;
                return;
            }

            this.silentSamples += requested - delivered;
            if (!this.inUnderrun)
            {
                this.inUnderrun = true;
                this.underruns++;
            }
        }
    }
}
