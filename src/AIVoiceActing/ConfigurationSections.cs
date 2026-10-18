namespace AIVoiceActing;

using AIVoiceActing.Ports;

// Persisted by Newtonsoft with TypeNameHandling ("$type": "AIVoiceActing.EngineSettings, AIVoiceActing"),
// so these are top-level types and must never be renamed or moved without a migration.

/// <summary>Which engine speaks, and the optional expressive second engine.</summary>
public sealed class EngineSettings
{
    /// <summary>Engine id (aliases are resolved by the engine catalog).</summary>
    public string Primary { get; set; } = "kokoro";

    public ExpressiveSettings Expressive { get; set; } = new();

    /// <summary>Advanced per-engine overrides only; absent key = engine defaults.</summary>
    public EngineOptionsMap PerEngine { get; set; } = new();
}

/// <summary>
/// Engine id -> options. A named type on purpose: Newtonsoft's TypeNameHandling would persist
/// <c>Dictionary&lt;string, EngineOptions&gt;</c> with the plugin assembly inside the generic
/// argument list, which Dalamud's load context can fail to resolve on the way back in.
/// </summary>
public sealed class EngineOptionsMap : Dictionary<string, EngineOptions>
{
}

/// <summary>Routes strongly emotional lines to a second engine when it is installed.</summary>
public sealed class ExpressiveSettings
{
    public bool Enabled { get; set; }

    public string EngineId { get; set; } = "chatterbox-turbo";
}

public sealed class EngineOptions
{
    /// <summary>inherit | cpu | gpu.</summary>
    public string Device { get; set; } = "inherit";

    /// <summary>auto | fp32 | fp16 | q4f16.</summary>
    public string Precision { get; set; } = "auto";

    /// <summary>Engine-specific extras (for example remote: url, model, voiceMap).</summary>
    public Dictionary<string, string> Extra { get; set; } = new();
}

/// <summary>Where synthesis runs. <see cref="Mode"/> auto means each engine's recommended device.</summary>
public sealed class DeviceSettings
{
    public const string ModeAuto = "auto";
    public const string ModeGpu = "gpu";
    public const string ModeCpu = "cpu";

    /// <summary>auto | gpu | cpu.</summary>
    public string Mode { get; set; } = ModeAuto;

    /// <summary>
    /// performance (the discrete GPU, the default) | lowpower (the integrated GPU, leaves the discrete one
    /// to the game). The GPU runtime can pick an adapter class but not a specific adapter, so this
    /// replaces the old per-adapter picker (<c>AdapterId</c>, never exposed; old files carrying it load fine).
    /// </summary>
    public string GpuPreference { get; set; } = PreferencePerformance;

    public const string PreferencePerformance = "performance";
    public const string PreferenceLowPower = "lowpower";

    /// <summary>
    /// Advanced, for measuring: let the voice decoder's two worker threads spin-wait between short jobs.
    /// Off by default (every session parks idle threads so the game keeps its cores).
    /// </summary>
    public bool DecoderSpinning { get; set; }

    /// <summary>Advanced: auto | webgpu | directml.</summary>
    public string Backend { get; set; } = "auto";

    /// <summary>Advanced, webgpu only: auto | vulkan | d3d12.</summary>
    public string Api { get; set; } = "auto";

    /// <summary>gentle | balanced | fast.</summary>
    public string GpuImpact { get; set; } = "balanced";

    /// <summary>ONNX intra-op thread budget: low = 2, medium = 4, high = 8.</summary>
    public string CpuImpact { get; set; } = "medium";

    public bool FallbackToCpu { get; set; } = true;

    /// <summary>Set by the GPU crash guard; cleared by "Retry on GPU".</summary>
    public bool GpuDisabledByCrashGuard { get; set; }

    /// <summary>The device settings as the policy reads them (unknown strings fall back to the defaults).</summary>
    public DevicePreferences ToPreferences() => new(
        this.Mode switch
        {
            ModeGpu => DeviceMode.Gpu,
            ModeCpu => DeviceMode.Cpu,
            _ => DeviceMode.Auto,
        },
        this.GpuPreference == PreferenceLowPower
            ? Ports.GpuPreference.LowPower
            : Ports.GpuPreference.Performance,
        this.Api switch
        {
            "vulkan" => GpuApi.Vulkan,
            "d3d12" => GpuApi.D3D12,
            _ => GpuApi.Auto,
        },
        this.Backend switch
        {
            "webgpu" => DeviceBackend.WebGpu,
            "directml" => DeviceBackend.DirectML,
            _ => DeviceBackend.Auto,
        },
        this.GpuDisabledByCrashGuard);

    /// <summary>
    /// The execution-provider string the legacy ONNX engines understand (auto/cpu/directml),
    /// derived from <see cref="Mode"/>. Turbo and Kokoro follow <see cref="ToPreferences"/> instead.
    /// </summary>
    public string ToExecutionProvider() => this.Mode switch
    {
        ModeCpu => "cpu",
        ModeGpu => "directml",
        _ => "auto",
    };

    /// <summary>Applies a legacy-style execution-provider choice (auto/cpu/directml/coreml).</summary>
    public void ApplyExecutionProvider(string? executionProvider)
    {
        switch (executionProvider)
        {
            case "cpu":
                this.Mode = ModeCpu;
                break;
            case "directml":
                this.Mode = ModeGpu;
                this.Backend = "directml";
                break;
            default:
                this.Mode = ModeAuto;
                break;
        }
    }

    /// <summary>Synthesis thread count for the CPU impact setting.</summary>
    public int CpuThreads() => this.CpuImpact switch
    {
        "low" => 2,
        "high" => 8,
        _ => 4,
    };
}

public sealed class VoiceSettings
{
    public List<string> DisabledPacks { get; set; } = [];

    public List<string> ExtraPackDirs { get; set; } = [];

    /// <summary>Every character gets a voice of their own, blended from the casting set the first
    /// time they speak and reused from then on. Off: characters pick one of the cast voices.</summary>
    public bool UniquePerCharacter { get; set; } = true;
}

/// <summary>
/// Report of the last schema migration, persisted until the user acknowledges it (the UI shows
/// a banner while <see cref="Changes"/> is non-empty).
/// </summary>
public sealed class MigrationState
{
    public int FromVersion { get; set; }

    public int ToVersion { get; set; }

    public List<MigrationChange> Changes { get; set; } = [];
}

public sealed record MigrationChange(string Area, string Before, string After, string Message);
