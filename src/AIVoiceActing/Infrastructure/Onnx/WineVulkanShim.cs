namespace AIVoiceActing.Infrastructure.Onnx;

using AIVoiceActing.Ports;

/// <summary>
/// Under Wine, Dawn looks for <c>vulkan-1.dll</c> only next to the WebGPU runtime dll (then next to
/// the exe), never on the Windows search path, so "No supported adapters" follows on a stock layout.
/// Copying the prefix's own Wine stub (<c>%windir%\system32\vulkan-1.dll</c>, which forwards to the
/// host Vulkan driver) beside the runtime fixes it. Only the plugin folder is written; the game
/// folder is never touched.
/// </summary>
public static class WineVulkanShim
{
    public const string FileName = "vulkan-1.dll";

    /// <summary>Copies the loader when missing or different. Returns null on success, else a plain-text failure.</summary>
    public static string? EnsureLoader(string pluginDirectory, string systemDirectory, ILogSink? log = null)
    {
        try
        {
            var source = Path.Combine(systemDirectory, FileName);
            var target = Path.Combine(pluginDirectory, FileName);
            if (!File.Exists(source))
            {
                return "Wine has no Vulkan loader (vulkan-1.dll) in this prefix, so the GPU cannot be used.";
            }

            if (File.Exists(target) && new FileInfo(target).Length == new FileInfo(source).Length)
            {
                return null;
            }

            File.Copy(source, target, overwrite: true);
            log?.Info($"Copied {FileName} next to the GPU runtime for Wine.");
            return null;
        }
        catch (Exception ex)
        {
            log?.Warn($"Could not place {FileName} for Wine: {ex.Message}");
            return "Could not set up the Vulkan loader for Wine, so the GPU cannot be used.";
        }
    }
}
