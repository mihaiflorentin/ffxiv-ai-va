namespace AIVoiceActing.Tests;

using AIVoiceActing.UI.State;
using Xunit;

public sealed class LabelsTests
{
    [Theory]
    [InlineData("Execution provider", "Processor")]
    [InlineData("execution provider", "Processor")]
    [InlineData("Exaggeration", "Expressiveness")]
    [InlineData("Bias", "Expressiveness")]
    [InlineData("Pool (unknown gender)", "Any gender")]
    [InlineData("Fork", "Duplicate")]
    [InlineData("Model-id bindings", "Creature models")]
    public void Plain_MapsJargon(string jargon, string plain)
    {
        Assert.Equal(plain, Labels.Plain(jargon));
    }

    [Fact]
    public void Plain_LeavesUnknownTextAlone()
    {
        Assert.Equal("Volume", Labels.Plain("Volume"));
    }

    [Theory]
    [InlineData("Execution provider", true)]
    [InlineData("Uses ONNX on DirectML", true)]
    [InlineData("Regex filter", true)]
    [InlineData("Voice speed", false)]
    [InlineData("Graphics card", false)]
    public void ContainsJargon_FlagsBannedTerms(string text, bool expected)
    {
        Assert.Equal(expected, Labels.ContainsJargon(text));
    }

    [Theory]
    [InlineData(null, "Automatic")]
    [InlineData("", "Automatic")]
    [InlineData("Auto", "Automatic")]
    [InlineData("DirectML", "Graphics card")]
    [InlineData("gpu", "Graphics card")]
    [InlineData("CPU", "CPU")]
    public void Processor_NamesPlainly(string? raw, string expected)
    {
        Assert.Equal(expected, Labels.Processor(raw));
    }

    [Theory]
    [InlineData(2, "Light (2 threads)")]
    [InlineData(4, "Balanced (4 threads)")]
    [InlineData(8, "Fast (8 threads)")]
    public void BackgroundLoad_DescribesThreads(int threads, string expected)
    {
        Assert.Equal(expected, Labels.BackgroundLoad(threads));
    }

    [Fact]
    public void FileSize_FormatsBytes()
    {
        Assert.Equal("512 B", Labels.FileSize(512));
        Assert.Equal("1.5 MB", Labels.FileSize(1_572_864));
        Assert.Equal("2.0 GB", Labels.FileSize(2L * 1024 * 1024 * 1024));
    }
}
