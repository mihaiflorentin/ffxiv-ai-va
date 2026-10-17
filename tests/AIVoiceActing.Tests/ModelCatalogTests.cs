namespace AIVoiceActing.Tests;

using AIVoiceActing.Infrastructure.Onnx;
using AIVoiceActing.Ports;
using Xunit;

/// <summary>
/// Pins the Chatterbox catalog: literal byte sizes from ref-chatterbox-recipe.md
/// (verified byte-exact against the HF API tree), sha256 pins for LFS files,
/// and the shape of the required group.
/// </summary>
public sealed class ModelCatalogTests
{
    [Fact]
    public void PinnedSizes_MatchRecipeLiterals()
    {
        Assert.Equal(1184608, Size("onnx/speech_encoder.onnx"));
        Assert.Equal(591274880, Size("onnx/speech_encoder.onnx_data"));
        Assert.Equal(13286, Size("onnx/embed_tokens.onnx"));
        Assert.Equal(61640704, Size("onnx/embed_tokens.onnx_data"));
        Assert.Equal(6350448, Size("onnx/conditional_decoder.onnx"));
        Assert.Equal(533970816, Size("onnx/conditional_decoder.onnx_data"));
        Assert.Equal(227911, Size("onnx/language_model_q4.onnx"));
        Assert.Equal(353621248, Size("onnx/language_model_q4.onnx_data"));
        Assert.Equal(28543, Size("tokenizer.json"));
        Assert.Equal(714320, Size("default_voice.wav"));
    }

    [Fact]
    public void Sha256_PinsPresentForLfsFilesOnly()
    {
        // The 8 ONNX files + default_voice.wav are LFS-tracked (oids from the HF API).
        foreach (var name in new[]
                 {
                     "onnx/speech_encoder.onnx", "onnx/speech_encoder.onnx_data",
                     "onnx/embed_tokens.onnx", "onnx/embed_tokens.onnx_data",
                     "onnx/conditional_decoder.onnx", "onnx/conditional_decoder.onnx_data",
                     "onnx/language_model_q4.onnx", "onnx/language_model_q4.onnx_data",
                     "default_voice.wav",
                 })
        {
            Assert.Matches("^[0-9a-f]{64}$", ModelCatalog.Sha256For(name));
        }

        // tokenizer.json is stored plain → size-only integrity.
        Assert.Null(ModelCatalog.Sha256For("tokenizer.json"));
    }

    [Fact]
    public void RequiredGroup_HoldsExactlyTheTenChatterboxAssets_AndKokoroAndF5AreSeparate()
    {
        Assert.Equal(10, ModelCatalog.ChatterboxRequiredAssets.Count);
        Assert.Equal(29 + 16, ModelCatalog.Assets.Count);
        Assert.Equal(2, ModelCatalog.TurboBaseAssets.Count);

        var emotion = ModelCatalog.Assets.Where(a => a.Group == ModelCatalog.EmotionGroup).ToArray();
        Assert.Equal(2, emotion.Length);
        Assert.All(emotion, a => Assert.True(a.Asset.Optional));
        Assert.Equal(
            ModelCatalog.EmotionRepoBaseUrl + "onnx/model.onnx",
            ModelCatalog.UrlFor(emotion.Single(a => a.Asset.FileName == ModelCatalog.EmotionModelFileName).Asset));
        Assert.All(ModelCatalog.Assets.Where(a => a.Group == ModelCatalog.ChatterboxRequiredGroup),
            a => Assert.False(a.Asset.Optional));
        Assert.All(ModelCatalog.Assets.Where(a => a.Group == ModelCatalog.ChatterboxFp32LmGroup), a => Assert.True(a.Asset.Optional));

        var kokoro = ModelCatalog.Assets.Where(a => a.Group == ModelCatalog.KokoroGroup).ToArray();
        Assert.Single(kokoro);
        Assert.False(kokoro[0].Asset.Optional);
        Assert.NotNull(kokoro[0].Sha256);
        Assert.Equal(
            ModelCatalog.KokoroRepoBaseUrl + ModelCatalog.KokoroRemoteFileName,
            ModelCatalog.UrlFor(kokoro[0].Asset));

        var f5 = ModelCatalog.Assets.Where(a => a.Group == ModelCatalog.F5Group).ToArray();
        Assert.Equal(4, f5.Length);
        Assert.All(f5, a => Assert.False(a.Asset.Optional));
        Assert.All(f5, a => Assert.NotNull(a.Sha256));
        Assert.Equal(
            ModelCatalog.F5RepoBaseUrl + "F5_Transformer.onnx",
            ModelCatalog.UrlFor(f5.Single(a => a.Asset.FileName == ModelCatalog.F5TransformerFileName).Asset));

        var turbo = ModelCatalog.Assets.Where(a => a.Group is ModelCatalog.TurboGroup or ModelCatalog.TurboBaseGroup).ToArray();
        Assert.Equal(10, turbo.Length);
        Assert.Equal(8, ModelCatalog.Assets.Count(a => a.Group == ModelCatalog.TurboGroup));
        Assert.All(turbo, a => Assert.False(a.Asset.Optional));
        Assert.Equal(
            ModelCatalog.TurboRepoBaseUrl + "onnx/language_model.onnx",
            ModelCatalog.UrlFor(turbo.Single(a => a.Asset.FileName == ModelCatalog.TurboLanguageModelFileName).Asset));
        Assert.Equal(
            ModelCatalog.TurboRepoBaseUrl + "tokenizer.json",
            ModelCatalog.UrlFor(turbo.Single(a => a.Asset.FileName == ModelCatalog.TurboTokenizerJsonFileName).Asset));
    }

    [Fact]
    public void UrlFor_ResolvesAgainstPinnedRepo()
    {
        var asset = ModelCatalog.ChatterboxRequiredAssets[0];
        Assert.Equal("https://huggingface.co/onnx-community/chatterbox-ONNX/resolve/main/" + asset.FileName,
            ModelCatalog.UrlFor(asset));
    }

    // Local paths of the pre-catalog constants: existing installs must stay "downloaded".
    private static readonly (string Group, string Local)[] LegacyPaths =
    [
        ("chatterbox-required", "onnx/speech_encoder.onnx"),
        ("chatterbox-required", "onnx/speech_encoder.onnx_data"),
        ("chatterbox-required", "onnx/embed_tokens.onnx"),
        ("chatterbox-required", "onnx/embed_tokens.onnx_data"),
        ("chatterbox-required", "onnx/conditional_decoder.onnx"),
        ("chatterbox-required", "onnx/conditional_decoder.onnx_data"),
        ("chatterbox-required", "onnx/language_model_q4.onnx"),
        ("chatterbox-required", "onnx/language_model_q4.onnx_data"),
        ("chatterbox-required", "tokenizer.json"),
        ("chatterbox-required", "default_voice.wav"),
        ("chatterbox-fp32-lm", "onnx/language_model.onnx"),
        ("chatterbox-fp32-lm", "onnx/language_model.onnx_data"),
        ("kokoro", "kokoro-v1.0.onnx"),
        ("f5", "f5-preprocess.onnx"),
        ("f5", "f5-transformer.onnx"),
        ("f5", "f5-decode.onnx"),
        ("f5", "f5-vocab.txt"),
        ("turbo-base", "turbo-tokenizer.json"), // split out of "turbo": a graphics card needs these without the 3.3 GB fp32 files
        ("turbo-base", "turbo-default-voice.wav"),
        ("turbo", "turbo-speech-encoder.onnx"),
        ("turbo", "speech_encoder.onnx_data"),
        ("turbo", "turbo-embed-tokens.onnx"),
        ("turbo", "embed_tokens.onnx_data"),
        ("turbo", "turbo-language-model.onnx"),
        ("turbo", "language_model.onnx_data"),
        ("turbo", "turbo-conditional-decoder.onnx"),
        ("turbo", "conditional_decoder.onnx_data"),
        ("emotion", "emotion-classifier.onnx"),
        ("emotion", "emotion-tokenizer.json"),
    ];

    [Fact]
    public void ExistingLocalPaths_AreUnchanged_AndInTheSameGroup()
    {
        foreach (var (group, local) in LegacyPaths)
        {
            Assert.Contains(ModelCatalog.Assets, a => a.Asset.FileName == local && a.Group == group);
        }

        Assert.Equal(LegacyPaths.Select(p => p.Local), ModelCatalog.Assets.Take(LegacyPaths.Length).Select(a => a.Asset.FileName));
    }

    [Fact]
    public void LocalPaths_AreUnique()
    {
        var duplicates = ModelCatalog.Assets.GroupBy(a => a.Asset.FileName).Where(g => g.Count() > 1).Select(g => g.Key);
        Assert.Empty(duplicates);
    }

    [Fact]
    public void EveryAsset_HasSizeAndPrecision_AndLfsFilesHaveSha()
    {
        Assert.All(ModelCatalog.Assets, a =>
        {
            Assert.True(a.Asset.SizeBytes > 0, a.Asset.FileName);
            Assert.False(string.IsNullOrEmpty(a.Asset.Precision), a.Asset.FileName);
        });

        // Only the plain-git tokenizers lack an LFS oid; every other file is sha-pinned.
        var unpinned = ModelCatalog.Assets.Where(a => a.Sha256 is null).Select(a => a.Asset.FileName).Order();
        Assert.Equal(["emotion-tokenizer.json", "tokenizer.json", "turbo-tokenizer.json"], unpinned);
        Assert.All(ModelCatalog.Assets.Where(a => a.Sha256 is not null), a => Assert.Matches("^[0-9a-f]{64}$", a.Sha256));
    }

    [Fact]
    public void TurboPrecisionGroups_ArePresent_WithPinnedTotals()
    {
        Assert.Equal(3_323_391_436, Total(ModelCatalog.TurboGroup) + Total(ModelCatalog.TurboBaseGroup));
        Assert.Equal(3_562_272 + 714_320, Total(ModelCatalog.TurboBaseGroup));
        Assert.Equal(1_661_100_256, Total(ModelCatalog.TurboFp16Group));
        Assert.Equal(561_806_478, Total(ModelCatalog.TurboQ4F16Group));

        foreach (var (group, precision) in new[] { (ModelCatalog.TurboFp16Group, "fp16"), (ModelCatalog.TurboQ4F16Group, "q4f16") })
        {
            var assets = ModelCatalog.Assets.Where(a => a.Group == group).ToArray();
            Assert.Equal(8, assets.Length);
            Assert.All(assets, a => Assert.Equal(precision, a.Asset.Precision));
            Assert.All(assets, a => Assert.StartsWith("https://huggingface.co/ResembleAI/chatterbox-turbo-ONNX/resolve/main/onnx/", a.Url));
            Assert.All(assets, a => Assert.True(ModelCatalog.IsTurboAsset(a.Asset.FileName)));
        }
    }

    private static long Total(string group) =>
        ModelCatalog.Assets.Where(a => a.Group == group).Sum(a => a.Asset.SizeBytes ?? 0);

    private static long Size(string fileName) =>
        ModelCatalog.Assets.First(a => a.Asset.FileName == fileName).Asset.SizeBytes
        ?? throw new InvalidOperationException($"no size pinned for {fileName}");
}
