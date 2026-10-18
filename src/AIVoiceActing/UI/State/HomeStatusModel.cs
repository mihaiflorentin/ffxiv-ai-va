namespace AIVoiceActing.UI.State;

using AIVoiceActing.Ports;

/// <summary>What Home knows about the engine, as plain facts.</summary>
public sealed record HomeStatusInput(
    bool SpeechEnabled,
    bool EngineReady,
    bool Warming,
    bool NeedsDownload,
    string EngineName,
    bool OnGraphicsCard,
    bool DownloadActive,
    int DownloadFileNumber,
    int DownloadFileCount,
    string NotReadyReason,
    string? ErrorTitle);

public sealed record HomeStatus(Severity Severity, string Text);

/// <summary>
/// The one status line at the top of Home. Plain words, no engine internals: "Ready — Kokoro on
/// your processor", "Not set up yet", "Downloading 2 of 5…". The severity picks the pill's icon
/// and colour, so the state never rides on colour alone.
/// </summary>
public static class HomeStatusModel
{
    public static HomeStatus Describe(HomeStatusInput s)
    {
        if (s.ErrorTitle is { Length: > 0 } error)
        {
            return new HomeStatus(Severity.Error, error);
        }

        if (s.DownloadActive)
        {
            var count = Math.Max(s.DownloadFileCount, s.DownloadFileNumber);
            return new HomeStatus(Severity.Info, $"Downloading {s.DownloadFileNumber} of {count}…");
        }

        if (s.Warming)
        {
            return new HomeStatus(Severity.Info, $"Loading {s.EngineName}…");
        }

        if (!s.EngineReady)
        {
            return s.NeedsDownload || string.IsNullOrWhiteSpace(s.NotReadyReason)
                ? new HomeStatus(Severity.Warning, "Not set up yet")
                : new HomeStatus(Severity.Warning, $"Not ready — {s.NotReadyReason.TrimEnd('.')}");
        }

        var where = s.OnGraphicsCard ? "your graphics card" : "your processor";
        return s.SpeechEnabled
            ? new HomeStatus(Severity.Success, $"Ready — {s.EngineName} on {where}")
            : new HomeStatus(Severity.Warning, $"Speech is off — {s.EngineName} is ready on {where}");
    }

    /// <summary>True when the engine runs on the graphics card under the player's Processor choice.</summary>
    public static bool OnGraphicsCard(string deviceMode, EngineInfo engine)
    {
        if (!engine.SupportsGpu || string.Equals(deviceMode, DeviceSettings.ModeCpu, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return string.Equals(deviceMode, DeviceSettings.ModeGpu, StringComparison.OrdinalIgnoreCase)
               || engine.DefaultDevice == ComputeDevice.Gpu;
    }
}
