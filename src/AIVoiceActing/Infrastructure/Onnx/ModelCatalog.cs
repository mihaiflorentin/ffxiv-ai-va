namespace AIVoiceActing.Infrastructure.Onnx;

using AIVoiceActing.Ports;

/// <summary>
/// Pinned Chatterbox ONNX model assets (onnx-community/chatterbox-ONNX, MIT).
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

    /// <summary>One pinned catalog entry: the port asset plus integrity metadata.</summary>
    /// <param name="Asset">Port-level asset (name, file, size, optionality).</param>
    /// <param name="Sha256">Pinned sha256 (HF LFS oid); null when the file is not LFS-tracked.</param>
    /// <param name="Group">Download-group key for the Models tab.</param>
    public sealed record CatalogAsset(ModelAsset Asset, string? Sha256, string Group);

    /// <summary>All pinned assets across every group, in stable display order.</summary>
    public static IReadOnlyList<CatalogAsset> Assets { get; } =
    [
        new(
            new ModelAsset("Speech encoder", SpeechEncoderFileName, 1184608),
            "8f1c8a0f89b77bf9cd5dd8f2e034eb2c79dc00fe70d41196b28c257643b00ccb",
            ChatterboxRequiredGroup),
        new(
            new ModelAsset("Speech encoder weights", SpeechEncoderDataFileName, 591274880),
            "04431dcef6325c54b02de2219845888b464bcd1f1ac2f8839c2fecd1ed2ef294",
            ChatterboxRequiredGroup),
        new(
            new ModelAsset("Text embedding", EmbedTokensFileName, 13286),
            "160722ec14789f616abdb1e31916cbbf9223c03fde0ab546d64ca74fb72e430b",
            ChatterboxRequiredGroup),
        new(
            new ModelAsset("Text embedding weights", EmbedTokensDataFileName, 61640704),
            "898c563c3a5ca1b9ea10ce89b0cdcf252b0bb5ab460dfc4eadea003b56e5d2ee",
            ChatterboxRequiredGroup),
        new(
            new ModelAsset("Conditional decoder", ConditionalDecoderFileName, 6350448),
            "1656d0d31332bae1854839959a3139300ebb67c178651dfa3f8c5fbfa5351351",
            ChatterboxRequiredGroup),
        new(
            new ModelAsset("Conditional decoder weights", ConditionalDecoderDataFileName, 533970816),
            "51d58345a272747665ec9d5bb61e01835258a940e321a288582ac4c18cf01b5a",
            ChatterboxRequiredGroup),
        new(
            new ModelAsset("Language model (q4)", LanguageModelQ4FileName, 227911),
            "7f8cdca83b2493536cbf3acf421199808a3d68736f55f4eabd20ef8a99da4313",
            ChatterboxRequiredGroup),
        new(
            new ModelAsset("Language model weights (q4)", LanguageModelQ4DataFileName, 353621248),
            "5203d1e83c159316f9923c5c83759f6a34f87be1322ce4ad0facd9fc4aef4790",
            ChatterboxRequiredGroup),
        new(
            new ModelAsset("Tokenizer", TokenizerJsonFileName, 28543),
            Sha256: null,
            ChatterboxRequiredGroup),
        new(
            new ModelAsset("Default reference voice", DefaultVoiceFileName, 714320),
            "3ebc531cdaba358a327099c1c4f0448026719957bcf4d8e9868767f227e02f4e",
            ChatterboxRequiredGroup),
        new(
            new ModelAsset("Language model (fp32)", LanguageModelFp32FileName, 171387, Optional: true),
            "861a34585605e8ad671051788afc495dcbeaee833a41523a1b33aded9c3babc7",
            ChatterboxFp32LmGroup),
        new(
            new ModelAsset("Language model weights (fp32)", LanguageModelFp32DataFileName, 2080632832, Optional: true),
            "efe9a1173c40d50bc651cb96ebff9f23d6f20d5b3a11b0685510e3a3facdbcf1",
            ChatterboxFp32LmGroup),
        new(
            new ModelAsset("Kokoro model (fp32)", KokoroModelFileName, 325508342),
            "0cfd5e79aab70a3d8c1a57dc639835110ddb32c9f5ff4fdd1f4db202ea43bb05",
            KokoroGroup),
        new(
            new ModelAsset("F5 preprocess", F5PreprocessFileName, 68549853),
            "e71d3ed14e90ba3fc1e83560512e86a771e25b6f0be7789b2cf53a5c9ba5617d",
            F5Group),
        new(
            new ModelAsset("F5 transformer", F5TransformerFileName, 1321718282),
            "c63aeb96953ccae551df6717d892f73a8408e59f1a6fe8edc2aed8063bd195b8",
            F5Group),
        new(
            new ModelAsset("F5 decode", F5DecodeFileName, 62550703),
            "a16fea891beb4889b47e5987b80c841bb996beaccc1d1fe27a1f9323a089a6da",
            F5Group),
        new(
            new ModelAsset("F5 vocab", F5VocabFileName, 13800),
            "2a05f992e00af9b0bd3800a8d23e78d520dbd705284ed2eedb5f4bd29398fa3c",
            F5Group),
    ];

    /// <summary>Required Chatterbox assets (the whole engine; optionals would be excluded).</summary>
    public static IReadOnlyList<ModelAsset> ChatterboxRequiredAssets { get; } =
        Assets.Where(a => a.Group == ChatterboxRequiredGroup).Select(a => a.Asset).ToArray();

    /// <summary>Resolve URLs are RepoBaseUrl + ModelAsset.FileName; Kokoro's remote file
    /// is a GitHub release asset and F5's remote names differ from the flat local names
    /// (catalog constants map them).</summary>
    public static string UrlFor(ModelAsset asset) => asset.FileName switch
    {
        F5PreprocessFileName => F5RepoBaseUrl + "F5_Preprocess.onnx",
        F5TransformerFileName => F5RepoBaseUrl + "F5_Transformer.onnx",
        F5DecodeFileName => F5RepoBaseUrl + "F5_Decode.onnx",
        F5VocabFileName => F5RepoBaseUrl + "vocab.txt",
        KokoroModelFileName => KokoroRepoBaseUrl + KokoroRemoteFileName,
        _ => RepoBaseUrl + asset.FileName,
    };

    public static string? Sha256For(string fileName) =>
        Assets.FirstOrDefault(a => a.Asset.FileName == fileName)?.Sha256;
}
