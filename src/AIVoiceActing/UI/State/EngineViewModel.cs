namespace AIVoiceActing.UI.State;

using AIVoiceActing.Domain;
using AIVoiceActing.Ports;

/// <summary>What the card of one engine knows. <see cref="Status"/> is null for an engine that is not running right now.</summary>
/// <param name="Installed">Its required files are on disk.</param>
/// <param name="Warming">A load is in progress for this engine.</param>
public sealed record EngineViewInput(string EngineName, bool Installed, bool Warming, EngineStatus? Status);

/// <summary>One word, one line of detail, and the severity that picks the icon and colour.</summary>
public sealed record EngineView(Severity Severity, string Word, string Detail);

/// <summary>
/// Pure table from an engine's state to what its card says: not installed, loading, ready on the
/// graphics card, ready on the processor, fell back (with the port's reason, verbatim), failed.
/// The word carries the state as well as the colour, so it never rides on colour alone. Reasons come
/// from the port's <see cref="EngineStatus.Reason"/> and are never rewritten here.
/// </summary>
public static class EngineViewModel
{
    public const string NotInstalledWord = "Not installed";
    public const string InstalledWord = "Installed";
    public const string LoadingWord = "Loading";
    public const string ReadyWord = "Ready";
    public const string FellBackWord = "Using your processor";
    public const string FailedWord = "Not working";

    private const string DownloadHint = "Download its files under Downloads to use it.";

    public static EngineView Describe(EngineViewInput input)
    {
        var name = input.EngineName;
        if (input.Status is not { } status)
        {
            return input.Installed
                ? new EngineView(Severity.Success, InstalledWord, "Ready when you switch to it.")
                : new EngineView(Severity.Warning, NotInstalledWord, DownloadHint);
        }

        if (input.Warming || status.State == EngineState.Loading)
        {
            return new EngineView(Severity.Info, LoadingWord, LoadingDetail(name, status));
        }

        return status.State switch
        {
            EngineState.NotInstalled => new EngineView(
                Severity.Warning, NotInstalledWord, NonEmpty(status.Reason) ?? DownloadHint),
            EngineState.Ready => new EngineView(Severity.Success, ReadyWord, ReadyDetail(status)),
            EngineState.FellBack => new EngineView(
                Severity.Warning,
                FellBackWord,
                NonEmpty(status.Reason) ?? "The graphics card could not be used, so this runs on your processor."),
            _ => new EngineView(
                Severity.Error,
                FailedWord,
                NonEmpty(status.Reason) ?? $"{name} could not start. Copy the details and check the log."),
        };
    }

    /// <summary>"Running on your graphics card (name)." or "Running on your processor."</summary>
    public static string WhereItRuns(ActiveDevice device)
    {
        if (!device.IsGpu)
        {
            return "Running on your processor.";
        }

        return string.IsNullOrWhiteSpace(device.Adapter)
            ? "Running on your graphics card."
            : $"Running on your graphics card ({device.Adapter.Trim()}).";
    }

    /// <summary>
    /// Where a ready engine runs. On the processor when the player asked for more than that, the port's reason
    /// follows, so "Ready" never hides why the graphics card is not in use.
    /// </summary>
    private static string ReadyDetail(EngineStatus status)
    {
        var where = WhereItRuns(status.ActiveDevice);
        if (status.ActiveDevice.IsGpu && status.Reason == PrecisionNotes.FullSizeIgnoredOnGpu)
        {
            return $"{where} {status.Reason}";
        }

        if (status.ActiveDevice.IsGpu || status.RequestedDevice == DeviceMode.Cpu || NonEmpty(status.Reason) is not { } reason)
        {
            return where;
        }

        return reason.StartsWith(where.TrimEnd('.'), StringComparison.Ordinal) ? reason : $"{where} {reason}";
    }

    private static string LoadingDetail(string name, EngineStatus status) =>
        status.ActiveDevice.IsGpu
            ? $"Loading {name} on your graphics card. The first line can take a few seconds."
            : $"Loading {name}.";

    private static string? NonEmpty(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();
}
