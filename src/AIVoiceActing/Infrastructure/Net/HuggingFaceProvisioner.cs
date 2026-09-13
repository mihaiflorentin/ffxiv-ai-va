namespace AIVoiceActing.Infrastructure.Net;

using System.Security.Cryptography;
using AIVoiceActing.Infrastructure.Onnx;
using AIVoiceActing.Infrastructure.Storage;
using AIVoiceActing.Ports;

/// <summary>
/// Downloads pinned model assets from the Chatterbox ONNX HF repo. Streams to
/// "&lt;dest&gt;.part", verifies pinned size (±5%) and sha256 when known, then atomically
/// moves into place; the .part file never survives a failure or cancellation.
/// Downloads happen only on explicit request (Models tab button / SmokeSynth).
/// </summary>
public sealed class HuggingFaceProvisioner : IModelProvisioner
{
    private const long ProgressGranularityBytes = 1024 * 1024;

    private readonly string modelsDir;
    private readonly IModelStore store;
    private readonly HttpClient http;
    private readonly ILogSink? log;

    /// <param name="modelsDir">Directory receiving the assets (created on demand).</param>
    /// <param name="baseUrl">Resolve base; defaults to the pinned Chatterbox repo.</param>
    /// <param name="handler">Optional handler override for tests.</param>
    /// <param name="store">Presence-check store; defaults to one over <paramref name="modelsDir"/>
    /// with the full catalog (single is-downloaded implementation).</param>
    public HuggingFaceProvisioner(
        string modelsDir,
        string? baseUrl = null,
        HttpMessageHandler? handler = null,
        ILogSink? log = null,
        IModelStore? store = null)
    {
        this.modelsDir = Path.GetFullPath(modelsDir);
        Directory.CreateDirectory(this.modelsDir);
        this.store = store
            ?? new FileModelStore(this.modelsDir, [.. ModelCatalog.Assets.Select(a => a.Asset)]);
        this.http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: true);
        this.http.Timeout = TimeSpan.FromMinutes(30);
        this.http.DefaultRequestHeaders.UserAgent.ParseAdd("AIVoiceActing/0.1 (Dalamud plugin; ffxiv-ai-va)");
        this.BaseUrl = baseUrl ?? ModelCatalog.RepoBaseUrl;
        this.log = log;
    }

    private string BaseUrl { get; }

    public bool IsDownloaded(ModelAsset asset) => this.store.IsDownloaded(asset.FileName);

    public async Task DownloadAsync(
        ModelAsset asset,
        IProgress<DownloadProgress> progress,
        CancellationToken cancellationToken)
    {
        var destination = Path.Combine(this.modelsDir, asset.FileName);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var partPath = destination + ".part";

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, this.BaseUrl + asset.FileName);
            using var response = await this.http
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            await using var network = await response.Content
                .ReadAsStreamAsync(cancellationToken)
                .ConfigureAwait(false);
            await using var part = new FileStream(partPath, FileMode.Create, FileAccess.Write, FileShare.None);
            using var sha = ModelCatalog.Sha256For(asset.FileName) is { } pinned
                ? IncrementalHash.CreateHash(HashAlgorithmName.SHA256)
                : null;

            var total = asset.SizeBytes ?? response.Content.Headers.ContentLength;
            progress.Report(new DownloadProgress(0, total));
            var lastReported = 0L;
            var received = 0L;
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

            if (asset.SizeBytes is { } expected
                && !FileModelStore.SizeWithinTolerance(received, expected))
            {
                throw new InvalidOperationException(
                    $"Downloaded \"{asset.FileName}\" is {received} bytes; expected {expected} (±5%).");
            }

            if (sha is { } hash)
            {
                var actualSha = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
                if (actualSha != ModelCatalog.Sha256For(asset.FileName))
                {
                    throw new InvalidOperationException(
                        $"Downloaded \"{asset.FileName}\" failed sha256 verification.");
                }
            }

            part.Dispose();
            File.Move(partPath, destination, overwrite: true);
            this.log?.Info($"Downloaded {asset.FileName} ({received} bytes).");
            progress.Report(new DownloadProgress(received, total));
        }
        catch
        {
            TryCleanup(partPath);
            throw;
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
