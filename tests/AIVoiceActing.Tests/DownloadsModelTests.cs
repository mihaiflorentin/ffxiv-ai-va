namespace AIVoiceActing.Tests;

using AIVoiceActing.Infrastructure.Engines;
using AIVoiceActing.Infrastructure.Onnx;
using AIVoiceActing.Ports;
using AIVoiceActing.UI.State;
using Xunit;

public sealed class DownloadsModelTests
{
    private const long Gb = 1024L * 1024 * 1024;

    private static readonly IEngineCatalog Catalog = new EngineCatalog();

    private static IReadOnlyList<DownloadRow> Rows(
        Func<ModelAsset, bool>? downloaded = null,
        Func<string, bool>? inUse = null) =>
        DownloadsModel.Rows(
            Catalog.List(),
            [.. ModelCatalog.Assets.Select(a => (a.Asset, a.Group))],
            downloaded ?? (_ => false),
            assets => assets.Sum(a => a.SizeBytes ?? 0),
            inUse ?? (_ => false));

    private static DownloadRow Row(IReadOnlyList<DownloadRow> rows, string group) => rows.Single(r => r.Group == group);

    [Fact]
    public void Rows_ListEveryOfferedGroupAndNoRetiredOnes()
    {
        var groups = Rows().Select(r => r.Group).ToList();

        Assert.Equal(["kokoro", "turbo", "turbo-fp16", "turbo-q4f16", "emotion"], groups);
    }

    [Fact]
    public void Rows_GiveEachGroupAPlainNameAndASize()
    {
        foreach (var row in Rows())
        {
            Assert.False(string.IsNullOrWhiteSpace(row.Name));
            Assert.False(Labels.ContainsJargon(row.Name + " " + row.Blurb), row.Group);
            Assert.True(row.TotalBytes > 0, row.Group);
            Assert.Equal(row.TotalBytes, row.MissingBytes);
            Assert.False(row.Installed);
        }
    }

    [Fact]
    public void RequiredAndOptionalGroups_AreToldApart()
    {
        var rows = Rows();

        Assert.False(Row(rows, "kokoro").Optional);
        Assert.False(Row(rows, "turbo").Optional);
        Assert.True(Row(rows, "turbo-fp16").Optional);
        Assert.True(Row(rows, "turbo-q4f16").Optional);
        Assert.True(Row(rows, "emotion").Optional);
    }

    [Fact]
    public void Installed_And_Partial_FollowTheFilesOnDisk()
    {
        var kokoroFiles = ModelCatalog.Assets.Where(a => a.Group == "kokoro").Select(a => a.Asset.FileName).ToHashSet();
        var someTurbo = ModelCatalog.Assets.Where(a => a.Group == "turbo").Select(a => a.Asset.FileName).Take(2).ToHashSet();

        var rows = Rows(a => kokoroFiles.Contains(a.FileName) || someTurbo.Contains(a.FileName));

        Assert.True(Row(rows, "kokoro").Installed);
        Assert.Equal(0, Row(rows, "kokoro").MissingBytes);
        Assert.True(Row(rows, "turbo").Partial);
        Assert.False(Row(rows, "turbo").Installed);
        Assert.False(Row(rows, "turbo-fp16").Partial);
    }

    [Fact]
    public void Removable_NeedsFilesAndEitherOptionalOrNotInUse()
    {
        var all = Rows(_ => true, key => key == ConfigMigrator.KokoroId);

        Assert.False(Row(all, "kokoro").Removable);          // required and speaking
        Assert.True(Row(all, "turbo").Removable);            // required but not the engine in use
        Assert.True(Row(all, "emotion").Removable);          // optional, even though Kokoro owns it
        Assert.False(Row(Rows(), "turbo").Removable);        // nothing on disk
    }

    [Fact]
    public void Remove_ExplainsWhyNot()
    {
        var all = Rows(_ => true, key => key == ConfigMigrator.KokoroId);

        Assert.Null(DownloadsModel.CannotRemove(Row(all, "turbo"), downloadActive: false));
        Assert.Contains("download", DownloadsModel.CannotRemove(Row(all, "turbo"), downloadActive: true), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("quality", DownloadsModel.CannotRemove(Row(all, "kokoro"), downloadActive: false));
        Assert.Equal("Nothing to remove.", DownloadsModel.CannotRemove(Row(Rows(), "turbo"), downloadActive: false));
    }

    [Fact]
    public void Download_IsBlockedWhileAnotherRunsOrWhenTheDiskIsTooFull()
    {
        var turbo = Row(Rows(), "turbo");

        Assert.Null(DownloadsModel.CannotDownload(turbo, freeBytes: 100 * Gb, downloadActive: false));
        Assert.Contains("Another download", DownloadsModel.CannotDownload(turbo, 100 * Gb, downloadActive: true));
        var disk = DownloadsModel.CannotDownload(turbo, freeBytes: Gb, downloadActive: false);
        Assert.Contains("free", disk);
        Assert.Contains(Labels.FileSize(Gb), disk);
    }

    [Fact]
    public void Download_OfAnInstalledGroup_IsNotOffered()
    {
        var kokoro = Row(Rows(_ => true), "kokoro");

        Assert.Equal("Already downloaded.", DownloadsModel.CannotDownload(kokoro, 100 * Gb, false));
    }

    [Fact]
    public void DownloadLabel_SaysResumeWhenSomethingIsAlreadyThere()
    {
        var fresh = Row(Rows(), "kokoro");
        var partial = fresh with { MissingBytes = fresh.TotalBytes / 2 };

        Assert.StartsWith("Download ", DownloadsModel.DownloadLabel(fresh));
        Assert.StartsWith("Resume ", DownloadsModel.DownloadLabel(partial));
        Assert.EndsWith(Labels.FileSize(fresh.TotalBytes), DownloadsModel.DownloadLabel(fresh));
    }

    [Fact]
    public void Status_IsAWordWithASeverity()
    {
        var kokoro = Row(Rows(_ => true), "kokoro");
        var turbo = Row(Rows(), "turbo");

        Assert.Equal("Installed", DownloadsModel.StatusWord(kokoro, downloadingNow: false));
        Assert.Equal(Severity.Success, DownloadsModel.StatusSeverity(kokoro, false));
        Assert.Equal("Not installed", DownloadsModel.StatusWord(turbo, false));
        Assert.Equal(Severity.Warning, DownloadsModel.StatusSeverity(turbo, false));
        Assert.Equal("Downloading", DownloadsModel.StatusWord(turbo, downloadingNow: true));
        Assert.Equal(Severity.Info, DownloadsModel.StatusSeverity(turbo, true));
    }

    [Fact]
    public void FreeDisk_HandlesAnUnknownVolume()
    {
        Assert.Equal("Free space is unknown", DownloadsModel.FreeDiskText(long.MaxValue));
        Assert.Equal("2.0 GB free on this drive", DownloadsModel.FreeDiskText(2 * Gb));
    }

    [Fact]
    public void Missing_ListsOnlyFilesNotOnDisk()
    {
        var first = ModelCatalog.Assets.First(a => a.Group == "turbo").Asset.FileName;
        var turbo = Row(Rows(a => a.FileName == first), "turbo");

        var missing = turbo.Missing(a => a.FileName == first);

        Assert.DoesNotContain(missing, a => a.FileName == first);
        Assert.Equal(turbo.FileCount - 1, missing.Count);
    }
}
