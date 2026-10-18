namespace AIVoiceActing.Domain;

/// <summary>Where the language model of one line runs.</summary>
public enum LmDevice
{
    Gpu,
    Cpu,
}

/// <summary>Knobs of <see cref="LmDeviceChooser"/>; the times are in milliseconds per speech token (real time is 40).</summary>
/// <param name="GpuSlowMs">Above this the graphics card is too slow to be trusted with a line (a speech token must come in under 40 ms to keep up), and the processor is worth trying.</param>
/// <param name="GpuGoodEnoughMs">At or below this the graphics card is left alone however fast the processor is: it keeps up with room to spare and the game gets the processor's cores.</param>
/// <param name="SwitchToCpuRatio">The graphics card must be this many times slower than the processor before a line moves to the processor.</param>
/// <param name="SwitchToGpuRatio">The graphics card is preferred back as soon as it is within this many times the processor's time. Lower than <see cref="SwitchToCpuRatio"/> so the choice does not flap.</param>
/// <param name="Alpha">Weight of the newest line in the running average.</param>
/// <param name="MinSteps">A line with fewer measured steps says nothing about the pace.</param>
/// <param name="ProbeEveryLines">While on the processor, the graphics card is tried again after this many lines.</param>
/// <param name="ProbeAfterMs">Or after this long since it last ran a line.</param>
/// <param name="ShortLineChars">A try is saved for a line this short, to cost little if the card is still slow, unless it is long overdue.</param>
public sealed record LmDeviceOptions(
    double GpuSlowMs = 36.0,
    double GpuGoodEnoughMs = 30.0,
    double SwitchToCpuRatio = 1.25,
    double SwitchToGpuRatio = 1.10,
    double Alpha = 0.4,
    int MinSteps = 8,
    int ProbeEveryLines = 6,
    long ProbeAfterMs = 5 * 60 * 1000,
    int ShortLineChars = 100)
{
    public static LmDeviceOptions Default { get; } = new();
}

/// <summary>
/// Picks, line by line, which device runs the language model when both a graphics card and the processor can. The card
/// shared with a game can run slower than real time while the processor, with a few threads, is several times quicker,
/// so the choice follows what was measured: a running average of the median step time of each device. The card is the
/// default and keeps the line unless it is slow in absolute terms and clearly slower than the processor; a band between
/// the two switching ratios keeps the choice from flapping. While on the processor the card is tried again now and then
/// (a short line, every few lines or minutes), so the choice returns by itself when the game stops loading the card. The
/// choice is made once per line, before it starts: nothing here moves a line that is running. Pure and clock-injected;
/// the synthesizer calls it from its one-line-at-a-time gate, and the UI reads the numbers from another thread.
/// </summary>
public sealed class LmDeviceChooser
{
    private readonly object gate = new();
    private readonly LmDeviceOptions options;
    private readonly Func<long> nowMs;
    private double gpuMs = double.NaN;
    private double cpuMs = double.NaN;
    private LmDevice preferred = LmDevice.Gpu;
    private int linesOnCpu;
    private long lastGpuAt;
    private bool probing;

    public LmDeviceChooser(LmDeviceOptions? options = null, Func<long>? nowMs = null)
    {
        this.options = options ?? LmDeviceOptions.Default;
        this.nowMs = nowMs ?? (() => Environment.TickCount64);
        this.lastGpuAt = this.nowMs();
    }

    /// <summary>The device the lines go to for now (the last line may have been a try of the other one).</summary>
    public LmDevice Preferred
    {
        get
        {
            lock (this.gate)
            {
                return this.preferred;
            }
        }
    }

    /// <summary>Average ms per speech token on the graphics card; 0 until a line was measured there.</summary>
    public double GpuMsPerToken
    {
        get
        {
            lock (this.gate)
            {
                return double.IsNaN(this.gpuMs) ? 0 : this.gpuMs;
            }
        }
    }

    /// <summary>Average ms per speech token on the processor; 0 until a line was measured there.</summary>
    public double CpuMsPerToken
    {
        get
        {
            lock (this.gate)
            {
                return double.IsNaN(this.cpuMs) ? 0 : this.cpuMs;
            }
        }
    }

    /// <summary>The measured time of the preferred device; 0 when it has not been measured.</summary>
    public double PreferredMsPerToken => this.Preferred == LmDevice.Cpu ? this.CpuMsPerToken : this.GpuMsPerToken;

    /// <summary>
    /// The graphics card has been measured too slow, so a processor session is worth building (it costs memory, so
    /// nothing is built while the card keeps up).
    /// </summary>
    public bool WantsCpu
    {
        get
        {
            lock (this.gate)
            {
                return !double.IsNaN(this.gpuMs) && this.gpuMs > this.options.GpuSlowMs;
            }
        }
    }

    /// <summary>
    /// The device for the next line. <paramref name="cpuReady"/> says whether a processor session exists; without one
    /// every line is the card's. The result can differ from <see cref="Preferred"/> for a line that tries the card again.
    /// </summary>
    public LmDevice Choose(int lineChars, bool cpuReady)
    {
        lock (this.gate)
        {
            this.probing = false;
            if (!cpuReady)
            {
                this.preferred = LmDevice.Gpu;
                return LmDevice.Gpu;
            }

            this.preferred = this.Decide();
            if (this.preferred == LmDevice.Gpu)
            {
                return LmDevice.Gpu;
            }

            if (this.ProbeDue(lineChars))
            {
                this.probing = true;
                return LmDevice.Gpu;
            }

            this.linesOnCpu++;
            return LmDevice.Cpu;
        }
    }

    /// <summary>A line ran on <paramref name="device"/> and its median step took <paramref name="medianStepMs"/> over <paramref name="steps"/> steps.</summary>
    public void Record(LmDevice device, double medianStepMs, int steps)
    {
        if (steps < this.options.MinSteps || medianStepMs <= 0 || double.IsNaN(medianStepMs))
        {
            return;
        }

        lock (this.gate)
        {
            if (device == LmDevice.Gpu)
            {
                // A try of the card says how it is now: it replaces what the average remembered of the busy times.
                this.gpuMs = double.IsNaN(this.gpuMs) || this.probing ? medianStepMs : Blend(this.gpuMs, medianStepMs, this.options.Alpha);
                this.lastGpuAt = this.nowMs();
                this.linesOnCpu = 0;
            }
            else
            {
                this.cpuMs = double.IsNaN(this.cpuMs) ? medianStepMs : Blend(this.cpuMs, medianStepMs, this.options.Alpha);
            }

            this.probing = false;
        }
    }

    private LmDevice Decide()
    {
        if (double.IsNaN(this.gpuMs))
        {
            return LmDevice.Gpu;
        }

        if (double.IsNaN(this.cpuMs))
        {
            // The processor has never run a line: try it once the card is too slow to trust.
            return this.gpuMs > this.options.GpuSlowMs ? LmDevice.Cpu : LmDevice.Gpu;
        }

        if (this.preferred == LmDevice.Gpu)
        {
            var leave = this.gpuMs > this.options.GpuGoodEnoughMs && this.gpuMs > this.cpuMs * this.options.SwitchToCpuRatio;
            return leave ? LmDevice.Cpu : LmDevice.Gpu;
        }

        return this.gpuMs <= this.cpuMs * this.options.SwitchToGpuRatio ? LmDevice.Gpu : LmDevice.Cpu;
    }

    private bool ProbeDue(int lineChars)
    {
        var elapsed = this.nowMs() - this.lastGpuAt;
        var due = this.linesOnCpu >= this.options.ProbeEveryLines || elapsed >= this.options.ProbeAfterMs;
        if (!due)
        {
            return false;
        }

        // Wait for a short line unless the card has been left alone for twice as long as planned.
        var overdue = this.linesOnCpu >= this.options.ProbeEveryLines * 2 || elapsed >= this.options.ProbeAfterMs * 2;
        return overdue || lineChars <= this.options.ShortLineChars;
    }

    private static double Blend(double old, double fresh, double alpha) => (alpha * fresh) + ((1.0 - alpha) * old);
}
