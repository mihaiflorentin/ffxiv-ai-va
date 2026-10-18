namespace AIVoiceActing.UI.State;

using AIVoiceActing.Ports;

/// <summary>What the Processor card knows.</summary>
/// <param name="Report">The probe's last report; null while the first check is still running.</param>
/// <param name="CrashGuardTripped">A previous run died inside graphics-card code and the player has not retried.</param>
/// <param name="Status">Status of the engine that can use the graphics card; null when none is running.</param>
/// <param name="Retrying">A retry on the graphics card is already in flight.</param>
public sealed record DeviceViewInput(
    DeviceMode Mode,
    DeviceReport? Report,
    bool CrashGuardTripped,
    EngineStatus? Status,
    bool Retrying = false);

/// <summary>A problem worth an action. Copy details is always offered with a banner.</summary>
public sealed record DeviceBanner(
    Severity Severity,
    string Title,
    string Detail,
    bool CanRetryGpu,
    bool CanUseCpu,
    bool Retrying);

/// <summary>Everything the Processor card draws, decided.</summary>
/// <param name="Detected">Headline over the adapter list ("Graphics cards reported by the system").</param>
/// <param name="Adapters">One line per adapter, display only.</param>
/// <param name="Note">Extra muted line (Wine or Proton); null when there is nothing to add.</param>
/// <param name="GraphicsCardAvailable">The "Graphics card" choice can be picked.</param>
/// <param name="GraphicsCardUnavailable">Plain reason the choice is off; null when available.</param>
/// <param name="ShowPreference">Performance / Low power only matters with more than one card.</param>
public sealed record DeviceView(
    string Detected,
    IReadOnlyList<string> Adapters,
    string? Note,
    bool GraphicsCardAvailable,
    string? GraphicsCardUnavailable,
    bool ShowPreference,
    DeviceBanner? Banner);

/// <summary>
/// Pure table behind the Processor card: no adapter, Wine, crash guard, fell back, failed, and the
/// quiet cases (ready, loading, not installed) that need no banner. Adapters are labelled as reported
/// by the system, because Wine can list a placeholder card. Every reason shown for a state comes from
/// the port's text verbatim.
/// </summary>
public static class DeviceViewModel
{
    public const string CrashGuardTitle = "Graphics card was turned off after a crash";
    public const string FellBackTitle = "Voices are running on your processor";
    public const string FailedTitle = "The voices could not start";
    public const string CheckingText = "Checking your graphics card…";
    public const string AdaptersHeading = "Graphics cards reported by the system";
    public const string NoAdapterText = "No supported graphics card was found.";

    /// <summary>Segment captions of the Processor choice, in the order of <see cref="ModeValues"/>.</summary>
    public static readonly IReadOnlyList<string> ModeLabels = ["Automatic", "Graphics card", "CPU"];

    public static readonly IReadOnlyList<string> ModeValues =
        [DeviceSettings.ModeAuto, DeviceSettings.ModeGpu, DeviceSettings.ModeCpu];

    public static readonly IReadOnlyList<string> PreferenceLabels = ["Performance", "Low power"];

    public static readonly IReadOnlyList<string> PreferenceValues =
        [DeviceSettings.PreferencePerformance, DeviceSettings.PreferenceLowPower];

    /// <summary>Background-load captions, in the order of <see cref="LoadValues"/> (2, 4 and 8 threads).</summary>
    public static readonly IReadOnlyList<string> LoadLabels = ["Light", "Balanced", "Fast"];

    public static readonly IReadOnlyList<string> LoadValues = ["low", "medium", "high"];

    /// <summary>Plain line under the Processor choice.</summary>
    public static string ModeExplanation(string mode) => mode switch
    {
        DeviceSettings.ModeGpu => "Prefer the graphics card for the voices that can. If it fails, or the game keeps it too busy for a while, they use your processor.",
        DeviceSettings.ModeCpu => "Always use your processor. Slower for expressive voices, but nothing touches the graphics card.",
        _ => "Each voice runs where it works best, and moves to your processor while the game keeps the graphics card too busy. This is right for almost everyone.",
    };

    public static string PreferenceExplanation(string preference) => preference == DeviceSettings.PreferenceLowPower
        ? "Use the card built into the processor and leave the faster one to the game."
        : "Use the faster card when there is more than one.";

    public static string LoadExplanation(string impact) => impact switch
    {
        "low" => "Gentlest on your frame rate. Lines take longer to arrive.",
        "high" => "Lines arrive soonest. Can lower your frame rate while a line is being made.",
        _ => "A middle path between frame rate and waiting time.",
    };

    public static int IndexOf(IReadOnlyList<string> values, string? value)
    {
        for (var i = 0; i < values.Count; i++)
        {
            if (string.Equals(values[i], value, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return 0;
    }

    public static DeviceView Describe(DeviceViewInput input)
    {
        var report = input.Report;
        var adapters = report is null ? [] : AdapterLines(report);
        var usable = report is { HasUsableGpu: true };

        string detected;
        string? unavailable = null;
        if (report is null)
        {
            detected = CheckingText;
            unavailable = "Still checking your graphics card.";
        }
        else if (!usable)
        {
            detected = NoAdapterText;
            unavailable = string.IsNullOrWhiteSpace(report.Reason) ? NoAdapterText : report.Reason.Trim();
        }
        else
        {
            detected = AdaptersHeading;
        }

        var wine = report?.Platform == DevicePlatform.Wine;
        var note = wine
            ? "You are running through Wine or Proton. The graphics card is reached through Vulkan, and the list " +
              "comes from Wine, so it can show a card you do not have."
            : null;

        return new DeviceView(
            detected,
            adapters,
            note,
            usable,
            unavailable,
            usable && input.Mode != DeviceMode.Cpu && adapters.Count > 1,
            BannerFor(input, usable));
    }

    /// <summary>"AMD Radeon RX 7800 XT (Vulkan)". The vendor is skipped when the name already starts with it.</summary>
    public static string AdapterLine(DeviceAdapter adapter)
    {
        var name = adapter.Name.Trim();
        var vendor = adapter.Vendor.Trim();
        var label = vendor.Length == 0 || name.StartsWith(vendor, StringComparison.OrdinalIgnoreCase)
            ? name
            : $"{vendor} {name}".Trim();
        if (label.Length == 0)
        {
            label = "Unnamed graphics card";
        }

        return string.IsNullOrWhiteSpace(adapter.Backend) ? label : $"{label} ({adapter.Backend.Trim()})";
    }

    private static IReadOnlyList<string> AdapterLines(DeviceReport report) =>
        [.. report.Adapters.Select(AdapterLine)];

    private static DeviceBanner? BannerFor(DeviceViewInput input, bool usable)
    {
        var status = input.Status;
        var notCpu = input.Mode != DeviceMode.Cpu;

        // Crash guard first: it is the reason behind a CPU fallback and the one with its own title.
        if (input.CrashGuardTripped && notCpu)
        {
            var reason = NonEmpty(status?.Reason)
                ?? "The game stopped while using the graphics card, so the voices use your processor until you try again.";
            return new DeviceBanner(Severity.Warning, CrashGuardTitle, reason, CanRetryGpu: true, CanUseCpu: true, input.Retrying);
        }

        if (status is null)
        {
            return null;
        }

        switch (status.State)
        {
            case EngineState.FellBack:
            {
                var reason = NonEmpty(status.Reason)
                    ?? "The graphics card could not be used, so the voices run on your processor.";

                // No usable card at all is an expected state, not a fault: say so quietly and offer no retry.
                var severity = usable || status.CanRetryGpu ? Severity.Warning : Severity.Info;
                return new DeviceBanner(
                    severity,
                    FellBackTitle,
                    reason,
                    status.CanRetryGpu,
                    CanUseCpu: notCpu && usable,
                    input.Retrying);
            }

            case EngineState.Failed:
                return new DeviceBanner(
                    Severity.Error,
                    FailedTitle,
                    NonEmpty(status.Reason) ?? "The voice engine failed to start. Copy the details and check the log.",
                    status.CanRetryGpu,
                    // A graphics-card failure is the only kind the processor can sidestep.
                    CanUseCpu: notCpu && status.CanRetryGpu,
                    input.Retrying);
            default:
                return null;
        }
    }

    private static string? NonEmpty(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();
}
