namespace AIVoiceActing.Infrastructure.Onnx;

using System.Runtime.InteropServices;
using AIVoiceActing.Ports;

/// <summary>
/// Facts about the host process: real Windows, Wine (XIVLauncher / Proton), or something else.
/// Wine exports <c>wine_get_version</c> from ntdll; real Windows does not. The export probe is
/// injectable so the detection rule is testable on any OS.
/// </summary>
public static class RuntimeHost
{
    /// <summary>True when <paramref name="hasExport"/> (default: a native ntdll lookup) finds wine_get_version.</summary>
    public static bool DetectWine(Func<string, string, bool>? hasExport = null)
    {
        try
        {
            return (hasExport ?? NativeExport)("ntdll.dll", "wine_get_version");
        }
        catch
        {
            return false;
        }
    }

    public static DevicePlatform PlatformOf(bool isWine) =>
        isWine ? DevicePlatform.Wine
        : OperatingSystem.IsWindows() ? DevicePlatform.Windows
        : DevicePlatform.Other;

    private static bool NativeExport(string module, string export) =>
        NativeLibrary.TryLoad(module, out var handle)
        && NativeLibrary.TryGetExport(handle, export, out _);
}
