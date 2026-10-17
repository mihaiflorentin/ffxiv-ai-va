namespace AIVoiceActing.Infrastructure.Net;

using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using AIVoiceActing.Infrastructure.Onnx;
using AIVoiceActing.Infrastructure.Storage;
using AIVoiceActing.Ports;

/// <summary>
/// Downloads pinned catalog assets (per-asset resolve URLs from <see cref="ModelCatalog"/>:
/// Chatterbox lives on Hugging Face, Kokoro on a GitHub release). Streams to
/// "&lt;dest&gt;.part", verifies pinned size (±5%) and sha256 when known, then atomically
/// moves into place. An interrupted download (cancel, network drop) keeps its .part file and
/// the next attempt resumes it with an HTTP Range request; a server that ignores Range gets a
/// clean restart, and a size or hash mismatch deletes the .part so a corrupt file is never reused.
/// Free disk space is checked before a download (or a whole group) starts.
/// Downloads happen only on explicit request (Models tab button / SmokeSynth).
/// </summary>
public sealed class HuggingFaceProvisioner : IModelProvisioner
{
    private const long ProgressGranularityBytes = 1024 * 1024;

    private readonly string modelsDir;
    private readonly IModelStore store;
    private readonly HttpClient http;
    private readonly ILogSink? log;
    private readonly IReadOnlyList<ModelCatalog.CatalogAsset> catalog;

    /// <param name="modelsDir">Directory receiving the assets (created on demand).</param>
    /// <param name="handler">Optional handler override for tests.</param>
    /// <param name="store">Presence-check and free-space store; defaults to one over
    /// <paramref name="modelsDir"/> with the full catalog (single is-downloaded implementation).</param>
    /// <param name="catalog">Catalog supplying URLs and sha256 pins; defaults to <see cref="ModelCatalog.Assets"/>.</param>
    public HuggingFaceProvisioner(
        string modelsDir,
        HttpMessageHandler? handler = null,
        ILogSink? log = null,
        IModelStore? store = null,
        IReadOnlyList<ModelCatalog.CatalogAsset>? catalog = null)
    {
        this.modelsDir = Path.GetFullPath(modelsDir);
        Directory.CreateDirectory(this.modelsDir);
        this.catalog = catalog ?? ModelCatalog.Assets;
        this.store = store
            ?? new FileModelStore(this.modelsDir, [.. this.catalog.Select(a => a.Asset)]);
        this.http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: true);
        this.http.Timeout = TimeSpan.FromMinutes(30);
        this.http.DefaultRequestHeaders.UserAgent.ParseAdd("AIVoiceActing/0.1 (Dalamud plugin; ffxiv-ai-va)");
        this.log = log;
    }

    public bool IsDownloaded(ModelAsset asset) => this.store.IsDownloaded(asset.FileName);

    public long BytesNeeded(IReadOnlyList<ModelAsset> assets) =>
        assets.Where(a => !this.IsDownloaded(a)).Sum(this.RemainingBytes);

    public async Task DownloadGroupAsync(
        IReadOnlyList<ModelAsset> assets,
        IProgress<DownloadProgress> progress,
        CancellationToken cancellationToken)
    {
        this.EnsureFreeSpace(this.BytesNeeded(assets));
        foreach (var asset in assets.Where(a => !this.IsDownloaded(a)))
        {
            await this.DownloadAsync(asset, progress, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task DownloadAsync(
        ModelAsset asset,
        IProgress<DownloadProgress> progress,
        CancellationToken cancellationToken)
    {
        var destination = Path.Combine(this.modelsDir, asset.FileName);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var partPath = destination + ".part";
        var pinned = this.Sha256For(asset.FileName);

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var existing = ExistingPartLength(partPath, asset);
            this.EnsureFreeSpace(asset.SizeBytes is { } size ? Math.Max(0, size - existing) : 0);

            using var sha = pinned is not null ? IncrementalHash.CreateHash(HashAlgorithmName.SHA256) : null;
            var received = existing;
            long? total = asset.SizeBytes;

            // A leftover .part that is already the full pinned size (crash between flush and
            // move) needs no network: verify it below.
            var complete = asset.SizeBytes is { } want && existing == want;
            if (existing > 0)
            {
                await HashExistingAsync(partPath, sha, cancellationToken).ConfigureAwait(false);
            }

            if (!complete)
            {
                received = await this.FetchAsync(asset, partPath, existing, sha, progress, cancellationToken)
                    .ConfigureAwait(false);
                total = asset.SizeBytes ?? received;
            }
            else
            {
                progress.Report(new DownloadProgress(received, total));
            }

            if (asset.SizeBytes is { } expected
                && !FileModelStore.SizeWithinTolerance(received, expected))
            {
                throw new InvalidOperationException(
                    $"Downloaded \"{asset.FileName}\" is {received} bytes; expected {expected} (±5%).");
            }

            if (sha is { } hash)
            {
                var actualSha = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
                if (actualSha != pinned)
                {
                    throw new InvalidOperationException(
                        $"Downloaded \"{asset.FileName}\" failed sha256 verification.");
                }
            }

            File.Move(partPath, destination, overwrite: true);
            this.log?.Info($"Downloaded {asset.FileName} ({received} bytes).");
            progress.Report(new DownloadProgress(received, total));
        }
        catch (Exception ex) when (ex is InvalidOperationException)
        {
            // Corrupt content: a resume would only extend the corruption.
            TryCleanup(partPath);
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or InsufficientDiskSpaceException)
        {
            // Rejected (HTTP error / no space) before any bytes were committed that we distrust;
            // an empty or stale .part is useless.
            TryCleanupIfEmpty(partPath);
            throw;
        }
        // Cancellation and IO/network drops fall through with the .part kept for resume.
    }

    /// <summary>
    /// Streams the remainder of <paramref name="asset"/> into the .part file. Sends a Range
    /// request when a partial file exists; on 200 (Range ignored) the part is truncated and the
    /// hash restarted; on 416 the stale part is discarded and the fetch retried from zero.
    /// Returns the total length of the part file.
    /// </summary>
    private async Task<long> FetchAsync(
        ModelAsset asset,
        string partPath,
        long existing,
        IncrementalHash? sha,
        IProgress<DownloadProgress> progress,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, this.UrlFor(asset));
        if (existing > 0)
        {
            request.Headers.Range = new RangeHeaderValue(existing, null);
        }

        using var response = await this.http
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable && existing > 0)
        {
            this.log?.Info($"Range rejected for {asset.FileName}; restarting download.");
            File.Delete(partPath);
            sha?.GetHashAndReset();
            return await this.FetchAsync(asset, partPath, 0, sha, progress, cancellationToken).ConfigureAwait(false);
        }

        response.EnsureSuccessStatusCode();

        var resumed = existing > 0 && response.StatusCode == HttpStatusCode.PartialContent
            && response.Content.Headers.ContentRange?.From == existing;
        if (existing > 0 && !resumed)
        {
            // Server ignored Range (200) or answered a different range: start over cleanly.
            this.log?.Info($"Server ignored Range for {asset.FileName}; restarting download.");
            existing = 0;
            sha?.GetHashAndReset();
        }

        await using var network = await response.Content
            .ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var part = new FileStream(
            partPath,
            resumed ? FileMode.Append : FileMode.Create,
            FileAccess.Write,
            FileShare.None);

        var total = asset.SizeBytes
            ?? (response.Content.Headers.ContentLength is { } len ? len + existing : null);
        progress.Report(new DownloadProgress(existing, total));
        var lastReported = existing;
        var received = existing;
        var buffer = new byte[1024 * 1024];
        int read;
        while ((read = await network.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)
            .ConfigureAwait(false)) > 0)
        {
            await part.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            sha?.AppendData(buffer, 0, read);
            received += read;
            if (received - lastReported >= ProgressGranularityBytes)
            {
                progress.Report(new DownloadProgress(received, total));
                lastReported = received;
            }
        }

        await part.FlushAsync(cancellationToken).ConfigureAwait(false);
        return received;
    }

    private string UrlFor(ModelAsset asset) =>
        this.catalog.FirstOrDefault(a => a.Asset.FileName == asset.FileName)?.Url ?? ModelCatalog.UrlFor(asset);

    private string? Sha256For(string fileName) =>
        this.catalog.FirstOrDefault(a => a.Asset.FileName == fileName)?.Sha256;

    private long RemainingBytes(ModelAsset asset)
    {
        var size = asset.SizeBytes ?? 0;
        var partPath = Path.Combine(this.modelsDir, asset.FileName + ".part");
        var existing = File.Exists(partPath) ? new FileInfo(partPath).Length : 0;
        return Math.Max(0, size - existing);
    }

    private void EnsureFreeSpace(long requiredBytes)
    {
        if (requiredBytes <= 0)
        {
            return;
        }

        var free = this.store.FreeBytes(this.modelsDir);
        if (free < requiredBytes)
        {
            throw new InsufficientDiskSpaceException(requiredBytes, free);
        }
    }

    /// <summary>Length of a usable partial file; an oversize part (cannot be a prefix) is discarded.</summary>
    private static long ExistingPartLength(string partPath, ModelAsset asset)
    {
        if (!File.Exists(partPath))
        {
            return 0;
        }

        var length = new FileInfo(partPath).Length;
        if (asset.SizeBytes is { } expected
            && length > expected
            && !FileModelStore.SizeWithinTolerance(length, expected))
        {
            TryCleanup(partPath);
            return 0;
        }

        return length;
    }

    private static async Task HashExistingAsync(string partPath, IncrementalHash? sha, CancellationToken cancellationToken)
    {
        if (sha is null)
        {
            return;
        }

        await using var existing = new FileStream(partPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var buffer = new byte[1024 * 1024];
        int read;
        while ((read = await existing.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)
            .ConfigureAwait(false)) > 0)
        {
            sha.AppendData(buffer, 0, read);
        }
    }

    private static void TryCleanupIfEmpty(string partPath)
    {
        try
        {
            if (File.Exists(partPath) && new FileInfo(partPath).Length == 0)
            {
                File.Delete(partPath);
            }
        }
        catch (IOException)
        {
            // Best-effort; see TryCleanup.
        }
    }

    private static void TryCleanup(string partPath)
    {
        try
        {
            if (File.Exists(partPath))
            {
                File.Delete(partPath);
            }
        }
        catch (IOException)
        {
            // Best-effort: the .part file must never be mistaken for a complete asset,
            // and a locked file on Windows resolves on the next download attempt.
        }
    }
}
