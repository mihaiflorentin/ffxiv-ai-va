namespace AIVoiceActing.Infrastructure.Onnx;

using AIVoiceActing.Infrastructure.Storage;
using AIVoiceActing.Ports;

/// <summary>Numeric precision of one Turbo file set. The names match <c>EngineOptions.Precision</c>.</summary>
public enum TurboPrecision
{
    Fp32,
    Fp16,
    Q4F16,
}

/// <summary>
/// The four graph files of one Turbo precision (local names from <c>models-catalog.json</c>). Their
/// external-data siblings sit next to them and are checked through the catalog group.
/// </summary>
public sealed record TurboFiles(
    TurboPrecision Precision,
    string Encoder,
    string Embed,
    string Lm,
    string Decoder,
    string CatalogGroup)
{
    public static TurboFiles For(TurboPrecision precision) => precision switch
    {
        TurboPrecision.Fp16 => new TurboFiles(
            precision,
            "turbo-speech-encoder-fp16.onnx",
            "turbo-embed-tokens-fp16.onnx",
            "turbo-language-model-fp16.onnx",
            "turbo-conditional-decoder-fp16.onnx",
            ModelCatalog.TurboFp16Group),
        TurboPrecision.Q4F16 => new TurboFiles(
            precision,
            "turbo-speech-encoder-q4f16.onnx",
            "turbo-embed-tokens-q4f16.onnx",
            "turbo-language-model-q4f16.onnx",
            "turbo-conditional-decoder-q4f16.onnx",
            ModelCatalog.TurboQ4F16Group),
        _ => new TurboFiles(
            precision,
            ModelCatalog.TurboSpeechEncoderFileName,
            ModelCatalog.TurboEmbedTokensFileName,
            ModelCatalog.TurboLanguageModelFileName,
            ModelCatalog.TurboConditionalDecoderFileName,
            ModelCatalog.TurboGroup),
    };

    /// <summary>Short label for logs and the render-cache key ("fp32", "fp16", "q4f16").</summary>
    public string Label => Describe(this.Precision);

    public static string Describe(TurboPrecision precision) => precision switch
    {
        TurboPrecision.Fp16 => "fp16",
        TurboPrecision.Q4F16 => "q4f16",
        _ => "fp32",
    };

    /// <summary>True when every file of the set (graphs and weights) is on disk at its pinned size.</summary>
    public bool IsInstalled(string modelsDir)
    {
        var assets = ModelCatalog.GroupAssets(this.CatalogGroup);
        return assets.Count > 0 && assets.All(asset =>
        {
            var path = Path.Combine(modelsDir, asset.FileName);
            return File.Exists(path)
                && (asset.SizeBytes is not { } size || FileModelStore.SizeWithinTolerance(new FileInfo(path).Length, size));
        });
    }
}

/// <summary>What the precision setting asks for. <see cref="Auto"/> lets the device plan decide.</summary>
public enum TurboPrecisionRequest
{
    Auto,
    Fp32,
    Fp16,
    Q4F16,
}

/// <summary>Pure rules for which Turbo precision a setting means and which sets to consider, best first.</summary>
public static class TurboModelSet
{
    public static TurboPrecisionRequest Parse(string? setting) => setting?.Trim().ToLowerInvariant() switch
    {
        "fp32" => TurboPrecisionRequest.Fp32,
        "fp16" => TurboPrecisionRequest.Fp16,
        "q4f16" => TurboPrecisionRequest.Q4F16,
        _ => TurboPrecisionRequest.Auto,
    };

    /// <summary>
    /// The request after the processor choice: full-size files only run on the processor, so a full-size pin
    /// means nothing unless the player chose the processor. With Graphics card (or Automatic) it plans like Automatic,
    /// which still ends on the full-size files when no graphics card can be used.
    /// </summary>
    public static TurboPrecisionRequest Effective(TurboPrecisionRequest request, DeviceMode mode) =>
        request == TurboPrecisionRequest.Fp32 && mode != DeviceMode.Cpu ? TurboPrecisionRequest.Auto : request;

    /// <summary>
    /// Sets worth trying in order. Auto prefers the smallest GPU-verified set (q4f16, then fp16) and
    /// ends at fp32, which is always installed with the engine and is the CPU choice. An explicit
    /// fp16 or q4f16 falls back to fp32 when that set was not downloaded.
    /// </summary>
    public static IReadOnlyList<TurboPrecision> Candidates(TurboPrecisionRequest request) => request switch
    {
        TurboPrecisionRequest.Fp32 => [TurboPrecision.Fp32],
        TurboPrecisionRequest.Fp16 => [TurboPrecision.Fp16, TurboPrecision.Fp32],
        TurboPrecisionRequest.Q4F16 => [TurboPrecision.Q4F16, TurboPrecision.Fp32],
        _ => [TurboPrecision.Q4F16, TurboPrecision.Fp16, TurboPrecision.Fp32],
    };
}
