namespace AIVoiceActing.Tests;

using System.Security.Cryptography;
using AIVoiceActing.Infrastructure.Net;
using AIVoiceActing.Infrastructure.Onnx;
using AIVoiceActing.Infrastructure.Storage;
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
            // A cancelled download keeps its .part for resume, but never a final file.
            Assert.False(File.Exists(Path.Combine(dir, "tokenizer.json")));
        }
    }

    // ---- Stub-handler tests: resume, Range ignored, hash mismatch, free disk ----

    private static readonly byte[] Payload = Enumerable.Range(0, 5000).Select(i => (byte)(i * 31 % 251)).ToArray();

    private static readonly ModelAsset StubAsset = new("stub", "stub.bin", Payload.Length);

    private static IReadOnlyList<ModelCatalog.CatalogAsset> StubCatalog(byte[] content, string? shaOf = null) =>
    [
        new(new ModelAsset("stub", "stub.bin", content.Length),
            Convert.ToHexString(SHA256.HashData(shaOf is null ? content : Payload)).ToLowerInvariant(),
            "stub-group",
            "https://stub.test/stub.bin"),
    ];

    [Fact]
    public async Task Resume_ContinuesFromPartialWithRangeRequest()
    {
        var dir = NewDir();
        File.WriteAllBytes(Path.Combine(dir, "stub.bin.part"), Payload[..1200]);
        var handler = new StubHandler(Payload, honorRange: true);
        var provisioner = new HuggingFaceProvisioner(dir, handler, catalog: StubCatalog(Payload));

        var reports = new List<DownloadProgress>();
        await provisioner.DownloadAsync(StubAsset, new SyncProgress(reports), CancellationToken.None);

        var request = Assert.Single(handler.Ranges);
        Assert.Equal(1200, request?.From);
        Assert.Equal(Payload, File.ReadAllBytes(Path.Combine(dir, "stub.bin")));
        Assert.False(File.Exists(Path.Combine(dir, "stub.bin.part")));
        Assert.Equal(1200, reports[0].BytesReceived); // progress starts at the resume offset
        Assert.Equal(Payload.Length, reports[^1].BytesReceived);
    }

    [Fact]
    public async Task Resume_ServerIgnoresRange_RestartsCleanly()
    {
        var dir = NewDir();
        File.WriteAllBytes(Path.Combine(dir, "stub.bin.part"), Payload[..1200]);
        var handler = new StubHandler(Payload, honorRange: false);
        var provisioner = new HuggingFaceProvisioner(dir, handler, catalog: StubCatalog(Payload));

        await provisioner.DownloadAsync(StubAsset, Null(), CancellationToken.None);

        Assert.Single(handler.Ranges); // Range was sent, then ignored (200)
        Assert.Equal(Payload, File.ReadAllBytes(Path.Combine(dir, "stub.bin"))); // not 1200 + full
        Assert.False(File.Exists(Path.Combine(dir, "stub.bin.part")));
    }

    [Fact]
    public async Task Resume_RangeNotSatisfiable_RestartsFromZero()
    {
        var dir = NewDir();
        File.WriteAllBytes(Path.Combine(dir, "stub.bin.part"), Payload[..1200]);
        var handler = new StubHandler(Payload, honorRange: true) { RejectRangeWith416 = true };
        var provisioner = new HuggingFaceProvisioner(dir, handler, catalog: StubCatalog(Payload));

        await provisioner.DownloadAsync(StubAsset, Null(), CancellationToken.None);

        Assert.Equal(2, handler.Ranges.Count);
        Assert.Null(handler.Ranges[1]);
        Assert.Equal(Payload, File.ReadAllBytes(Path.Combine(dir, "stub.bin")));
    }

    [Fact]
    public async Task HashMismatch_DeletesPart_AndLeavesNoFinalFile()
    {
        var dir = NewDir();
        var corrupt = (byte[])Payload.Clone();
        corrupt[10] ^= 0xFF; // same size, wrong content
        var provisioner = new HuggingFaceProvisioner(
            dir, new StubHandler(corrupt, honorRange: true), catalog: StubCatalog(corrupt, shaOf: "payload"));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            provisioner.DownloadAsync(StubAsset, Null(), CancellationToken.None));

        Assert.False(File.Exists(Path.Combine(dir, "stub.bin.part")));
        Assert.False(File.Exists(Path.Combine(dir, "stub.bin")));
    }

    [Fact]
    public async Task HashMismatchOnResume_DeletesPart()
    {
        var dir = NewDir();
        var badPrefix = (byte[])Payload[..1200].Clone();
        badPrefix[0] ^= 0xFF;
        File.WriteAllBytes(Path.Combine(dir, "stub.bin.part"), badPrefix);
        var provisioner = new HuggingFaceProvisioner(
            dir, new StubHandler(Payload, honorRange: true), catalog: StubCatalog(Payload));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            provisioner.DownloadAsync(StubAsset, Null(), CancellationToken.None));

        Assert.False(File.Exists(Path.Combine(dir, "stub.bin.part")));
    }

    [Fact]
    public async Task Cancel_KeepsPartForResume()
    {
        var dir = NewDir();
        using var cts = new CancellationTokenSource();
        var handler = new StubHandler(Payload, honorRange: true) { CancelAfterBytes = 2000, Cts = cts };
        var provisioner = new HuggingFaceProvisioner(dir, handler, catalog: StubCatalog(Payload));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            provisioner.DownloadAsync(StubAsset, Null(), cts.Token));

        var part = Path.Combine(dir, "stub.bin.part");
        Assert.True(File.Exists(part));
        Assert.Equal(Payload[..(int)new FileInfo(part).Length], File.ReadAllBytes(part));
    }

    [Fact]
    public async Task DownloadAsync_InsufficientFreeSpace_ThrowsBeforeAnyRequest()
    {
        var dir = NewDir();
        var handler = new StubHandler(Payload, honorRange: true);
        var store = new StubStore(dir, freeBytes: Payload.Length - 1);
        var provisioner = new HuggingFaceProvisioner(dir, handler, store: store, catalog: StubCatalog(Payload));

        var ex = await Assert.ThrowsAsync<InsufficientDiskSpaceException>(() =>
            provisioner.DownloadAsync(StubAsset, Null(), CancellationToken.None));

        Assert.Equal(Payload.Length, ex.RequiredBytes);
        Assert.Equal(Payload.Length - 1, ex.AvailableBytes);
        Assert.Empty(handler.Ranges);
    }

    [Fact]
    public async Task DownloadGroupAsync_ChecksWholeGroupUpFront_CountingPartials()
    {
        var dir = NewDir();
        var other = new ModelAsset("other", "other.bin", 3000);
        File.WriteAllBytes(Path.Combine(dir, "stub.bin.part"), Payload[..1000]);
        var handler = new StubHandler(Payload, honorRange: true);
        var needed = (Payload.Length - 1000) + 3000;

        var tight = new HuggingFaceProvisioner(dir, handler, store: new StubStore(dir, needed - 1), catalog: StubCatalog(Payload));
        Assert.Equal(needed, tight.BytesNeeded([StubAsset, other]));
        var ex = await Assert.ThrowsAsync<InsufficientDiskSpaceException>(() =>
            tight.DownloadGroupAsync([StubAsset, other], Null(), CancellationToken.None));
        Assert.Equal(needed, ex.RequiredBytes);
        Assert.Empty(handler.Ranges); // nothing fetched, not even the asset that would have fit

        var roomy = new HuggingFaceProvisioner(dir, handler, store: new StubStore(dir, needed), catalog: StubCatalog(Payload));
        await roomy.DownloadGroupAsync([StubAsset], Null(), CancellationToken.None);
        Assert.True(File.Exists(Path.Combine(dir, "stub.bin")));
    }

    [Fact]
    public void FileModelStore_FreeBytes_ReportsPositiveForExistingAndMissingDirs()
    {
        var store = new FileModelStore(NewDir(), []);
        Assert.True(store.FreeBytes(store.ModelsDir) > 0);
        Assert.True(store.FreeBytes(Path.Combine(store.ModelsDir, "not", "yet")) > 0);
    }

    private static IProgress<DownloadProgress> Null() => new Progress<DownloadProgress>(_ => { });

    private sealed class SyncProgress(List<DownloadProgress> sink) : IProgress<DownloadProgress>
    {
        public void Report(DownloadProgress value) => sink.Add(value);
    }

    private sealed class StubStore(string dir, long freeBytes) : IModelStore
    {
        private readonly FileModelStore inner = new(dir, []);

        public string ModelsDir => this.inner.ModelsDir;

        public bool IsDownloaded(string assetName) => this.inner.IsDownloaded(assetName);

        public long FreeBytes(string dir) => freeBytes;

        public IReadOnlyList<ModelAsset> Missing() => this.inner.Missing();

        public bool Remove(string assetName) => this.inner.Remove(assetName);
    }

    /// <summary>Serves <c>content</c> for any URL; optionally honours Range, rejects it with 416, or cancels mid-body.</summary>
    private sealed class StubHandler(byte[] content, bool honorRange) : HttpMessageHandler
    {
        public List<System.Net.Http.Headers.RangeItemHeaderValue?> Ranges { get; } = [];

        public bool RejectRangeWith416 { get; init; }

        public int? CancelAfterBytes { get; init; }

        public CancellationTokenSource? Cts { get; init; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var range = request.Headers.Range?.Ranges.FirstOrDefault();
            this.Ranges.Add(range);

            if (range?.From is not null && this.RejectRangeWith416)
            {
                return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.RequestedRangeNotSatisfiable));
            }

            if (range?.From is { } start && honorRange)
            {
                var response = new HttpResponseMessage(System.Net.HttpStatusCode.PartialContent)
                {
                    Content = this.Body(content[(int)start..]),
                };
                response.Content.Headers.ContentRange =
                    new System.Net.Http.Headers.ContentRangeHeaderValue(start, content.Length - 1, content.Length);
                return Task.FromResult(response);
            }

            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = this.Body(content) });
        }

        private HttpContent Body(byte[] bytes) => this.CancelAfterBytes is { } limit
            ? new StreamContent(new CancellingStream(bytes, limit, this.Cts!))
            : new ByteArrayContent(bytes);
    }

    /// <summary>Yields <c>limit</c> bytes, then cancels the token and throws on the next read.</summary>
    private sealed class CancellingStream(byte[] bytes, int limit, CancellationTokenSource cts) : MemoryStream(bytes)
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (this.Position >= limit)
            {
                await cts.CancelAsync();
                cancellationToken.ThrowIfCancellationRequested();
            }

            return await base.ReadAsync(buffer[..Math.Min(buffer.Length, limit - (int)this.Position)], cancellationToken);
        }
    }

    private static string NewDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"aiva-prov-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }
}
