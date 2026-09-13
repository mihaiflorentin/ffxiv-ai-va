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
        Assert.Equal(27, ModelCatalog.Assets.Count);
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

        var turbo = ModelCatalog.Assets.Where(a => a.Group == ModelCatalog.TurboGroup).ToArray();
        Assert.Equal(10, turbo.Length);
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

    private static long Size(string fileName) =>
        ModelCatalog.Assets.First(a => a.Asset.FileName == fileName).Asset.SizeBytes
        ?? throw new InvalidOperationException($"no size pinned for {fileName}");
}
