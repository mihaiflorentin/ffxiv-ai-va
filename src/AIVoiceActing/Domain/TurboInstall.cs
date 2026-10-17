namespace AIVoiceActing.Domain;

/// <summary>
/// Which download groups make a Chatterbox Turbo install complete. Every install needs the base group
/// (tokenizer and default voice, 4 MB) plus ONE complete precision set: the full-size fp32 files (the CPU
/// choice), the fp16 files, or the q4f16 files (the graphics card choice, 0.56 GB). Nothing forces the big
/// fp32 download on a machine that never uses it. Pure: callers say which groups are on disk.
/// </summary>
public static class TurboInstall
{
    public const string BaseGroup = "turbo-base";
    public const string Fp32Group = "turbo";
    public const string Fp16Group = "turbo-fp16";
    public const string Q4F16Group = "turbo-q4f16";

    /// <summary>The precision sets, in the order a machine prefers them (see <see cref="PreferredSets"/>).</summary>
    public static IReadOnlyList<string> SetGroups { get; } = [Fp32Group, Fp16Group, Q4F16Group];

    /// <summary>Every group that is part of a Turbo install, base first.</summary>
    public static IReadOnlyList<string> AllGroups { get; } = [BaseGroup, Fp32Group, Fp16Group, Q4F16Group];

    /// <summary>The precision sets best first: on a graphics card the smallest verified build leads, on the CPU the full-size one.</summary>
    public static IReadOnlyList<string> PreferredSets(bool gpu) =>
        gpu ? [Q4F16Group, Fp16Group, Fp32Group] : [Fp32Group, Fp16Group, Q4F16Group];

    /// <summary>True when the base files and at least one complete precision set are on disk.</summary>
    public static bool IsInstalled(Func<string, bool> groupInstalled) =>
        groupInstalled(BaseGroup) && SetGroups.Any(groupInstalled);

    /// <summary>The best complete set on disk for this machine; null when none is.</summary>
    public static string? InstalledSet(bool gpu, Func<string, bool> groupInstalled) =>
        PreferredSets(gpu).FirstOrDefault(groupInstalled);

    /// <summary>
    /// The groups this machine needs: the base group plus the set it already has, or when it has none the
    /// set it should download (q4f16 with a usable graphics card, fp32 without).
    /// </summary>
    public static IReadOnlyList<string> RequiredGroups(bool gpu, Func<string, bool> groupInstalled) =>
        [BaseGroup, InstalledSet(gpu, groupInstalled) ?? PreferredSets(gpu)[0]];
}
