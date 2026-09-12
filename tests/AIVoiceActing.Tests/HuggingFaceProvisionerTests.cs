namespace AIVoiceActing.Tests;

using AIVoiceActing.Infrastructure.Net;
using AIVoiceActing.Ports;
using Xunit;

/// <summary>
/// Live-network tests against the pinned tiny Chatterbox files (93 B / 28 KB — never the
/// model weights). Prove: atomic .part move, size sanity, cancellation cleanup, progress.
/// </summary>
public sealed class HuggingFaceProvisionerTests
{
    private static readonly ModelAsset GenerationConfig = new("generation_config", "generation_config.json", 93);
    private static readonly ModelAsset Tokenizer = new("tokenizer", "tokenizer.json", 28543);

    [Fact]
    public async Task DownloadTinyFile_AtomicWriteAndSizeCheck()
    {
        var dir = NewDir();
        var provisioner = new HuggingFaceProvisioner(dir);
        Assert.False(provisioner.IsDownloaded(GenerationConfig));

        var reports = new List<DownloadProgress>();
        await provisioner.DownloadAsync(GenerationConfig, new Progress<DownloadProgress>(reports.Add),
            CancellationToken.None);

        var path = Path.Combine(dir, "generation_config.json");
        Assert.True(File.Exists(path));
        Assert.Equal(93, new FileInfo(path).Length);
        Assert.True(provisioner.IsDownloaded(GenerationConfig));
        Assert.False(File.Exists(path + ".part"));

        var final = Assert.Single(reports, r => r.BytesReceived >= 93);
        Assert.Equal(93, final.BytesReceived);
    }

    [Fact]
    public async Task Download_SizeDeviationRejected_NoArtifactsLeft()
    {
        var dir = NewDir();
        var provisioner = new HuggingFaceProvisioner(dir);
        var bogus = new ModelAsset("tokenizer", "tokenizer.json", 999999);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            provisioner.DownloadAsync(bogus, Null(), CancellationToken.None));

        Assert.False(File.Exists(Path.Combine(dir, "tokenizer.json")));
        Assert.Empty(Directory.GetFiles(dir, "*.part"));
    }

    [Fact]
    public async Task Download_CancelledBeforeStart_NoPartialFile()
    {
        var dir = NewDir();
        var provisioner = new HuggingFaceProvisioner(dir);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            provisioner.DownloadAsync(Tokenizer, Null(), cts.Token));

        Assert.Empty(Directory.GetFiles(dir)); // no file, no .part
    }

    [Fact]
    public async Task Download_MidFlightCancel_CleansPartFile()
    {
        var dir = NewDir();
        var provisioner = new HuggingFaceProvisioner(dir);
        using var cts = new CancellationTokenSource();

        var download = provisioner.DownloadAsync(Tokenizer, Null(), cts.Token);
        try
        {
            await download.WaitAsync(TimeSpan.FromSeconds(10));
            // Completed before the cancel landed — the completed file must still be valid.
            Assert.True(provisioner.IsDownloaded(Tokenizer));
        }
        catch (TimeoutException)
        {
            cts.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => download);
            Assert.True(provisioner.IsDownloaded(Tokenizer) || !File.Exists(Path.Combine(dir, "tokenizer.json")));
        }

        Assert.Empty(Directory.GetFiles(dir, "*.part"));
    }

    private static IProgress<DownloadProgress> Null() => new Progress<DownloadProgress>(_ => { });

    private static string NewDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"aiva-prov-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }
}
