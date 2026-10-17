namespace AIVoiceActing.Tests;

using AIVoiceActing;
using AIVoiceActing.Infrastructure.Engines;
using AIVoiceActing.UI.State;
using Xunit;

/// <summary>The Home status line: priority order, plain wording, and where the engine runs.</summary>
public sealed class HomeStatusModelTests
{
    private static HomeStatusInput Input(
        bool enabled = true,
        bool ready = true,
        bool warming = false,
        bool needsDownload = false,
        bool gpu = false,
        bool downloading = false,
        int number = 1,
        int count = 1,
        string reason = "",
        string? error = null) =>
        new(enabled, ready, warming, needsDownload, "Kokoro", gpu, downloading, number, count, reason, error);

    [Fact]
    public void Ready_on_the_processor()
    {
        var status = HomeStatusModel.Describe(Input());

        Assert.Equal(Severity.Success, status.Severity);
        Assert.Equal("Ready — Kokoro on your processor", status.Text);
    }

    [Fact]
    public void Ready_on_the_graphics_card()
    {
        Assert.Equal("Ready — Kokoro on your graphics card", HomeStatusModel.Describe(Input(gpu: true)).Text);
    }

    [Fact]
    public void Speech_off_is_a_warning_even_when_ready()
    {
        var status = HomeStatusModel.Describe(Input(enabled: false));

        Assert.Equal(Severity.Warning, status.Severity);
        Assert.StartsWith("Speech is off", status.Text);
    }

    [Fact]
    public void Missing_models_read_as_not_set_up_yet()
    {
        var status = HomeStatusModel.Describe(Input(ready: false, needsDownload: true, reason: "Models not downloaded."));

        Assert.Equal(Severity.Warning, status.Severity);
        Assert.Equal("Not set up yet", status.Text);
    }

    [Fact]
    public void Not_ready_for_another_reason_shows_that_reason()
    {
        var status = HomeStatusModel.Describe(Input(ready: false, reason: "The voice file is damaged."));

        Assert.Equal("Not ready — The voice file is damaged", status.Text);
    }

    [Fact]
    public void Downloads_show_file_position()
    {
        var status = HomeStatusModel.Describe(Input(ready: false, downloading: true, number: 2, count: 5));

        Assert.Equal(Severity.Info, status.Severity);
        Assert.Equal("Downloading 2 of 5…", status.Text);
    }

    [Fact]
    public void Loading_shows_the_engine_name()
    {
        Assert.Equal("Loading Kokoro…", HomeStatusModel.Describe(Input(ready: false, warming: true)).Text);
    }

    [Fact]
    public void An_error_wins_over_everything_else()
    {
        var status = HomeStatusModel.Describe(Input(downloading: true, error: "Download of \"Kokoro\" failed"));

        Assert.Equal(Severity.Error, status.Severity);
        Assert.Equal("Download of \"Kokoro\" failed", status.Text);
    }

    [Theory]
    [InlineData("auto", "kokoro", false)]
    [InlineData("auto", "chatterbox-turbo", true)]
    [InlineData("gpu", "kokoro", false)]
    [InlineData("gpu", "chatterbox-turbo", true)]
    [InlineData("cpu", "chatterbox-turbo", false)]
    public void OnGraphicsCard_follows_the_processor_choice_and_what_the_engine_supports(string mode, string engine, bool expected)
    {
        Assert.Equal(expected, HomeStatusModel.OnGraphicsCard(mode, EngineCatalog.Instance.Get(engine)));
    }

    [Fact]
    public void Status_wording_has_no_engine_jargon()
    {
        var lines = new[]
        {
            HomeStatusModel.Describe(Input()).Text,
            HomeStatusModel.Describe(Input(gpu: true)).Text,
            HomeStatusModel.Describe(Input(ready: false, needsDownload: true)).Text,
            HomeStatusModel.Describe(Input(ready: false, downloading: true, number: 1, count: 3)).Text,
        };

        Assert.All(lines, l => Assert.False(Labels.ContainsJargon(l), l));
    }
}
