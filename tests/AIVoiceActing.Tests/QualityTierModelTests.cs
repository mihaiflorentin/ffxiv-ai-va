namespace AIVoiceActing.Tests;

using AIVoiceActing;
using AIVoiceActing.Infrastructure.Engines;
using AIVoiceActing.Domain;
using AIVoiceActing.Ports;
using AIVoiceActing.UI.State;
using Xunit;

/// <summary>Tier to engine-settings mapping: round trip, GPU gating, aliases, custom setups, cancel-restore.</summary>
public sealed class QualityTierModelTests
{
    private static readonly EngineCatalog Catalog = EngineCatalog.Instance;

    [Theory]
    [InlineData(QualityTier.Fast)]
    [InlineData(QualityTier.Balanced)]
    [InlineData(QualityTier.MostExpressive)]
    public void Apply_then_FromConfig_round_trips(QualityTier tier)
    {
        var engine = new EngineSettings();

        Assert.True(QualityTierModel.Apply(engine, tier, hasUsableGpu: true));

        Assert.Equal(tier, QualityTierModel.FromConfig(engine, Catalog));
    }

    [Fact]
    public void Fast_is_kokoro_with_expressive_routing_off()
    {
        var engine = new EngineSettings { Primary = "chatterbox-turbo", Expressive = { Enabled = true } };

        QualityTierModel.Apply(engine, QualityTier.Fast, hasUsableGpu: false);

        Assert.Equal("kokoro", engine.Primary);
        Assert.False(engine.Expressive.Enabled);
    }

    [Fact]
    public void Balanced_is_kokoro_with_turbo_routed_in()
    {
        var engine = new EngineSettings();

        QualityTierModel.Apply(engine, QualityTier.Balanced, hasUsableGpu: false);

        Assert.Equal("kokoro", engine.Primary);
        Assert.True(engine.Expressive.Enabled);
        Assert.Equal("chatterbox-turbo", engine.Expressive.EngineId);
    }

    [Fact]
    public void MostExpressive_makes_turbo_primary_and_turns_routing_off()
    {
        var engine = new EngineSettings { Expressive = { Enabled = true } };

        Assert.True(QualityTierModel.Apply(engine, QualityTier.MostExpressive, hasUsableGpu: true));

        Assert.Equal("chatterbox-turbo", engine.Primary);
        Assert.False(engine.Expressive.Enabled);
    }

    [Fact]
    public void MostExpressive_without_a_gpu_is_refused_and_changes_nothing()
    {
        var engine = new EngineSettings();

        Assert.False(QualityTierModel.IsAvailable(QualityTier.MostExpressive, hasUsableGpu: false));
        Assert.NotNull(QualityTierModel.UnavailableReason(QualityTier.MostExpressive, hasUsableGpu: false));
        Assert.False(QualityTierModel.Apply(engine, QualityTier.MostExpressive, hasUsableGpu: false));

        Assert.Equal("kokoro", engine.Primary);
        Assert.False(engine.Expressive.Enabled);
    }

    [Theory]
    [InlineData(QualityTier.Fast)]
    [InlineData(QualityTier.Balanced)]
    public void Cpu_tiers_are_available_without_a_gpu(QualityTier tier)
    {
        Assert.True(QualityTierModel.IsAvailable(tier, hasUsableGpu: false));
        Assert.Null(QualityTierModel.UnavailableReason(tier, hasUsableGpu: false));
    }

    [Fact]
    public void The_turbo_alias_reads_as_most_expressive()
    {
        var engine = new EngineSettings { Primary = "turbo" };

        Assert.Equal(QualityTier.MostExpressive, QualityTierModel.FromConfig(engine, Catalog));
    }

    [Theory]
    [InlineData("remote")]
    [InlineData("f5")]
    public void Other_primary_engines_read_as_custom(string primary)
    {
        var engine = new EngineSettings { Primary = primary };

        Assert.Null(QualityTierModel.FromConfig(engine, Catalog));
    }

    [Fact]
    public void Routing_to_an_engine_other_than_turbo_reads_as_custom()
    {
        var engine = new EngineSettings { Expressive = { Enabled = true, EngineId = "remote" } };

        Assert.Null(QualityTierModel.FromConfig(engine, Catalog));
    }

    [Fact]
    public void Restore_puts_back_what_Capture_took()
    {
        var engine = new EngineSettings { Primary = "kokoro", Expressive = { Enabled = true, EngineId = "chatterbox-turbo" } };
        var before = QualityTierModel.Capture(engine);

        QualityTierModel.Apply(engine, QualityTier.MostExpressive, hasUsableGpu: true);
        QualityTierModel.Restore(engine, before);

        Assert.Equal(QualityTier.Balanced, QualityTierModel.FromConfig(engine, Catalog));
    }

    [Theory]
    [InlineData(QualityTier.Fast, "kokoro")]
    [InlineData(QualityTier.Balanced, "kokoro,chatterbox-turbo")]
    [InlineData(QualityTier.MostExpressive, "chatterbox-turbo")]
    public void Each_tier_names_the_engines_it_needs_on_disk(QualityTier tier, string expected)
    {
        Assert.Equal(expected, string.Join(",", QualityTierModel.EnginesFor(tier)));
    }

    [Fact]
    public void Every_tier_has_a_label_and_a_plain_one_line_explanation()
    {
        foreach (var tier in QualityTierModel.All)
        {
            Assert.False(string.IsNullOrWhiteSpace(QualityTierModel.Label(tier)));
            var text = QualityTierModel.Explanation(tier);
            Assert.False(string.IsNullOrWhiteSpace(text));
            Assert.DoesNotContain('\n', text);
            Assert.False(Labels.ContainsJargon(text), text);
        }
    }

    private static EngineStatus OnCpu(EngineState state, string reason, DeviceMode requested = DeviceMode.Gpu) =>
        new(state, requested, ActiveDevice.Cpu, reason, false);

    [Theory]
    [InlineData(QualityTier.Balanced)]
    [InlineData(QualityTier.MostExpressive)]
    public void A_tier_that_expects_the_graphics_card_warns_with_the_ports_reason(QualityTier tier)
    {
        var warning = QualityTierModel.ProcessorWarning(tier, OnCpu(EngineState.FellBack, "Precision is set to full precision."));

        Assert.Equal("Precision is set to full precision.", warning);
    }

    [Fact]
    public void Without_a_reason_the_warning_still_says_what_is_wrong()
    {
        var warning = QualityTierModel.ProcessorWarning(QualityTier.MostExpressive, OnCpu(EngineState.Ready, string.Empty));

        Assert.Contains("expects your graphics card", warning);
        Assert.False(Labels.ContainsJargon(warning!), warning);
    }

    [Fact]
    public void No_warning_when_all_is_as_expected()
    {
        var onGpu = new EngineStatus(EngineState.Ready, DeviceMode.Gpu, new ActiveDevice(DeviceEp.WebGpu, "RTX"), string.Empty, false);

        Assert.Null(QualityTierModel.ProcessorWarning(QualityTier.Fast, OnCpu(EngineState.FellBack, "x")));
        Assert.Null(QualityTierModel.ProcessorWarning(null, OnCpu(EngineState.FellBack, "x")));
        Assert.Null(QualityTierModel.ProcessorWarning(QualityTier.MostExpressive, onGpu));
        Assert.Null(QualityTierModel.ProcessorWarning(QualityTier.MostExpressive, OnCpu(EngineState.Ready, "x", DeviceMode.Cpu)));
        Assert.Null(QualityTierModel.ProcessorWarning(QualityTier.MostExpressive, OnCpu(EngineState.Loading, "x")));
        Assert.Null(QualityTierModel.ProcessorWarning(QualityTier.MostExpressive, null));
    }

    [Fact]
    public void No_warning_while_the_processor_is_in_use_by_choice_because_it_is_faster()
    {
        var byChoice = new EngineStatus(EngineState.Ready, DeviceMode.Auto, ActiveDevice.Cpu, "Running on your processor right now.", false, OnProcessorForNow: true);

        Assert.Null(QualityTierModel.ProcessorWarning(QualityTier.MostExpressive, byChoice));
    }
}
