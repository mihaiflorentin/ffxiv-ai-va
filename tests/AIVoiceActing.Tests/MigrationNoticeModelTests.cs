namespace AIVoiceActing.Tests;

using AIVoiceActing;
using AIVoiceActing.UI.State;
using Xunit;

/// <summary>The migration notice: when it shows, how changes group by area, and what "Got it" clears.</summary>
public sealed class MigrationNoticeModelTests
{
    private static Configuration WithChanges(params MigrationChange[] changes) => new()
    {
        Migration = new MigrationState { FromVersion = 1, ToVersion = Configuration.CurrentVersion, Changes = [.. changes] },
    };

    private static readonly MigrationChange EngineChange = new("Engine", "f5", "kokoro", "F5 was retired; switched to Kokoro.");
    private static readonly MigrationChange DeviceChange = new("Device", "coreml", "auto", "Now automatic.");
    private static readonly MigrationChange SecondEngineChange = new("Engine", "full", "removed", "Option removed.");

    [Fact]
    public void No_notice_when_nothing_changed()
    {
        var config = new Configuration();

        Assert.False(MigrationNoticeModel.ShouldShow(config));
        Assert.Empty(MigrationNoticeModel.Groups(config.Migration));
    }

    [Fact]
    public void Notice_shows_when_changes_exist_and_the_version_is_not_acknowledged()
    {
        var config = WithChanges(EngineChange);

        Assert.True(MigrationNoticeModel.ShouldShow(config));
    }

    [Fact]
    public void Notice_stays_hidden_once_this_version_is_acknowledged_even_if_changes_remain()
    {
        var config = WithChanges(EngineChange);
        config.MigrationNoticeAckedVersion = Configuration.CurrentVersion;

        Assert.False(MigrationNoticeModel.ShouldShow(config));
    }

    [Fact]
    public void Changes_group_by_area_in_first_seen_order()
    {
        var report = WithChanges(EngineChange, DeviceChange, SecondEngineChange).Migration;

        var groups = MigrationNoticeModel.Groups(report);

        Assert.Equal(["Engine", "Device"], groups.Select(g => g.Area));
        Assert.Equal([EngineChange, SecondEngineChange], groups[0].Changes);
        Assert.Equal([DeviceChange], groups[1].Changes);
    }

    [Fact]
    public void Group_titles_are_player_wording()
    {
        var groups = MigrationNoticeModel.Groups(WithChanges(EngineChange, DeviceChange, new MigrationChange("Voices", "a", "b", "m")).Migration);

        Assert.Equal(["Voice engine", "Processor", "Voices"], groups.Select(g => g.Title));
    }

    [Fact]
    public void Summary_counts_changes_and_areas()
    {
        Assert.StartsWith("1 change in 1 area.", MigrationNoticeModel.Summary(WithChanges(EngineChange).Migration));
        Assert.StartsWith("3 changes in 2 areas.", MigrationNoticeModel.Summary(WithChanges(EngineChange, DeviceChange, SecondEngineChange).Migration));
    }

    [Fact]
    public void Acknowledge_clears_the_report_and_records_the_version()
    {
        var config = WithChanges(EngineChange, DeviceChange);

        MigrationNoticeModel.Acknowledge(config);

        Assert.Empty(config.Migration.Changes);
        Assert.Equal(Configuration.CurrentVersion, config.MigrationNoticeAckedVersion);
        Assert.False(MigrationNoticeModel.ShouldShow(config));
    }

    [Fact]
    public void Acknowledge_keeps_the_versions_of_the_report()
    {
        var config = WithChanges(EngineChange);

        MigrationNoticeModel.Acknowledge(config);

        Assert.Equal(1, config.Migration.FromVersion);
        Assert.Equal(Configuration.CurrentVersion, config.Migration.ToVersion);
    }
}
