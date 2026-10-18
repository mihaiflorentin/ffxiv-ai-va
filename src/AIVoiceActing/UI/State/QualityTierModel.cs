namespace AIVoiceActing.UI.State;

using AIVoiceActing.Domain;
using AIVoiceActing.Ports;

/// <summary>The engine settings a tier writes, captured so a cancelled setup can put them back.</summary>
public sealed record EngineChoice(string Primary, bool ExpressiveEnabled, string ExpressiveEngineId);

/// <summary>
/// Pure mapping between the quality tier and <see cref="EngineSettings"/>. Fast is Kokoro alone;
/// Balanced is Kokoro with the expressive engine (Turbo) routed in for emotional lines; Most
/// expressive makes Turbo the main engine and needs a usable graphics card. The round trip
/// <see cref="Apply"/> then <see cref="FromConfig"/> returns the same tier. Settings that match
/// none of the three (the remote server, a retired engine) read back as null, "custom".
/// </summary>
public static class QualityTierModel
{
    public static readonly IReadOnlyList<QualityTier> All =
        [QualityTier.Fast, QualityTier.Balanced, QualityTier.MostExpressive];

    public static string Label(QualityTier tier) => tier switch
    {
        QualityTier.Fast => "Fast",
        QualityTier.Balanced => "Balanced",
        _ => "Most expressive",
    };

    /// <summary>One plain sentence on what the tier sounds like and costs.</summary>
    public static string Explanation(QualityTier tier) => tier switch
    {
        QualityTier.Fast => "Natural narration that runs on your processor. Quick to start and light on your computer.",
        QualityTier.Balanced => "The same fast narration, plus a more expressive voice for emotional lines such as laughs and sighs.",
        _ => "The most lifelike delivery for every line. Needs a graphics card and a large download.",
    };

    /// <summary>
    /// A plain warning for the quality card when a tier that expects the graphics card (Balanced, Most expressive)
    /// is running its expressive voice on the processor; null when all is as expected. <paramref name="gpuEngine"/>
    /// is the status of the engine that can use the graphics card. The port's own reason is shown when it has one.
    /// </summary>
    public static string? ProcessorWarning(QualityTier? tier, EngineStatus? gpuEngine)
    {
        if (tier is not (QualityTier.Balanced or QualityTier.MostExpressive)
            || gpuEngine is not { State: EngineState.Ready or EngineState.FellBack } status
            || status.ActiveDevice.IsGpu
            || status.RequestedDevice == DeviceMode.Cpu)
        {
            return null;
        }

        return string.IsNullOrWhiteSpace(status.Reason)
            ? $"{Label(tier.Value)} expects your graphics card, but its voice is running on your processor."
            : status.Reason.Trim();
    }

    /// <summary>Engine ids the tier needs on disk, in download order.</summary>
    public static IReadOnlyList<string> EnginesFor(QualityTier tier) => tier switch
    {
        QualityTier.Fast => [ConfigMigrator.KokoroId],
        QualityTier.Balanced => [ConfigMigrator.KokoroId, ConfigMigrator.TurboId],
        _ => [ConfigMigrator.TurboId],
    };

    public static bool IsAvailable(QualityTier tier, bool hasUsableGpu) => SetupAdvisor.IsAvailable(tier, hasUsableGpu);

    /// <summary>Why a tier cannot be chosen; null when it can.</summary>
    public static string? UnavailableReason(QualityTier tier, bool hasUsableGpu) =>
        SetupAdvisor.UnavailableReason(tier, hasUsableGpu);

    /// <summary>The tier the settings describe, or null for a custom combination.</summary>
    public static QualityTier? FromConfig(EngineSettings engine, IEngineCatalog catalog)
    {
        var primary = catalog.Resolve(engine.Primary);
        if (primary == ConfigMigrator.TurboId)
        {
            return QualityTier.MostExpressive;
        }

        if (primary != ConfigMigrator.KokoroId)
        {
            return null;
        }

        if (!engine.Expressive.Enabled)
        {
            return QualityTier.Fast;
        }

        return catalog.Find(engine.Expressive.EngineId)?.Key == ConfigMigrator.TurboId ? QualityTier.Balanced : null;
    }

    /// <summary>
    /// Writes the tier into <paramref name="engine"/>. Returns false, changing nothing, when the
    /// tier is unavailable on this machine.
    /// </summary>
    public static bool Apply(EngineSettings engine, QualityTier tier, bool hasUsableGpu)
    {
        if (!IsAvailable(tier, hasUsableGpu))
        {
            return false;
        }

        switch (tier)
        {
            case QualityTier.Fast:
                engine.Primary = ConfigMigrator.KokoroId;
                engine.Expressive.Enabled = false;
                break;
            case QualityTier.Balanced:
                engine.Primary = ConfigMigrator.KokoroId;
                engine.Expressive.Enabled = true;
                engine.Expressive.EngineId = ConfigMigrator.TurboId;
                break;
            default:
                // Turbo is the main engine, so expressive routing (which needs Kokoro in front) stays off.
                engine.Primary = ConfigMigrator.TurboId;
                engine.Expressive.Enabled = false;
                break;
        }

        return true;
    }

    public static EngineChoice Capture(EngineSettings engine) =>
        new(engine.Primary, engine.Expressive.Enabled, engine.Expressive.EngineId);

    public static void Restore(EngineSettings engine, EngineChoice choice)
    {
        engine.Primary = choice.Primary;
        engine.Expressive.Enabled = choice.ExpressiveEnabled;
        engine.Expressive.EngineId = choice.ExpressiveEngineId;
    }
}
