namespace AIVoiceActing.Tests;

using AIVoiceActing.Infrastructure.Engines;
using AIVoiceActing.Infrastructure.Onnx;
using AIVoiceActing.Ports;
using Xunit;

public sealed class EngineCatalogTests
{
    private readonly IEngineCatalog catalog = new EngineCatalog();

    [Fact]
    public void Lists_the_expected_engines()
    {
        var keys = this.catalog.List().Select(e => e.Key).ToArray();

        Assert.Contains("kokoro", keys);
        Assert.Contains("chatterbox-turbo", keys);
        Assert.Contains("remote", keys);
        Assert.Equal(keys.Length, keys.Distinct().Count());
    }

    [Theory]
    [InlineData("turbo", "chatterbox-turbo")]
    [InlineData("TURBO", "chatterbox-turbo")]
    [InlineData("chatterbox-turbo", "chatterbox-turbo")]
    [InlineData("kokoro", "kokoro")]
    [InlineData(" kokoro ", "kokoro")]
    [InlineData("f5", "f5")]
    [InlineData("chatterbox", "chatterbox")]
    [InlineData(null, "kokoro")]
    [InlineData("", "kokoro")]
    [InlineData("no-such-engine", "kokoro")]
    public void Resolve_handles_aliases_and_unknowns(string? input, string expected) =>
        Assert.Equal(expected, this.catalog.Resolve(input));

    [Fact]
    public void Find_and_Get_agree_and_Get_throws_for_unknown()
    {
        Assert.Same(this.catalog.Find("kokoro"), this.catalog.Get("kokoro"));
        Assert.Equal("chatterbox-turbo", this.catalog.Get("turbo").Key);
        Assert.Null(this.catalog.Find("nope"));
        Assert.Null(this.catalog.Find(null));
        var ex = Assert.Throws<EngineNotFoundException>(() => this.catalog.Get("nope"));
        Assert.Equal("nope", ex.EngineId);
    }

    [Fact]
    public void Every_model_asset_group_belongs_to_exactly_one_engine()
    {
        var groups = ModelCatalog.Assets.Select(a => a.Group).Distinct().ToArray();
        Assert.NotEmpty(groups);

        foreach (var group in groups)
        {
            var owners = this.catalog.List().Where(e => e.AssetGroups.Contains(group)).ToArray();
            Assert.True(owners.Length == 1, $"group \"{group}\" owned by {owners.Length} engines");
            Assert.Same(owners[0], this.catalog.OwnerOfGroup(group));
        }
    }

    [Fact]
    public void No_engine_lists_a_group_the_model_catalog_does_not_have()
    {
        var known = ModelCatalog.Assets.Select(a => a.Group).ToHashSet();

        foreach (var engine in this.catalog.List())
        {
            Assert.All(engine.AssetGroups, g => Assert.Contains(g, known));
        }
    }

    [Fact]
    public void OwnerOfGroup_is_null_for_unknown_group() =>
        Assert.Null(this.catalog.OwnerOfGroup("nonexistent"));

    [Fact]
    public void Kokoro_is_the_cpu_default_and_turbo_prefers_gpu()
    {
        var kokoro = this.catalog.Get("kokoro");
        var turbo = this.catalog.Get("chatterbox-turbo");

        Assert.Equal(ComputeDevice.Cpu, kokoro.DefaultDevice);
        Assert.Equal(EngineSpeed.Fast, kokoro.Speed);
        Assert.Equal(ComputeDevice.Gpu, turbo.DefaultDevice);
        Assert.True(turbo.SupportsGpu);
        Assert.Contains("fp16", turbo.ApproxBytesByPrecision.Keys);
        Assert.Contains("q4f16", turbo.ApproxBytesByPrecision.Keys);
    }

    [Fact]
    public void Legacy_engines_are_retired_and_f5_is_flagged_non_commercial()
    {
        Assert.True(this.catalog.Get("f5").Retired);
        Assert.True(this.catalog.Get("f5").NonCommercial);
        Assert.True(this.catalog.Get("chatterbox").Retired);
        Assert.False(this.catalog.Get("kokoro").Retired);
        Assert.False(this.catalog.Get("chatterbox-turbo").Retired);
    }

    [Fact]
    public void Container_exposes_the_catalog_and_resolves_the_turbo_alias_for_routing()
    {
        using var container = new AIVoiceActing.Container.ServiceContainer(
            logSinkOverride: new Mock.FakeLogSink(),
            selectedEngineFactory: () => "turbo");

        Assert.Equal("chatterbox-turbo", container.EngineCatalog.Resolve("turbo"));
        Assert.Same(container.EngineCatalog, container.EngineCatalog);
    }
}
