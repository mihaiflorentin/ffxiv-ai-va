namespace AIVoiceActing.Infrastructure.Onnx;

using AIVoiceActing.Domain;
using AIVoiceActing.Ports;
using Microsoft.ML.OnnxRuntime;

/// <summary>Result of loading the WebGPU runtime and listing its adapters.</summary>
/// <param name="Failure">Plain-text reason when <paramref name="Available"/> is false.</param>
public sealed record WebGpuEnumeration(bool Available, IReadOnlyList<DeviceAdapter> Adapters, string Failure)
{
    public static WebGpuEnumeration Unavailable(string failure) => new(false, [], failure);
}

/// <summary>Seam over the native WebGPU execution provider so the probe and factory test without a GPU.</summary>
public interface IWebGpuRuntime
{
    /// <summary>Loads the runtime on first use (cached afterwards) and lists the GPUs it found.</summary>
    WebGpuEnumeration Enumerate();

    /// <summary>Adds the WebGPU provider to <paramref name="options"/>; throws when it is unavailable.</summary>
    void Append(SessionOptions options, DevicePlan plan);

    /// <summary>
    /// The allocator description of the GPU's default buffer memory ("WebGPU_Buf"), after a successful
    /// <see cref="Enumerate"/>; what a session bound to the GPU needs to keep tensors device-resident.
    /// Null when the runtime is unavailable (and in fakes).
    /// </summary>
    OrtMemoryInfo? DeviceMemory { get; }
}

/// <summary>
/// Registers <c>onnxruntime_providers_webgpu</c> with ONNX Runtime by absolute path (Dalamud gives
/// plugin assemblies an empty Location, so the caller passes the plugin folder) and lists its
/// adapters from <c>GetEpDevices</c>, for display only: the device handed to
/// <c>AppendExecutionProvider</c> is ignored by the runtime, which picks the adapter class from
/// <c>powerPreference</c>. Registration is process-wide and survives a plugin reload, so a second
/// registration under the same name is treated as success.
/// </summary>
public sealed class WebGpuRuntime : IWebGpuRuntime
{
    public const string EpName = "WebGpuExecutionProvider";
    public const string RegistrationName = "aiva_webgpu_ep";

    /// <summary>Companion natives Dawn needs; preloaded by absolute path so the runtime's imports resolve.</summary>
    public static readonly string[] CompanionNatives = ["dxil.dll", "dxcompiler.dll"];

    private static readonly object ProcessGate = new();

    private readonly string? pluginDirectory;
    private readonly bool isWine;
    private readonly ILogSink? log;
    private readonly string? libraryPathOverride;
    private readonly Func<string> systemDirectory;
    private readonly object gate = new();
    private WebGpuEnumeration? cached;
    private IReadOnlyList<OrtEpDevice> devices = [];
    private OrtMemoryInfo? deviceMemory;

    /// <param name="pluginDirectory">Folder holding the runtime dlls (the plugin's AssemblyLocation directory).</param>
    /// <param name="libraryPathOverride">Absolute runtime path for hosts without the flat plugin layout (SmokeSynth on Linux).</param>
    public WebGpuRuntime(
        string? pluginDirectory,
        bool isWine,
        ILogSink? log = null,
        string? libraryPathOverride = null,
        Func<string>? systemDirectory = null)
    {
        this.pluginDirectory = pluginDirectory;
        this.isWine = isWine;
        this.log = log;
        this.libraryPathOverride = libraryPathOverride;
        this.systemDirectory = systemDirectory ?? (() => Environment.SystemDirectory);
    }

    /// <summary>Runtime file name for the current OS.</summary>
    public static string LibraryFileName =>
        OperatingSystem.IsWindows() ? "onnxruntime_providers_webgpu.dll"
        : OperatingSystem.IsMacOS() ? "libonnxruntime_providers_webgpu.dylib"
        : "libonnxruntime_providers_webgpu.so";

    public WebGpuEnumeration Enumerate()
    {
        lock (this.gate)
        {
            return this.cached ??= this.EnumerateCore();
        }
    }

    public OrtMemoryInfo? DeviceMemory
    {
        get
        {
            lock (this.gate)
            {
                return this.deviceMemory ??= this.devices.Count > 0
                    ? this.devices[0].GetMemoryInfo(OrtDeviceMemoryType.DEFAULT)
                    : null;
            }
        }
    }

    public void Append(SessionOptions options, DevicePlan plan)
    {
        ArgumentNullException.ThrowIfNull(options);
        var listing = this.Enumerate();
        if (!listing.Available || this.devices.Count == 0)
        {
            throw new InvalidOperationException(
                listing.Failure.Length > 0 ? listing.Failure : "The WebGPU runtime is not available.");
        }

        // The device argument is ignored by the runtime (the adapter comes from powerPreference);
        // the first WebGPU device is the documented way to attach the provider.
        options.AppendExecutionProvider(
            OrtEnv.Instance(),
            [this.devices[0]],
            new Dictionary<string, string>(WebGpuOptions.Build(plan)));
    }

    private WebGpuEnumeration EnumerateCore()
    {
        try
        {
            var libraryPath = this.libraryPathOverride
                ?? (this.pluginDirectory is { Length: > 0 } dir ? Path.Combine(dir, LibraryFileName) : null);
            if (libraryPath is null || !File.Exists(libraryPath))
            {
                return WebGpuEnumeration.Unavailable(
                    "The GPU runtime files are missing from the plugin folder, so the GPU cannot be used.");
            }

            var directory = Path.GetDirectoryName(libraryPath)!;
            if (this.isWine)
            {
                var problem = WineVulkanShim.EnsureLoader(directory, this.systemDirectory(), this.log);
                if (problem is not null)
                {
                    return WebGpuEnumeration.Unavailable(problem);
                }
            }

            if (OperatingSystem.IsWindows())
            {
                NativeLibraryPreloader.Preload(directory, CompanionNatives, this.log);
            }

            var env = OrtEnv.Instance();
            lock (ProcessGate)
            {
                try
                {
                    env.RegisterExecutionProviderLibrary(RegistrationName, libraryPath);
                }
                catch (OnnxRuntimeException ex) when (ex.Message.Contains("already", StringComparison.OrdinalIgnoreCase))
                {
                    this.log?.Info("WebGPU runtime was already registered in this process.");
                }
            }

            this.devices = [.. env.GetEpDevices().Where(d => d.EpName == EpName)];
            var adapters = this.devices
                .Select(d => Describe(d.HardwareDevice, this.isWine))
                .ToList();
            this.log?.Info($"WebGPU runtime ready: {adapters.Count} GPU(s): {string.Join("; ", adapters.Select(a => $"{a.Name} ({a.Backend})"))}.");
            return adapters.Count > 0
                ? new WebGpuEnumeration(true, adapters, string.Empty)
                : WebGpuEnumeration.Unavailable("No compatible graphics card was found.");
        }
        catch (Exception ex)
        {
            this.log?.Warn($"WebGPU runtime failed to load: {ex.GetType().Name}: {ex.Message}");
            return WebGpuEnumeration.Unavailable("The GPU runtime could not be loaded, so the GPU cannot be used.");
        }
    }

    internal static DeviceAdapter Describe(OrtHardwareDevice hardware, bool isWine)
    {
        var vendor = VendorName(hardware.VendorId, hardware.Vendor);
        string? name = null;
        foreach (var entry in hardware.Metadata.Entries)
        {
            var key = entry.Key.ToLowerInvariant();
            if (!string.IsNullOrWhiteSpace(entry.Value) && (key.Contains("descr") || key.Contains("name")))
            {
                name = entry.Value.Trim();
                break;
            }
        }

        name ??= $"{vendor} GPU (device 0x{hardware.DeviceId:x4})";
        var backend = isWine || !OperatingSystem.IsWindows() ? "Vulkan" : "D3D12";
        return new DeviceAdapter(vendor, name, backend);
    }

    private static string VendorName(uint vendorId, string? vendor) => vendorId switch
    {
        0x1002 => "AMD",
        0x10DE => "NVIDIA",
        0x8086 => "Intel",
        _ => string.IsNullOrWhiteSpace(vendor) ? $"0x{vendorId:x4}" : vendor.Trim(),
    };
}
