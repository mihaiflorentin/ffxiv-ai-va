namespace AIVoiceActing.Ports;

/// <summary>Raised by <see cref="IEngineCatalog.Get"/> for an id or alias the catalog does not know.</summary>
public sealed class EngineNotFoundException(string id)
    : Exception($"Unknown engine \"{id}\".")
{
    public string EngineId { get; } = id;
}

public enum EngineSpeed
{
    Fast,
    Slower,
    Slow,
}

public enum EngineTier
{
    Cpu,
    Gpu,
    Labs,
    Remote,
}

public enum ComputeDevice
{
    Cpu,
    Gpu,
}

/// <summary>What a voice must provide for an engine to use it.</summary>
public enum RealizationKind
{
    StyleTensor,
    ReferenceClip,
    EnginePreset,
}

/// <summary>
/// Static facts about one engine. <see cref="AssetGroups"/> are the download-group keys of the
/// model catalog; each group belongs to exactly one engine.
/// </summary>
/// <param name="Key">Canonical engine id (what <c>Engine.Primary</c> stores).</param>
/// <param name="Retired">Kept only so old selections resolve; the UI must not offer it.</param>
public sealed record EngineInfo(
    string Key,
    string DisplayName,
    string Blurb,
    EngineSpeed Speed,
    EngineTier Tier,
    bool SupportsGpu,
    ComputeDevice DefaultDevice,
    IReadOnlyList<RealizationKind> Consumes,
    string License,
    bool NonCommercial,
    bool Experimental,
    bool Retired,
    IReadOnlyList<string> AssetGroups,
    IReadOnlyDictionary<string, long> ApproxBytesByPrecision);

/// <summary>Engine descriptors, alias resolution and asset-group ownership.</summary>
public interface IEngineCatalog
{
    /// <summary>Every known engine (including retired ones), in display order.</summary>
    IReadOnlyList<EngineInfo> List();

    /// <summary>Canonical key for an id or alias; null, empty or unknown values resolve to the default engine (kokoro).</summary>
    string Resolve(string? idOrAlias);

    /// <summary>The engine for an id or alias, or null when unknown.</summary>
    EngineInfo? Find(string? idOrAlias);

    /// <summary>The engine for an id or alias.</summary>
    /// <exception cref="EngineNotFoundException">Unknown id.</exception>
    EngineInfo Get(string idOrAlias);

    /// <summary>The engine that owns a model download group, or null for an unknown group.</summary>
    EngineInfo? OwnerOfGroup(string assetGroup);
}
