namespace AIVoiceActing.UI.State;

/// <summary>Migration changes that share an area, in the order the migrator wrote them.</summary>
public sealed record MigrationGroup(string Area, string Title, IReadOnlyList<MigrationChange> Changes);

/// <summary>
/// Pure state of the "Your settings were updated" notice. The notice shows while the stored
/// report has changes and the player has not acknowledged this schema version; acknowledging
/// clears the report and records the version, so it stays gone after a restart.
/// </summary>
public static class MigrationNoticeModel
{
    public const string Headline = "Your settings were updated";

    public static bool ShouldShow(Configuration config) =>
        config.MigrationNoticeAckedVersion < Configuration.CurrentVersion
        && config.Migration.Changes.Count > 0;

    /// <summary>Changes grouped by area, first-seen order, with a player-facing title per area.</summary>
    public static IReadOnlyList<MigrationGroup> Groups(MigrationState report)
    {
        var groups = new List<MigrationGroup>();
        foreach (var byArea in report.Changes.GroupBy(c => c.Area, StringComparer.OrdinalIgnoreCase))
        {
            groups.Add(new MigrationGroup(byArea.Key, AreaTitle(byArea.Key), [.. byArea]));
        }

        return groups;
    }

    /// <summary>One line under the headline, for example "2 changes in 1 area."</summary>
    public static string Summary(MigrationState report)
    {
        var changes = report.Changes.Count;
        var areas = report.Changes.Select(c => c.Area).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        return $"{changes} {(changes == 1 ? "change" : "changes")} in {areas} {(areas == 1 ? "area" : "areas")}. " +
               "Nothing needs doing.";
    }

    /// <summary>"Got it": drops the report and remembers that this version's notice was read.</summary>
    public static void Acknowledge(Configuration config)
    {
        config.Migration.Changes.Clear();
        config.MigrationNoticeAckedVersion = Configuration.CurrentVersion;
    }

    private static string AreaTitle(string area) => area.ToLowerInvariant() switch
    {
        "engine" => "Voice engine",
        "device" => "Processor",
        _ => area,
    };
}
