namespace AIVoiceActing.UI.State;

/// <summary>
/// What the setup flow knows about the graphics card. Until the device probe and setup advisor
/// exist, <see cref="Unknown"/> is all anyone reports, which makes the recommendation Fast.
/// The plugin root swaps <c>UiContext.DeviceInfo</c> for a real probe without touching the flow.
/// </summary>
public sealed record SetupDeviceInfo(bool HasUsableGpu, string? GpuName)
{
    public static SetupDeviceInfo Unknown { get; } = new(false, null);
}
