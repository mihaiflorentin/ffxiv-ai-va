namespace AIVoiceActing.Domain;

using System.Globalization;
using AIVoiceActing.Ports;

/// <summary>The three voice-quality choices a player makes instead of picking engines.</summary>
public enum QualityTier
{
    Fast,
    Balanced,
    MostExpressive,
}

/// <summary>One model download group: its full size and how much is still missing (partial files already subtracted).</summary>
public sealed record GroupStatus(string Group, long TotalBytes, long MissingBytes)
{
    public bool Installed => this.MissingBytes <= 0;
}

/// <summary>What the advisor decides from.</summary>
/// <param name="HasUsableGpu">A GPU path exists and nothing has switched it off (see <see cref="SetupAdvisor.HasUsableGpu"/>).</param>
/// <param name="FreeDiskBytes">Free space on the drive that holds the models; <see cref="long.MaxValue"/> when unknown.</param>
/// <param name="Groups">Every group the advisor may ask for (a group that is not listed counts as not downloaded, size unknown).</param>
/// <param name="CurrentTier">The tier the saved settings already describe; null for a custom setup.</param>
/// <param name="VramBytes">Video memory of the chosen adapter; null while unknown, which the advisor treats as "enough".</param>
public sealed record SetupAdvisorInput(
    bool HasUsableGpu,
    long FreeDiskBytes,
    IReadOnlyList<GroupStatus> Groups,
    QualityTier? CurrentTier = null,
    long? VramBytes = null);

/// <summary>A tier with the engine, precision and downloads it needs. <see cref="Blocked"/> is the plain reason it cannot start; null when it can.</summary>
/// <param name="PrimaryEngine">Engine id that speaks every line.</param>
/// <param name="ExpressiveEngine">Engine id the strongly emotional lines go to; null when routing is off.</param>
/// <param name="Precision">Turbo precision to ask for: <c>q4f16</c> with a GPU, <c>fp32</c> on the CPU; null when Turbo is not used.</param>
/// <param name="Groups">Every download group the tier needs, in download order.</param>
/// <param name="ToDownload">The groups among <paramref name="Groups"/> that are not on disk yet.</param>
public sealed record SetupRecommendation(
    QualityTier Tier,
    string PrimaryEngine,
    string? ExpressiveEngine,
    string? Precision,
    IReadOnlyList<string> Groups,
    IReadOnlyList<GroupStatus> ToDownload,
    long BytesToDownload,
    string? Blocked);

/// <summary>
/// Pure hardware to setup mapping: which tier to suggest on this machine, which engine and precision it
/// uses, which download groups it needs and how many bytes are still missing. A verified GPU turns the
/// expressive engine (Turbo) into its q4f16 build, 0.56 GB plus the 4 MB base files, and never asks for the
/// 3.3 GB full-size files; without one Turbo stays on the CPU at full precision (base files plus the
/// full-size group), usable only for the occasional emotional line.
/// "Most expressive" (Turbo for every line) needs the GPU. Disk space is checked against the bytes still missing.
/// </summary>
public static class SetupAdvisor
{
    public const string KokoroGroup = "kokoro";
    public const string TurboGroup = TurboInstall.Fp32Group;
    public const string TurboQ4F16Group = TurboInstall.Q4F16Group;
    public const string TurboBaseGroup = TurboInstall.BaseGroup;
    public const string KokoroEngine = "kokoro";
    public const string TurboEngine = "chatterbox-turbo";
    public const string Q4F16 = "q4f16";
    public const string Fp32 = "fp32";

    /// <summary>Free space kept back on top of the download itself.</summary>
    public const long DiskHeadroomBytes = 256L * 1024 * 1024;

    /// <summary>Below this much video memory the GPU is not used for Turbo (the q4f16 language model needs about 0.2 GB plus its cache).</summary>
    public const long MinVramBytes = 768L * 1024 * 1024;

    /// <summary>
    /// True when the probe found a GPU path and the crash guard has not switched it off, so a recommendation
    /// never promises what is disabled.
    /// </summary>
    public static bool HasUsableGpu(DeviceReport? report, bool gpuDisabledByCrashGuard = false) =>
        report is { HasUsableGpu: true } && !gpuDisabledByCrashGuard;

    public static bool IsAvailable(QualityTier tier, bool hasUsableGpu) =>
        tier != QualityTier.MostExpressive || hasUsableGpu;

    /// <summary>Why a tier cannot be chosen on this machine; null when it can.</summary>
    public static string? UnavailableReason(QualityTier tier, bool hasUsableGpu) =>
        IsAvailable(tier, hasUsableGpu) ? null : "No supported graphics card was found, so this option is unavailable.";

    /// <summary>The download groups a tier needs, in order, given whether the GPU build of Turbo applies.</summary>
    public static IReadOnlyList<string> GroupsFor(QualityTier tier, bool gpu) => tier switch
    {
        QualityTier.Fast => [KokoroGroup],
        QualityTier.Balanced => gpu ? [KokoroGroup, TurboBaseGroup, TurboQ4F16Group] : [KokoroGroup, TurboBaseGroup, TurboGroup],
        _ => gpu ? [TurboBaseGroup, TurboQ4F16Group] : [TurboBaseGroup, TurboGroup],
    };

    /// <summary>Whether the GPU build of Turbo applies: a usable GPU, and enough video memory when that is known.</summary>
    public static bool UsesGpuBuild(SetupAdvisorInput input) =>
        input.HasUsableGpu && (input.VramBytes is not { } vram || vram >= MinVramBytes);

    public static SetupRecommendation Plan(QualityTier tier, SetupAdvisorInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var gpu = UsesGpuBuild(input);
        var groups = GroupsFor(tier, gpu);
        var toDownload = new List<GroupStatus>();
        foreach (var name in groups)
        {
            var status = input.Groups.FirstOrDefault(g => string.Equals(g.Group, name, StringComparison.Ordinal))
                ?? new GroupStatus(name, 0, 1);
            if (!status.Installed)
            {
                toDownload.Add(status);
            }
        }

        var bytes = toDownload.Sum(g => g.MissingBytes);
        var blocked = UnavailableReason(tier, input.HasUsableGpu);
        if (blocked is null && bytes > 0 && input.FreeDiskBytes < bytes + DiskHeadroomBytes)
        {
            blocked = $"Needs {FileSize(bytes + DiskHeadroomBytes)} free on the drive that holds your voices; " +
                      $"{FileSize(input.FreeDiskBytes)} is free.";
        }

        return new SetupRecommendation(
            tier,
            tier == QualityTier.MostExpressive ? TurboEngine : KokoroEngine,
            tier == QualityTier.Balanced ? TurboEngine : null,
            tier == QualityTier.Fast ? null : gpu ? Q4F16 : Fp32,
            groups,
            toDownload,
            bytes,
            blocked);
    }

    /// <summary>
    /// The tier to suggest. A tier the player already runs and has fully downloaded stays; with a usable
    /// graphics card and room on disk it is Balanced; everything else is Fast.
    /// </summary>
    public static SetupRecommendation Recommend(SetupAdvisorInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (input.CurrentTier is { } current)
        {
            var keep = Plan(current, input);
            if (keep.Blocked is null && keep.BytesToDownload == 0)
            {
                return keep;
            }
        }

        if (input.HasUsableGpu)
        {
            var balanced = Plan(QualityTier.Balanced, input);
            if (balanced.Blocked is null)
            {
                return balanced;
            }
        }

        return Plan(QualityTier.Fast, input);
    }

    // Same wording as the UI's Labels.FileSize (the UI layer is off limits to Domain; a test pins the two together).
    private static string FileSize(long bytes)
    {
        const double Kb = 1024;
        const double Mb = Kb * 1024;
        const double Gb = Mb * 1024;
        var inv = CultureInfo.InvariantCulture;
        return bytes switch
        {
            >= (long)Gb => (bytes / Gb).ToString("0.0", inv) + " GB",
            >= (long)Mb => (bytes / Mb).ToString("0.0", inv) + " MB",
            >= (long)Kb => (bytes / Kb).ToString("0.0", inv) + " KB",
            _ => bytes.ToString(inv) + " B",
        };
    }
}
