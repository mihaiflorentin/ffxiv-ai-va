namespace AIVoiceActing.Infrastructure.Onnx;

using System.Collections.Concurrent;
using System.Security.Cryptography;

/// <summary>One model file of a verified combination: pinned size and sha256.</summary>
public sealed record VerifiedFile(string FileName, long SizeBytes, string Sha256);

/// <summary>A graph plus its external weights, verified to run on a specific WebGPU runtime version.</summary>
public sealed record VerifiedCombo(string EpVersion, string Label, string GraphFile, IReadOnlyList<VerifiedFile> Files);

/// <summary>
/// The pinned allowlist of (WebGPU runtime version, model file hashes) pairs that were run end to
/// end on the GPU (docs/plans/2026-10-gpu-spike-results.md). The runtime aborts the whole process
/// on graphs it cannot handle, and a toy self-test cannot tell which ones, so anything outside this
/// list runs on the CPU. The hashes are literals on purpose: bumping a model in
/// <c>models-catalog.json</c> must not silently widen the list (a test compares the two).
/// </summary>
public sealed class VerifiedGpuCombos
{
    /// <summary>The Microsoft.ML.OnnxRuntime.EP.WebGpu release the plugin ships. 0.3.0 and 0.4.0 abort on Turbo graphs.</summary>
    public const string WebGpuEpVersion = "0.2.1";

    public static IReadOnlyList<VerifiedCombo> Combos { get; } =
    [
        new VerifiedCombo(
            WebGpuEpVersion,
            "Chatterbox Turbo language model (fp16)",
            "turbo-language-model-fp16.onnx",
            [
                new VerifiedFile("turbo-language-model-fp16.onnx", 209456, "396b35570dd1dbc5f537ffc932c3e69130ab8a09bf1390469d92338c8eae7da4"),
                new VerifiedFile("language_model_fp16.onnx_data", 634862406, "d730d6437126d3e232747b26e2a7adece68844f22133277ca5012298ffe36f93"),
            ]),
        new VerifiedCombo(
            WebGpuEpVersion,
            "Chatterbox Turbo language model (q4f16)",
            "turbo-language-model-q4f16.onnx",
            [
                new VerifiedFile("turbo-language-model-q4f16.onnx", 276803, "4c5d83674c52bb71683a1ea1f60e2608b80cda48aafce67e8f8c03162c474ad5"),
                new VerifiedFile("language_model_q4f16.onnx_data", 183981430, "f084f21f977e365e76e622d2958c309ff894537f7b7133ac7fc5ce8fc055d6f9"),
            ]),
    ];

    private readonly Func<string, string> sha256Of;
    private readonly ConcurrentDictionary<(string Path, long Length, long Ticks), string> hashCache = new();

    public VerifiedGpuCombos(Func<string, string>? sha256Of = null)
    {
        this.sha256Of = sha256Of ?? Sha256File;
    }

    /// <summary>
    /// True when <paramref name="graphFileName"/> under <paramref name="modelsDir"/> belongs to a combo
    /// verified for <paramref name="epVersion"/>. <paramref name="cheap"/> compares names and sizes only
    /// (safe to call from the UI thread); the full check also hashes every file once per process
    /// (about two seconds for the fp16 weights), so run it on the engine's load thread.
    /// </summary>
    public bool IsVerified(string epVersion, string modelsDir, string graphFileName, bool cheap = false)
    {
        var combo = Combos.FirstOrDefault(c =>
            c.EpVersion == epVersion
            && string.Equals(c.GraphFile, graphFileName, StringComparison.OrdinalIgnoreCase));
        if (combo is null)
        {
            return false;
        }

        foreach (var file in combo.Files)
        {
            var info = new FileInfo(Path.Combine(modelsDir, file.FileName));
            if (!info.Exists || info.Length != file.SizeBytes)
            {
                return false;
            }

            if (cheap)
            {
                continue;
            }

            var key = (info.FullName, info.Length, info.LastWriteTimeUtc.Ticks);
            if (!this.hashCache.TryGetValue(key, out var hash))
            {
                try
                {
                    hash = this.sha256Of(info.FullName);
                }
                catch (IOException)
                {
                    return false;
                }

                this.hashCache[key] = hash;
            }

            if (!string.Equals(hash, file.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }

    private static string Sha256File(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }
}
