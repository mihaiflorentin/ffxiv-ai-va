namespace AIVoiceActing.Infrastructure.Onnx;

using System.Runtime.InteropServices;
using AIVoiceActing.Ports;

/// <summary>
/// Preloads the flat native libraries shipped next to the plugin dll before any
/// P/Invoke binds them. Dalamud resolves unmanaged dlls through the plugin deps.json,
/// whose runtimes/win-x64/native entries do not exist in the packed layout (natives
/// are flattened to the plugin root), so binding by bare name fails with
/// DllNotFoundException 0x8007007E — in game and under Proton/Wine alike. Loading the
/// absolute paths first registers the modules under their canonical file names; later
/// DllImports ("onnxruntime", "hf_tokenizers", …) attach to the loaded copies.
/// </summary>
public static class NativeLibraryPreloader
{
    /// <summary>
    /// Native modules the engine binds at P/Invoke time, in load order:
    /// providers_shared must precede onnxruntime (the DML provider resolves it),
    /// DirectML before the session registers the DML EP.
    /// </summary>
    public static readonly string[] EngineNatives =
    [
        "onnxruntime_providers_shared.dll",
        "DirectML.dll",
        "onnxruntime.dll",
        "hf_tokenizers.dll",
    ];

    // Keep the raw module handles rooted for the process lifetime. They are plain
    // nint values (NativeLibrary.TryLoad out param) — nothing will ever free them.
    private static readonly List<nint> Loaded = [];

    /// <summary>
    /// Resolves the plugin directory from an assembly location. Dalamud loads plugin
    /// assemblies with an empty <see cref="System.Reflection.Assembly.Location"/>, so
    /// callers must pass <c>PluginInterface.AssemblyLocation</c>; this tolerates a null
    /// or empty input and returns null instead of throwing.
    /// </summary>
    public static string? ResolvePluginDirectory(string? location)
    {
        if (string.IsNullOrWhiteSpace(location))
        {
            return null;
        }

        return File.Exists(location) ? Path.GetDirectoryName(location) : location;
    }

    /// <summary>
    /// Best-effort preload: null-tolerant, logs and continues on failure so the engine's
    /// own error surfaces later with its real message instead of a missing-file cascade.
    /// Must never throw — callers run it from plugin construction, where an exception
    /// fails the whole plugin load ("Load failed" in the installer).
    /// </summary>
    public static void Preload(string? directory, IReadOnlyCollection<string> fileNames, ILogSink? log = null)
    {
        if (string.IsNullOrWhiteSpace(directory))
        {
            log?.Warn("Native preload skipped: plugin directory unknown.");
            return;
        }

        foreach (var fileName in fileNames)
        {
            var fullPath = Path.Combine(directory, fileName);
            if (!File.Exists(fullPath))
            {
                log?.Warn($"Native library not found next to the plugin: {fileName}");
                continue;
            }

            if (NativeLibrary.TryLoad(fullPath, out var handle))
            {
                Loaded.Add(handle);
                log?.Info($"Preloaded native library {fileName}.");
            }
            else
            {
                log?.Warn($"Native library failed to load: {fileName}");
            }
        }
    }
}
