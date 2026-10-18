namespace AIVoiceActing.Infrastructure.Onnx;

using AIVoiceActing.Domain;
using AIVoiceActing.Ports;

/// <summary>
/// Provider options for the WebGPU plugin execution provider, as a pure function of the plan.
/// The keys are UNPREFIXED: the runtime silently ignores <c>ep.webgpuexecutionprovider.*</c>
/// spellings and any unknown key without an error, so a typo here would be invisible.
/// <c>WebGpuOptionsTests</c> pins the exact strings.
/// </summary>
public static class WebGpuOptions
{
    public const string DawnBackendTypeKey = "dawnBackendType";
    public const string PowerPreferenceKey = "powerPreference";

    public const string BackendVulkan = "Vulkan";
    public const string BackendD3D12 = "D3D12";
    public const string PowerHigh = "high-performance";
    public const string PowerLow = "low-power";

    public static IReadOnlyDictionary<string, string> Build(DevicePlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (plan.Ep != DeviceEp.WebGpu)
        {
            throw new ArgumentException($"WebGPU options requested for a {plan.Ep} plan.", nameof(plan));
        }

        return Build(plan.Api, plan.PowerPreference);
    }

    public static IReadOnlyDictionary<string, string> Build(GpuApi api, GpuPreference preference) =>
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [DawnBackendTypeKey] = api switch
            {
                GpuApi.Vulkan => BackendVulkan,
                GpuApi.D3D12 => BackendD3D12,
                _ => throw new ArgumentException("The graphics API must be resolved before building options.", nameof(api)),
            },
            [PowerPreferenceKey] = preference == GpuPreference.LowPower ? PowerLow : PowerHigh,
        };
}
