namespace AIVoiceActing.Infrastructure.Onnx;

using AIVoiceActing.Ports;

/// <summary>
/// Loop breaker for native GPU aborts. A bad driver or graph kills the whole game process from
/// native code (no managed exception, no finally), so the only defence is bookkeeping on disk:
/// a marker file is written BEFORE the first GPU session is created and removed only after the
/// first successful inference. A marker found at the next start means the previous run died in
/// GPU code, so the plugin switches the GPU off (<c>Device.GpuDisabledByCrashGuard</c>) until the
/// user retries. Several scopes may overlap; the marker is removed when the last one completes.
/// </summary>
public sealed class GpuCrashGuard
{
    private readonly object gate = new();
    private readonly ILogSink? log;
    private int openScopes;

    public GpuCrashGuard(string markerPath, ILogSink? log = null)
    {
        this.MarkerPath = markerPath;
        this.log = log;
    }

    public string MarkerPath { get; }

    /// <summary>
    /// Startup check: true when a marker from an earlier process is on disk. Call before any
    /// <see cref="TryBegin"/> in this process; the caller persists the disabled flag and then
    /// calls <see cref="ClearLeftover"/> (flag first, so a failed save keeps the evidence).
    /// </summary>
    public bool HasLeftoverMarker(out string detail)
    {
        lock (this.gate)
        {
            detail = string.Empty;
            if (this.openScopes > 0 || !File.Exists(this.MarkerPath))
            {
                return false;
            }

            try
            {
                detail = File.ReadAllText(this.MarkerPath).Trim();
            }
            catch (Exception)
            {
                // An unreadable marker is still a marker.
            }

            return true;
        }
    }

    /// <summary>
    /// Startup helper: when a leftover marker exists, runs <paramref name="persistDisabled"/> (set the
    /// config flag and save it) and only then removes the marker. A throwing callback keeps the marker,
    /// so the next start trips again. Returns true when the guard tripped.
    /// </summary>
    public bool TripIfLeftover(Action<string> persistDisabled)
    {
        if (!this.HasLeftoverMarker(out var detail))
        {
            return false;
        }

        this.log?.Warn($"A previous run ended inside GPU code ({detail}); turning the GPU off.");
        persistDisabled(detail);
        this.ClearLeftover();
        return true;
    }

    public void ClearLeftover()
    {
        lock (this.gate)
        {
            if (this.openScopes == 0)
            {
                TryDelete(this.MarkerPath);
            }
        }
    }

    /// <summary>
    /// Writes the marker (first scope) and returns a scope the caller completes. False when the marker
    /// cannot be written: without it the loop breaker is blind, so the caller must stay on the CPU.
    /// </summary>
    public bool TryBegin(string detail, out Scope scope)
    {
        lock (this.gate)
        {
            try
            {
                if (this.openScopes == 0)
                {
                    var directory = Path.GetDirectoryName(this.MarkerPath);
                    if (!string.IsNullOrEmpty(directory))
                    {
                        Directory.CreateDirectory(directory);
                    }

                    File.WriteAllText(
                        this.MarkerPath,
                        $"{DateTime.UtcNow:O} pid {Environment.ProcessId}: {detail}");
                }

                this.openScopes++;
                scope = new Scope(this);
                return true;
            }
            catch (Exception ex)
            {
                this.log?.Warn($"GPU crash marker could not be written ({ex.Message}); the GPU stays off.");
                scope = null!;
                return false;
            }
        }
    }

    private void Close(bool succeeded)
    {
        lock (this.gate)
        {
            if (this.openScopes > 0 && --this.openScopes == 0)
            {
                TryDelete(this.MarkerPath);
            }
        }

        this.log?.Info(succeeded ? "GPU session verified; crash marker cleared." : "GPU session failed safely; crash marker cleared.");
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception)
        {
            // Leaving a marker behind only makes the next start cautious.
        }
    }

    /// <summary>One protected GPU operation. A scope that is never completed (the process died) leaves the marker in place.</summary>
    public sealed class Scope
    {
        private readonly GpuCrashGuard owner;
        private int done;

        internal Scope(GpuCrashGuard owner) => this.owner = owner;

        /// <summary>The first inference ran: the GPU path survived.</summary>
        public void Succeeded() => this.Finish(true);

        /// <summary>A managed exception surfaced, so the process is alive: nothing crashed.</summary>
        public void FailedSafely() => this.Finish(false);

        private void Finish(bool ok)
        {
            if (Interlocked.Exchange(ref this.done, 1) == 0)
            {
                this.owner.Close(ok);
            }
        }
    }
}
