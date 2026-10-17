namespace AIVoiceActing.UI.State;

using System.Text;
using AIVoiceActing.Ports;

/// <summary>One engine's line in the report.</summary>
public sealed record DiagnosticsEngine(string Name, EngineStatus? Status, bool Installed);

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
    long ProcessMemoryBytes);

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
        return sb.ToString().TrimEnd();
    }

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
