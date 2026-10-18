namespace AIVoiceActing.UI.State;

using System.Globalization;
using System.Text;
using AIVoiceActing.Ports;

/// <summary>One engine's line in the report.</summary>
public sealed record DiagnosticsEngine(string Name, EngineStatus? Status, bool Installed);

/// <summary>The settings that change how fast and where speech is made; none identifies the player.</summary>
/// <param name="PrecisionChoice">The voice file size asked for under Advanced (auto, fp32, fp16, q4f16).</param>
/// <param name="PrecisionInUse">Voice files and place in use right now (for example "q4f16/gpu"); empty when no engine is loaded.</param>
public sealed record DiagnosticsFlags(
    string PrecisionChoice,
    string PrecisionInUse,
    bool StreamSentences,
    bool LevelVoiceLoudness,
    bool DirectorEnabled,
    bool MixVoices,
    int CpuThreads);

/// <summary>Everything the diagnostics text is written from.</summary>
public sealed record DiagnosticsInput(
    string PluginVersion,
    int ConfigVersion,
    string Quality,
    DeviceSettings Device,
    string PrimaryEngine,
    IReadOnlyList<DiagnosticsEngine> Engines,
    DeviceReport? Report,
    bool Downloading,
    long ModelsFolderBytes,
    long FreeDiskBytes,
    int QueueDepth,
    long ProcessMemoryBytes,
    DiagnosticsFlags? Flags = null,
    IReadOnlyList<LineTiming>? RecentLines = null);

/// <summary>
/// Builds the plain-text report the player can copy into a bug report. Reasons are the ports' own
/// words. It names no folders, character names or line text: nothing here identifies the player.
/// </summary>
public static class DiagnosticsReport
{
    public static string Build(DiagnosticsInput i)
    {
        var sb = new StringBuilder();
        sb.AppendLine("AI Voice Acting diagnostics");
        sb.AppendLine($"Plugin version: {i.PluginVersion}   Settings version: {i.ConfigVersion}");
        sb.AppendLine($"Voice quality: {i.Quality}");
        sb.AppendLine($"Main engine: {i.PrimaryEngine}");
        sb.AppendLine(
            $"Processor setting: {i.Device.Mode}   Preference: {i.Device.GpuPreference}   " +
            $"Background load: {i.Device.CpuImpact}   Graphics runtime: {i.Device.Backend}/{i.Device.Api}   " +
            $"Crash guard tripped: {(i.Device.GpuDisabledByCrashGuard ? "yes" : "no")}");

        if (i.Flags is { } flags)
        {
            sb.AppendLine(
                $"Voice files: asked for {flags.PrecisionChoice}, in use {(string.IsNullOrWhiteSpace(flags.PrecisionInUse) ? "not loaded" : flags.PrecisionInUse)}   " +
                $"Background load threads: {flags.CpuThreads}");
            sb.AppendLine(
                $"Read sentence by sentence: {YesNo(flags.StreamSentences)}   Even out loudness: {YesNo(flags.LevelVoiceLoudness)}   " +
                $"Emotion detection: {YesNo(flags.DirectorEnabled)}   Unique voice per character: {YesNo(flags.MixVoices)}");
        }

        sb.AppendLine();
        sb.AppendLine("Engines");
        foreach (var engine in i.Engines)
        {
            sb.AppendLine(EngineLine(engine));
        }

        sb.AppendLine();
        sb.AppendLine("Graphics cards (reported by the system)");
        if (i.Report is null)
        {
            sb.AppendLine("  Not checked yet.");
        }
        else
        {
            var r = i.Report;
            sb.AppendLine($"  Platform: {r.Platform}   Graphics runtime available: {YesNo(r.WebGpuAvailable)}   DirectML available: {YesNo(r.DirectMlAvailable)}");
            foreach (var adapter in r.Adapters)
            {
                sb.AppendLine($"  {DeviceViewModel.AdapterLine(adapter)}");
            }

            if (r.Adapters.Count == 0)
            {
                sb.AppendLine("  None found.");
            }

            if (!string.IsNullOrWhiteSpace(r.Reason))
            {
                sb.AppendLine($"  Note: {r.Reason.Trim()}");
            }
        }

        sb.AppendLine();
        sb.AppendLine("Resources");
        sb.AppendLine(
            $"  Models folder size: {Labels.FileSize(i.ModelsFolderBytes)}   Free space: " +
            (i.FreeDiskBytes == long.MaxValue ? "unknown" : Labels.FileSize(i.FreeDiskBytes)));
        sb.AppendLine($"  Download running: {YesNo(i.Downloading)}   Lines queued: {i.QueueDepth}   Game memory: {Labels.FileSize(i.ProcessMemoryBytes)}");

        if (i.RecentLines is { Count: > 0 } lines)
        {
            sb.AppendLine();
            sb.AppendLine("Recent lines (newest first; times in seconds, no text or names)");
            foreach (var line in lines)
            {
                sb.AppendLine(LineSummary(line));
            }
        }

        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// One line's stage timings, for example
    /// <c>#12 3 chunks, turbo gpu q4f16: first audio 6.4, sound 6.8 [queue 0.0 ref 3.1 (rendered) enc 0.8 gate 0.0 prefill 0.2 decoder 0.9 over 4 calls device 0.4]
    /// LM 156 tok 4.9 (p50 21 ms, p95 63 ms), render 6.1, speech 7.2, underruns 1 (2.4 silence)</c>.
    /// A stage that was not measured is left out.
    /// </summary>
    public static string LineSummary(LineTiming t)
    {
        var sb = new StringBuilder();
        sb.Append(CultureInfo.InvariantCulture, $"  #{t.Id} {t.Chunks} {(t.Chunks == 1 ? "chunk" : "chunks")}");
        if (!string.IsNullOrWhiteSpace(t.Engine))
        {
            sb.Append(CultureInfo.InvariantCulture, $", {t.Engine}");
        }

        sb.Append(t.Streamed ? ", streamed" : string.Empty);
        sb.Append(':');
        if (t.FirstAudioMs >= 0)
        {
            sb.Append(CultureInfo.InvariantCulture, $" first audio {Sec(t.FirstAudioMs)}");
        }

        if (t.StartToSoundMs >= 0)
        {
            sb.Append(CultureInfo.InvariantCulture, $", sound {Sec(t.StartToSoundMs)}");
        }

        sb.Append(CultureInfo.InvariantCulture, $" [queue {Sec(t.QueueWaitMs)} voice {Sec(t.ProfileMs)} plan {Sec(t.PlanMs)}");
        if (t.ReferenceKind.Length > 0 || t.ReferencePrepMs > 0)
        {
            sb.Append(CultureInfo.InvariantCulture, $" ref {Sec(t.ReferencePrepMs)}{(t.ReferenceKind.Length > 0 ? $" ({t.ReferenceKind})" : string.Empty)}");
        }

        if (t.LmTokens > 0 || t.EncoderMs > 0)
        {
            sb.Append(CultureInfo.InvariantCulture, $" enc {Sec(t.EncoderMs)} gate {Sec(t.GateWaitMs)} prefill {Sec(t.PrefillMs)}");
        }

        if (t.Windows > 0)
        {
            sb.Append(CultureInfo.InvariantCulture, $" decoder {Sec(t.DecoderMsTotal)} over {t.Windows} {(t.Windows == 1 ? "call" : "calls")}");
        }

        if (t.DeviceOpenMs >= 0)
        {
            sb.Append(CultureInfo.InvariantCulture, $" device {Sec(t.DeviceOpenMs)}");
        }

        sb.Append(']');
        if (t.LmTokens > 0)
        {
            sb.Append(CultureInfo.InvariantCulture, $" LM {t.LmTokens} tok {Sec(t.LmMs)} (p50 {t.LmMsPerTokenP50} ms, p95 {t.LmMsPerTokenP95} ms)");
        }

        sb.Append(CultureInfo.InvariantCulture, $", render {Sec(t.TotalRenderMs)}, speech {Sec(t.AudioMs)}");
        sb.Append(CultureInfo.InvariantCulture, $", underruns {t.UnderrunCount}");
        if (t.UnderrunCount > 0)
        {
            sb.Append(CultureInfo.InvariantCulture, $" ({Sec(t.SilenceMs)} silence)");
        }

        return sb.ToString();
    }

    private static string Sec(long ms) => (ms / 1000.0).ToString("0.0", CultureInfo.InvariantCulture);

    private static string EngineLine(DiagnosticsEngine engine)
    {
        if (engine.Status is not { } s)
        {
            return $"  {engine.Name}: {(engine.Installed ? "installed, not running" : "not installed")}";
        }

        var where = s.ActiveDevice.IsGpu
            ? $"graphics card{(string.IsNullOrWhiteSpace(s.ActiveDevice.Adapter) ? string.Empty : " (" + s.ActiveDevice.Adapter.Trim() + ")")}"
            : "processor";
        var line = $"  {engine.Name}: {s.State}, running on the {where}, asked for {s.RequestedDevice}";
        if (!string.IsNullOrWhiteSpace(s.Reason))
        {
            line += $". {s.Reason.Trim()}";
        }

        return s.CanRetryGpu ? line + " (A retry on the graphics card is possible.)" : line;
    }

    private static string YesNo(bool value) => value ? "yes" : "no";
}
