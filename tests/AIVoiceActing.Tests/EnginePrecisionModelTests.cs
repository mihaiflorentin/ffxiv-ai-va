namespace AIVoiceActing.Tests;

using AIVoiceActing.Infrastructure.Engines;
using AIVoiceActing.Ports;
using AIVoiceActing.UI.State;
using Xunit;

public sealed class EnginePrecisionModelTests
{
    private static readonly IEngineCatalog Catalog = new EngineCatalog();

    private static EngineInfo Turbo => Catalog.Get("chatterbox-turbo");

    [Fact]
    public void Turbo_OffersAutomaticAndEachPrecisionWithItsSize()
    {
        var choices = EnginePrecisionModel.Choices(Turbo);

        Assert.Equal(["auto", "fp32", "fp16", "q4f16"], choices.Select(c => c.Value));
        Assert.Contains("3.2 GB", choices.Single(c => c.Value == "fp32").Label);
        Assert.Contains("1.7 GB", choices.Single(c => c.Value == "fp16").Label);
        Assert.Contains("560.0 MB", choices.Single(c => c.Value == "q4f16").Label);
        Assert.Contains("recommended", choices[0].Label);
    }

    [Theory]
    [InlineData("kokoro")]
    [InlineData("remote")]
    public void EnginesWithOneFileSet_OfferNoPicker(string id)
    {
        Assert.Empty(EnginePrecisionModel.Choices(Catalog.Get(id)));
    }

    [Theory]
    [InlineData("fp32", "turbo")]
    [InlineData("fp16", "turbo-fp16")]
    [InlineData("q4f16", "turbo-q4f16")]
    public void EachPrecision_PointsAtTheGroupThatHoldsItsFiles(string precision, string group)
    {
        Assert.Equal(group, EnginePrecisionModel.GroupFor(Turbo, precision));
    }

    [Fact]
    public void Automatic_HasNoGroupOfItsOwn()
    {
        Assert.Null(EnginePrecisionModel.GroupFor(Turbo, "auto"));
        Assert.Null(EnginePrecisionModel.GroupFor(Turbo, "nonsense"));
    }

    [Fact]
    public void Current_DefaultsToAutomatic()
    {
        Assert.Equal("auto", EnginePrecisionModel.Current(new EngineOptionsMap(), "chatterbox-turbo"));
    }

    [Fact]
    public void Set_StoresTheChoiceAndRoundTrips()
    {
        var map = new EngineOptionsMap();

        EnginePrecisionModel.Set(map, "chatterbox-turbo", "fp16");

        Assert.Equal("fp16", EnginePrecisionModel.Current(map, "chatterbox-turbo"));
        Assert.Equal("fp16", map["chatterbox-turbo"].Precision);
    }

    [Fact]
    public void Set_BackToAutomatic_DropsAnEntryThatHoldsNothingElse()
    {
        var map = new EngineOptionsMap();
        EnginePrecisionModel.Set(map, "chatterbox-turbo", "q4f16");

        EnginePrecisionModel.Set(map, "chatterbox-turbo", "auto");

        Assert.Empty(map);
    }

    [Fact]
    public void Set_BackToAutomatic_KeepsAnEntryThatHasOtherOptions()
    {
        var map = new EngineOptionsMap { ["chatterbox-turbo"] = new EngineOptions { Device = "gpu", Precision = "fp16" } };

        EnginePrecisionModel.Set(map, "chatterbox-turbo", "auto");

        Assert.Equal("gpu", map["chatterbox-turbo"].Device);
        Assert.Equal("auto", map["chatterbox-turbo"].Precision);
    }

    [Fact]
    public void Set_AutomaticOnAnEngineWithNoEntry_CreatesNothing()
    {
        var map = new EngineOptionsMap();

        EnginePrecisionModel.Set(map, "chatterbox-turbo", "auto");

        Assert.Empty(map);
    }
}
