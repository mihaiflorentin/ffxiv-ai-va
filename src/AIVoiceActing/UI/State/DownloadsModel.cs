namespace AIVoiceActing.UI.State;

using AIVoiceActing.Ports;

/// <summary>One download group as the Downloads card lists it.</summary>
/// <param name="Optional">Every file in the group is optional; the engine runs without it.</param>
/// <param name="InUse">The engine that owns the group is speaking right now.</param>
/// <param name="MissingBytes">What a download would fetch (partial files already subtracted).</param>
public sealed record DownloadRow(
    string Group,
    string Name,
    string Blurb,
    long TotalBytes,
    long MissingBytes,
    int FileCount,
    int InstalledCount,
    bool Optional,
    bool InUse,
    IReadOnlyList<ModelAsset> Assets)
{
    public bool Installed => this.InstalledCount >= this.FileCount;

    public bool Partial => this.InstalledCount > 0 && !this.Installed;

    /// <summary>Files can be deleted when the group is optional or its engine is not the one speaking.</summary>
    public bool Removable => this.InstalledCount > 0 && (this.Optional || !this.InUse);

    /// <summary>The group's files that are not on disk yet, in download order.</summary>
    public IReadOnlyList<ModelAsset> Missing(Func<ModelAsset, bool> downloaded) =>
        [.. this.Assets.Where(a => !downloaded(a))];
}

/// <summary>
/// Pure rows of the Downloads card: one per download group of every offered engine, plus the
/// shared emotion reader last. Names and one-line descriptions are plain words; the technical
/// precision names stay under Advanced.
/// </summary>
public static class DownloadsModel
{
    public const string EmotionGroup = "emotion";

    public static IReadOnlyList<DownloadRow> Rows(
        IReadOnlyList<EngineInfo> engines,
        IReadOnlyList<(ModelAsset Asset, string Group)> assets,
        Func<ModelAsset, bool> downloaded,
        Func<IReadOnlyList<ModelAsset>, long> bytesNeeded,
        Func<string, bool> engineInUse)
    {
        var rows = new List<DownloadRow>();
        foreach (var engine in engines.Where(e => !e.Retired))
        {
            foreach (var group in engine.AssetGroups.Where(g => g != EmotionGroup))
            {
                rows.Add(BuildRow(group, engine, assets, downloaded, bytesNeeded, engineInUse));
            }
        }

        // The emotion reader belongs to one engine in the catalog but serves them all, so it is listed last.
        var owner = engines.FirstOrDefault(e => !e.Retired && e.AssetGroups.Contains(EmotionGroup));
        if (owner is not null)
        {
            rows.Add(BuildRow(EmotionGroup, owner, assets, downloaded, bytesNeeded, engineInUse));
        }

        return [.. rows.Where(r => r.FileCount > 0)];
    }

    public static string GroupName(string group, string engineName) => group switch
    {
        "kokoro" => "Fast voices (Kokoro)",
        "turbo" => "Expressive voices (Chatterbox Turbo)",
        "turbo-fp16" => "Expressive voices, lighter version",
        "turbo-q4f16" => "Expressive voices, lightest version",
        EmotionGroup => "Emotion reader",
        _ => $"{engineName} ({group})",
    };

    public static string GroupBlurb(string group) => group switch
    {
        "kokoro" => "The everyday narrator. Used by Fast and Balanced.",
        "turbo" => "The expressive voices. Used by Balanced and Most expressive.",
        "turbo-fp16" => "A lighter version of the expressive voices for graphics cards with less memory. Optional.",
        "turbo-q4f16" => "The lightest version, for small graphics cards. A little less lifelike. Optional.",
        EmotionGroup => "Reads what each line means so delivery fits the scene. Optional.",
        _ => string.Empty,
    };

    /// <summary>The state in one word, next to a status icon.</summary>
    public static string StatusWord(DownloadRow row, bool downloadingNow)
    {
        if (downloadingNow)
        {
            return "Downloading";
        }

        return row.Installed ? "Installed" : row.Partial ? "Partly downloaded" : "Not installed";
    }

    public static Severity StatusSeverity(DownloadRow row, bool downloadingNow)
    {
        if (downloadingNow)
        {
            return Severity.Info;
        }

        return row.Installed ? Severity.Success : Severity.Warning;
    }

    /// <summary>"Download 1.7 GB" or "Resume 400 MB" when a partial file exists.</summary>
    public static string DownloadLabel(DownloadRow row)
    {
        var size = Labels.FileSize(Math.Max(row.MissingBytes, 0));
        return row.Partial || (row.MissingBytes > 0 && row.MissingBytes < row.TotalBytes)
            ? $"Resume {size}"
            : $"Download {size}";
    }

    /// <summary>Why Download cannot start now; null when it can.</summary>
    public static string? CannotDownload(DownloadRow row, long freeBytes, bool downloadActive)
    {
        if (downloadActive)
        {
            return "Another download is running. Wait for it to finish or cancel it.";
        }

        if (row.Installed)
        {
            return "Already downloaded.";
        }

        if (freeBytes < row.MissingBytes + SetupFlowModel.DiskHeadroomBytes)
        {
            return $"Needs {Labels.FileSize(row.MissingBytes + SetupFlowModel.DiskHeadroomBytes)} free on the drive that holds your voices; " +
                   $"{Labels.FileSize(freeBytes)} is free.";
        }

        return null;
    }

    /// <summary>Why Remove cannot run now; null when it can.</summary>
    public static string? CannotRemove(DownloadRow row, bool downloadActive)
    {
        if (downloadActive)
        {
            return "A download is running. Wait for it to finish or cancel it.";
        }

        if (row.InstalledCount == 0)
        {
            return "Nothing to remove.";
        }

        return row.Removable ? null : "In use by your current voice quality. Switch quality first.";
    }

    public static string FreeDiskText(long freeBytes) =>
        freeBytes == long.MaxValue ? "Free space is unknown" : $"{Labels.FileSize(freeBytes)} free on this drive";

    private static DownloadRow BuildRow(
        string group,
        EngineInfo owner,
        IReadOnlyList<(ModelAsset Asset, string Group)> assets,
        Func<ModelAsset, bool> downloaded,
        Func<IReadOnlyList<ModelAsset>, long> bytesNeeded,
        Func<string, bool> engineInUse)
    {
        var files = assets.Where(a => a.Group == group).Select(a => a.Asset).ToList();
        var missing = files.Where(a => !downloaded(a)).ToList();
        return new DownloadRow(
            group,
            GroupName(group, owner.DisplayName),
            GroupBlurb(group),
            files.Sum(a => a.SizeBytes ?? 0),
            missing.Count == 0 ? 0 : bytesNeeded(missing),
            files.Count,
            files.Count - missing.Count,
            files.Count > 0 && files.All(a => a.Optional),
            engineInUse(owner.Key),
            files);
    }
}
