namespace AIVoiceActing.Infrastructure.Onnx;

using System.Text.Json;
using AIVoiceActing.Ports;

/// <summary>
/// Pinned model assets. The legacy Chatterbox (onnx-community/chatterbox-ONNX, MIT) and F5 groups are
/// retired engines kept only so the Models page can still show and delete their files; the Turbo
/// groups below are the live Chatterbox. The old constants stay because the catalog data and its tests use them.
/// Sizes verified byte-exact against the HF API tree on 2026-09-12 and against
/// .superpowers/sdd/.../ref-chatterbox-recipe.md; sha256 values are the HF API
/// LFS oids (absent for non-LFS files → null). The optional "director" group
/// (Qwen3-0.6B ONNX) is deliberately NOT pinned yet: an export exists
/// (onnx-community/Qwen3-0.6B-ONNX) but usable-for-direction is verified in Step 4.
public static class ModelCatalog
{
    /// <summary>Resolve URLs are RepoBaseUrl + ModelAsset.FileName.</summary>
    public const string RepoBaseUrl = "https://huggingface.co/onnx-community/chatterbox-ONNX/resolve/main/";

    public const string ChatterboxRequiredGroup = "chatterbox-required";

    public const string SpeechEncoderFileName = "onnx/speech_encoder.onnx";
    public const string SpeechEncoderDataFileName = "onnx/speech_encoder.onnx_data";
    public const string EmbedTokensFileName = "onnx/embed_tokens.onnx";
    public const string EmbedTokensDataFileName = "onnx/embed_tokens.onnx_data";
    public const string ConditionalDecoderFileName = "onnx/conditional_decoder.onnx";
    public const string ConditionalDecoderDataFileName = "onnx/conditional_decoder.onnx_data";
    public const string LanguageModelQ4FileName = "onnx/language_model_q4.onnx";
    public const string LanguageModelQ4DataFileName = "onnx/language_model_q4.onnx_data";
    public const string TokenizerJsonFileName = "tokenizer.json";
    public const string DefaultVoiceFileName = "default_voice.wav";

    /// <summary>
    /// Optional fp32 LM group: the readme pipeline's full-precision model. The Step 3 gate
    /// found the q4 export degenerating on short prompts (immediate STOP); fp32 is the
    /// known-good fallback at ~2 GB. Optional — q4 stays the small default download.
    public const string ChatterboxFp32LmGroup = "chatterbox-fp32-lm";
    public const string LanguageModelFp32FileName = "onnx/language_model.onnx";
    public const string LanguageModelFp32DataFileName = "onnx/language_model.onnx_data";

    /// <summary>
    /// Kokoro-82M v1.0 (Apache-2.0) from KokoroSharpBinaries: the CPU real-time engine.
    /// This export names its inputs tokens/style/speed — what KokoroSharp's Infer feeds.
    /// (The onnx-community/kokoro-82M-v1.0-ONNX export names the input "input_ids" and is
    /// NOT loadable by KokoroSharp.) fp32 on purpose — quantized exports hit the
    /// MatMulNBits native-kernel class that crashed under Zen 5/AVX-512. Voice banks ship
    /// in the KokoroSharp NuGet package, so the model file is the only provisioned asset.
    /// </summary>
    public const string KokoroGroup = "kokoro";
    public const string KokoroModelFileName = "kokoro-v1.0.onnx";
    public const string KokoroRepoBaseUrl = "https://github.com/Lyrcaxis/KokoroSharpBinaries/releases/download/v2.0.0/";
    public const string KokoroRemoteFileName = "kokoro.onnx";
    /// <summary>
    /// F5-TTS English ONNX export (nibor1896, from DakeQQ tooling): the voice-acting
    /// engine. Zero-shot cloning — each reference clip (voices-f5/&lt;id&gt;.wav + .txt)
    /// IS the voice, and its delivery drives the output's emotion. Weights are
    /// CC-BY-NC (fine for personal use; the plugin never ships them). The DiT
    /// transformer runs an iterative denoise loop: roughly real-time on CPU.
    /// </summary>
    public const string F5Group = "f5";
    public const string F5RepoBaseUrl = "https://huggingface.co/nibor1896/F5-TTS-English-ONNX/resolve/main/";
    public const string F5PreprocessFileName = "f5-preprocess.onnx";
    public const string F5TransformerFileName = "f5-transformer.onnx";
    public const string F5DecodeFileName = "f5-decode.onnx";
    public const string F5VocabFileName = "f5-vocab.txt";

    /// <summary>
    /// Chatterbox Turbo ONNX (ResembleAI official export): distilled Chatterbox with
    /// native paralinguistic tags ([laugh]/[chuckle]/[cough] inline in text). Same
    /// four-session architecture as the legacy export, but 24 layers (vs 30), arange
    /// position_ids, no exaggeration input, and a GPT2 BPE tokenizer (3.5 MB). The default
    /// group holds the fp32 sessions (~3.2 GB): the int8-quantized kernels native-crashed inside
    /// onnxruntime.dll in-game (same Zen5/AVX-512 kernel class that forced the fp32 LM
    /// on legacy), while the fp32 set ran clean on the same hardware.
    /// </summary>
    public const string TurboGroup = "turbo";
    public const string TurboRepoBaseUrl = "https://huggingface.co/ResembleAI/chatterbox-turbo-ONNX/resolve/main/";
    // Graph files may carry the turbo- prefix (renaming a graph is safe), but the
    // external-data siblings MUST keep their exact remote names — the graphs reference
    // them by relative path and ONNX Runtime validates the name on load.
    public const string TurboSpeechEncoderFileName = "turbo-speech-encoder.onnx";
    public const string TurboSpeechEncoderDataFileName = "speech_encoder.onnx_data";
    public const string TurboEmbedTokensFileName = "turbo-embed-tokens.onnx";
    public const string TurboEmbedTokensDataFileName = "embed_tokens.onnx_data";
    public const string TurboLanguageModelFileName = "turbo-language-model.onnx";
    public const string TurboLanguageModelDataFileName = "language_model.onnx_data";
    public const string TurboConditionalDecoderFileName = "turbo-conditional-decoder.onnx";
    public const string TurboConditionalDecoderDataFileName = "conditional_decoder.onnx_data";

    /// <summary>True when the local file belongs to the Turbo engine (prefix or pinned data sibling).</summary>
    public static bool IsTurboAsset(string fileName) =>
        fileName.StartsWith("turbo-", StringComparison.Ordinal)
        || fileName is TurboSpeechEncoderDataFileName
            or TurboEmbedTokensDataFileName
            or TurboLanguageModelDataFileName
            or TurboConditionalDecoderDataFileName
        || Assets.Any(a => a.Asset.FileName == fileName && a.Group.StartsWith("turbo", StringComparison.Ordinal));
    public const string TurboTokenizerJsonFileName = "turbo-tokenizer.json";

    /// <summary>
    /// Optional emotion classifier (SamLowe/roberta-base-go_emotions-onnx, MIT): 28-label
    /// GoEmotions text classifier driving delivery. fp32 on purpose (the int8 export uses
    /// the kernel class that crashed in-game). Size and sha256 from the HF API tree,
    /// 2026-10-17.
    /// </summary>
    public const string EmotionGroup = "emotion";
    public const string EmotionRepoBaseUrl = "https://huggingface.co/SamLowe/roberta-base-go_emotions-onnx/resolve/main/";
    public const string EmotionModelFileName = "emotion-classifier.onnx";
    public const string EmotionTokenizerFileName = "emotion-tokenizer.json";
    public const string TurboDefaultVoiceFileName = "turbo-default-voice.wav";

    /// <summary>One pinned catalog entry: the port asset plus integrity metadata.</summary>
    /// <param name="Asset">Port-level asset (name, file, size, optionality).</param>
    /// <param name="Sha256">Pinned sha256 (HF LFS oid); null when the file is not LFS-tracked.</param>
    /// <param name="Group">Download-group key for the Models tab.</param>
    public sealed record CatalogAsset(ModelAsset Asset, string? Sha256, string Group, string Url);

    /// <summary>
    /// Precision variants of the Turbo set (same ResembleAI repo). They carry only the four
    /// graph+weights pairs; the tokenizer and default voice come from <see cref="TurboGroup"/>.
    /// </summary>
    public const string TurboFp16Group = "turbo-fp16";
    public const string TurboQ4F16Group = "turbo-q4f16";

    /// <summary>
    /// What every Turbo install needs whatever its precision: the tokenizer and the default reference voice
    /// (4 MB). Split from the graphs so a graphics card needs only the small q4f16 build next to it.
    /// </summary>
    public const string TurboBaseGroup = "turbo-base";

    private const string CatalogResourceName = "models-catalog.json";

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    /// <summary>All pinned assets across every group, in stable display order (loaded from the
    /// embedded models-catalog.json).</summary>
    public static IReadOnlyList<CatalogAsset> Assets { get; } = LoadCatalog();

    /// <summary>Assets of one download group, in catalog order.</summary>
    public static IReadOnlyList<ModelAsset> GroupAssets(string group) =>
        [.. Assets.Where(a => a.Group == group).Select(a => a.Asset)];

    private static IReadOnlyList<CatalogAsset> LoadCatalog()
    {
        using var stream = typeof(ModelCatalog).Assembly.GetManifestResourceStream(CatalogResourceName)
            ?? throw new InvalidOperationException($"Embedded resource \"{CatalogResourceName}\" is missing.");
        var file = JsonSerializer.Deserialize<CatalogFile>(stream, JsonOptions)
            ?? throw new InvalidOperationException("models-catalog.json is empty.");
        return
        [
            .. file.Assets.Select(e => new CatalogAsset(
                new ModelAsset(e.Name, e.Local, e.SizeBytes, e.Optional, e.Precision),
                e.Sha256,
                e.Group,
                (e.BaseUrl ?? $"https://huggingface.co/{e.Repo}/resolve/main/") + e.Remote)),
        ];
    }

    private sealed record CatalogFile(List<CatalogEntry> Assets);

    private sealed record CatalogEntry(
        string Group,
        string Name,
        string Precision,
        string? Repo,
        string? BaseUrl,
        string Remote,
        string Local,
        long SizeBytes,
        string? Sha256,
        bool Optional = false);

    /// <summary>Required Chatterbox assets (the whole engine; optionals would be excluded).</summary>
    public static IReadOnlyList<ModelAsset> ChatterboxRequiredAssets { get; } =
        Assets.Where(a => a.Group == ChatterboxRequiredGroup).Select(a => a.Asset).ToArray();

    /// <summary>The tokenizer and default voice every Turbo install needs (see <see cref="TurboBaseGroup"/>).</summary>
    public static IReadOnlyList<ModelAsset> TurboBaseAssets { get; } =
        Assets.Where(a => a.Group == TurboBaseGroup).Select(a => a.Asset).ToArray();

    /// <summary>The full-size Turbo install (the CPU choice): the base files plus the fp32 graphs and weights.</summary>
    public static IReadOnlyList<ModelAsset> TurboRequiredAssets { get; } =
        Assets.Where(a => a.Group is TurboBaseGroup or TurboGroup).Select(a => a.Asset).ToArray();

    /// <summary>Resolve URL for a catalog asset; unknown files fall back to the legacy Chatterbox repo.</summary>
    public static string UrlFor(ModelAsset asset) =>
        Assets.FirstOrDefault(a => a.Asset.FileName == asset.FileName)?.Url ?? RepoBaseUrl + asset.FileName;

    public static string? Sha256For(string fileName) =>
        Assets.FirstOrDefault(a => a.Asset.FileName == fileName)?.Sha256;
}

