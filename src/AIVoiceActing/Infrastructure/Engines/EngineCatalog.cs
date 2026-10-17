namespace AIVoiceActing.Infrastructure.Engines;

using AIVoiceActing.Infrastructure.Onnx;
using AIVoiceActing.Ports;

/// <summary>
/// Static engine descriptors. Group keys come from <see cref="ModelCatalog"/> so the UI stops
/// hard-coding them. Legacy engines (f5, chatterbox) stay listed as retired so old selections
/// and their download groups still resolve; remote is a placeholder until its phase.
/// </summary>
public sealed class EngineCatalog : IEngineCatalog
{
    public const string DefaultEngineId = "kokoro";

    private const long Mb = 1024L * 1024;
    private const long Gb = 1024L * Mb;

    /// <summary>Shared instance for callers that cannot take the container's handle (the legacy UI).</summary>
    public static EngineCatalog Instance { get; } = new();

    private static readonly IReadOnlyDictionary<string, string> Aliases =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["turbo"] = "chatterbox-turbo",
        };

    private static readonly IReadOnlyList<EngineInfo> Engines =
    [
        new EngineInfo(
            "kokoro", "Kokoro", "Fast narration on the CPU. The default for every line.",
            EngineSpeed.Fast, EngineTier.Cpu, SupportsGpu: false, ComputeDevice.Cpu,
            [RealizationKind.StyleTensor], "Apache-2.0", NonCommercial: false, Experimental: false, Retired: false,
            // The emotion classifier group is shared by every engine; it is listed here, with the
            // default engine, so each group still has exactly one owner.
            [ModelCatalog.KokoroGroup, ModelCatalog.EmotionGroup],
            new Dictionary<string, long> { ["fp32"] = 325 * Mb }),
        new EngineInfo(
            "chatterbox-turbo", "Chatterbox Turbo", "Expressive delivery with tags such as [laugh]; clones a reference clip.",
            EngineSpeed.Slower, EngineTier.Gpu, SupportsGpu: true, ComputeDevice.Gpu,
            [RealizationKind.ReferenceClip], "MIT", NonCommercial: false, Experimental: false, Retired: false,
            [ModelCatalog.TurboGroup],
            new Dictionary<string, long>
            {
                ["fp32"] = 3300 * Mb,
                ["fp16"] = 1700 * Mb,
                ["q4f16"] = 560 * Mb,
            }),
        new EngineInfo(
            "remote", "Remote server", "Speaks through an OpenAI-style HTTP server you run yourself.",
            EngineSpeed.Fast, EngineTier.Remote, SupportsGpu: false, ComputeDevice.Cpu,
            [RealizationKind.EnginePreset], "n/a", NonCommercial: false, Experimental: true, Retired: false,
            [],
            new Dictionary<string, long>()),
        new EngineInfo(
            "f5", "F5-TTS", "Reference-clip cloning with strong delivery, far slower than real time on CPU.",
            EngineSpeed.Slow, EngineTier.Labs, SupportsGpu: false, ComputeDevice.Cpu,
            [RealizationKind.ReferenceClip], "CC-BY-NC (weights)", NonCommercial: true, Experimental: true, Retired: true,
            [ModelCatalog.F5Group],
            new Dictionary<string, long> { ["fp32"] = 1450 * Mb }),
        new EngineInfo(
            "chatterbox", "Chatterbox (legacy)", "The original cloning engine, superseded by Turbo.",
            EngineSpeed.Slow, EngineTier.Labs, SupportsGpu: false, ComputeDevice.Cpu,
            [RealizationKind.ReferenceClip], "MIT", NonCommercial: false, Experimental: true, Retired: true,
            [ModelCatalog.ChatterboxRequiredGroup, ModelCatalog.ChatterboxFp32LmGroup],
            new Dictionary<string, long> { ["q4"] = 1 * Gb, ["fp32"] = 3 * Gb }),
    ];

    public IReadOnlyList<EngineInfo> List() => Engines;

    public string Resolve(string? idOrAlias) => this.Find(idOrAlias)?.Key ?? DefaultEngineId;

    public EngineInfo? Find(string? idOrAlias)
    {
        if (string.IsNullOrWhiteSpace(idOrAlias))
        {
            return null;
        }

        var id = idOrAlias.Trim();
        if (Aliases.TryGetValue(id, out var canonical))
        {
            id = canonical;
        }

        return Engines.FirstOrDefault(e => string.Equals(e.Key, id, StringComparison.OrdinalIgnoreCase));
    }

    public EngineInfo Get(string idOrAlias) =>
        this.Find(idOrAlias) ?? throw new EngineNotFoundException(idOrAlias);

    public EngineInfo? OwnerOfGroup(string assetGroup) =>
        Engines.FirstOrDefault(e => e.AssetGroups.Contains(assetGroup));
}
