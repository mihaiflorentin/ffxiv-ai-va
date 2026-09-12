namespace AIVoiceActing.Tests;

using AIVoiceActing.Ports;
using AIVoiceActing.UI.State;
using Xunit;

public sealed class ModelsTabModelTests
{
    private static readonly ModelAsset Big =
        new("LM weights", "onnx/language_model_q4.onnx_data", SizeBytes: 353621248);
    private static readonly ModelAsset Tiny = new("Tokenizer", "tokenizer.json", SizeBytes: null);

    [Fact]
    public void Row_ConvertsSizeBytesToMb()
    {
        var row = ModelsTabModel.Row(Big, downloaded: false);
        Assert.Equal("LM weights", row.Name);
        Assert.Equal(337.2, row.SizeMb, precision: 1);
        Assert.False(row.Downloaded);
        Assert.False(row.Optional);
    }

    [Fact]
    public void Row_HandlesUnknownSize()
    {
        Assert.Equal(0d, ModelsTabModel.Row(Tiny, true).SizeMb, precision: 1);
    }

    [Fact]
    public void StatusLabel_ReflectsDownloadState()
    {
        Assert.Equal("missing", ModelsTabModel.StatusLabel(ModelsTabModel.Row(Big, false)));
        Assert.Equal("downloaded", ModelsTabModel.StatusLabel(ModelsTabModel.Row(Big, true)));
        Assert.Equal(
            "optional (missing)",
            ModelsTabModel.StatusLabel(ModelsTabModel.Row(Big with { Optional = true }, false)));
    }

    [Fact]
    public void CanDownload_GatesOnInFlightAndPresence()
    {
        var row = ModelsTabModel.Row(Big, downloaded: false);
        Assert.True(ModelsTabModel.CanDownload(downloadInFlight: false, row));
        Assert.False(ModelsTabModel.CanDownload(downloadInFlight: true, row));
        Assert.False(ModelsTabModel.CanDownload(
            downloadInFlight: false, ModelsTabModel.Row(Big, downloaded: true)));
    }

    [Fact]
    public void OverallProgress_ClampsToUnitInterval()
    {
        Assert.Equal(0.5, ModelsTabModel.OverallProgress(50, 100), precision: 6);
        Assert.Equal(0d, ModelsTabModel.OverallProgress(50, null), precision: 6);
        Assert.Equal(0d, ModelsTabModel.OverallProgress(50, 0), precision: 6);
        Assert.Equal(1d, ModelsTabModel.OverallProgress(150, 100), precision: 6);
    }

    [Fact]
    public void ProgressLabel_FormatsMb()
    {
        Assert.Equal("1.0 / 2.0 MB", ModelsTabModel.ProgressLabel(1048576, 2097152));
        Assert.Equal("1.0 MB", ModelsTabModel.ProgressLabel(1048576, null));
    }

    [Fact]
    public void EngineNotReadyHint_IsANonEmptyUserHint()
    {
        Assert.False(string.IsNullOrWhiteSpace(ModelsTabModel.EngineNotReadyHint));
    }
}
