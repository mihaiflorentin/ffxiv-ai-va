namespace AIVoiceActing.Infrastructure.Net;

using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using AIVoiceActing.Ports;

/// <summary>
/// Remote engine: speaks through an OpenAI-compatible TTS server the user runs themselves
/// (<c>POST {url}/v1/audio/speech</c>, JSON in, wav out). Loopback only unless the user opts
/// in with <c>allowNonLoopback</c>, so line text never leaves the machine by accident.
/// Settings come from <c>Engine.PerEngine["remote"].Extra</c>, read fresh on every call:
/// <c>url</c> (default http://127.0.0.1:8880), <c>allowNonLoopback</c> ("true"),
/// <c>timeoutSeconds</c> (default 30), <c>model</c> (default "kokoro").
/// Extension: when <c>referenceClipResolver</c> returns a wav path for the request's voice id,
/// the clip is sent base64 as <c>reference_wav</c>; the voice id is always sent as <c>voice</c>.
/// </summary>
public sealed class RemoteSynthesizer : ISpeechSynthesizer, IDisposable
{
    public const string DefaultUrl = "http://127.0.0.1:8880";
    public const int DefaultTimeoutSeconds = 30;
    public const string DefaultModel = "kokoro";
    private const string DefaultVoiceName = "af_heart";

    private readonly Func<IReadOnlyDictionary<string, string>> extraFactory;
    private readonly Func<string, string?>? referenceClipResolver;
    private readonly ILogSink? log;
    private readonly HttpClient http;
    private volatile bool disposed;
    private volatile string lastStatus = string.Empty;

    public RemoteSynthesizer(
        Func<IReadOnlyDictionary<string, string>> extraFactory,
        ILogSink? log = null,
        HttpMessageHandler? handler = null,
        Func<string, string?>? referenceClipResolver = null)
    {
        this.extraFactory = extraFactory ?? throw new ArgumentNullException(nameof(extraFactory));
        this.log = log;
        this.referenceClipResolver = referenceClipResolver;

        // No proxy and no redirects: a proxy could route a loopback call elsewhere, and a
        // redirect could walk a loopback-approved request out to another host.
        this.http = new HttpClient(
            handler ?? new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false },
            disposeHandler: true)
        {
            Timeout = Timeout.InfiniteTimeSpan, // per-call timeout below
        };
    }

    public bool IsReady => this.FindNotReadyReason() is null;

    public string NotReadyReason => this.FindNotReadyReason() ?? string.Empty;

    /// <summary>Plain-language outcome of the last line ("Last line took 240 ms." or the error); empty before the first.</summary>
    public string LastStatus => this.lastStatus;

    /// <summary>True when the host is localhost or a loopback address (127.0.0.0/8, ::1).</summary>
    public static bool IsLoopbackHost(Uri uri)
    {
        var host = uri.IdnHost;
        if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return IPAddress.TryParse(host.Trim('[', ']'), out var address) && IPAddress.IsLoopback(address);
    }

    private string? FindNotReadyReason() => this.TryResolveSettings(out _, out var reason) ? null : reason;

    private bool TryResolveSettings(out RemoteSettings settings, out string reason)
    {
        settings = default!;
        var extra = this.extraFactory();
        var rawUrl = Get(extra, "url");
        var url = string.IsNullOrWhiteSpace(rawUrl) ? DefaultUrl : rawUrl.Trim();

        if (!Uri.TryCreate(url, UriKind.Absolute, out var baseUri)
            || (baseUri.Scheme != Uri.UriSchemeHttp && baseUri.Scheme != Uri.UriSchemeHttps))
        {
            reason = $"Remote server address \"{url}\" is not a valid http:// or https:// address.";
            return false;
        }

        var allowNonLoopback = string.Equals(Get(extra, "allowNonLoopback"), "true", StringComparison.OrdinalIgnoreCase);
        if (!IsLoopbackHost(baseUri) && !allowNonLoopback)
        {
            reason = $"Remote server \"{baseUri.Host}\" is not on this computer. Line text is only sent to " +
                     "localhost unless you turn on \"allow non-local server\".";
            return false;
        }

        var timeout = DefaultTimeoutSeconds;
        if (Get(extra, "timeoutSeconds") is { Length: > 0 } rawTimeout
            && int.TryParse(rawTimeout, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            && parsed > 0)
        {
            timeout = parsed;
        }

        var model = Get(extra, "model");
        var endpoint = new Uri(baseUri.AbsoluteUri.TrimEnd('/') + "/v1/audio/speech");
        settings = new RemoteSettings(endpoint, TimeSpan.FromSeconds(timeout), string.IsNullOrWhiteSpace(model) ? DefaultModel : model.Trim());
        reason = string.Empty;
        return true;
    }

    private static string? Get(IReadOnlyDictionary<string, string> extra, string key) =>
        extra.TryGetValue(key, out var value) ? value : null;

    /// <summary>Probes the server with a short line so a wrong address shows up before the first real one.</summary>
    public async Task WarmUpAsync(CancellationToken cancellationToken)
    {
        if (!this.IsReady)
        {
            return;
        }

        try
        {
            await this.SynthesizeAsync(
                new SynthesisRequest(DefaultVoiceName, "Ready.", Exaggeration: 0, Tags: []),
                cancellationToken).ConfigureAwait(false);
        }
        catch (SpeechSynthesisException)
        {
            // Already logged and visible through LastStatus; warm-up is best effort.
        }
    }

    public async Task<SynthesisResult> SynthesizeAsync(SynthesisRequest request, CancellationToken cancellationToken)
    {
        this.ThrowIfDisposed();
        if (!this.TryResolveSettings(out var settings, out var reason))
        {
            throw new SpeechSynthesisException(reason);
        }

        if (string.IsNullOrWhiteSpace(request.Text))
        {
            throw new SpeechSynthesisException("Nothing to speak.");
        }

        var voice = string.IsNullOrWhiteSpace(request.ReferenceVoiceId) ? DefaultVoiceName : request.ReferenceVoiceId;
        var speed = Math.Clamp(request.Speed * (1.05f - (0.20f * Math.Clamp(request.Exaggeration, 0f, 1f))), 0.25f, 4f);
        var body = this.BuildBody(settings.Model, request.Text, voice, speed);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(settings.Timeout);
        var stopwatch = Stopwatch.StartNew();
        try
        {
            using var message = new HttpRequestMessage(HttpMethod.Post, settings.Endpoint)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
            message.Headers.Accept.ParseAdd("audio/wav");
            using var response = await this.http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, timeout.Token)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                var detail = await ReadErrorDetailAsync(response, timeout.Token).ConfigureAwait(false);
                throw this.Fail(
                    $"The remote server answered {(int)response.StatusCode} {response.ReasonPhrase}{detail}.");
            }

            var bytes = await response.Content.ReadAsByteArrayAsync(timeout.Token).ConfigureAwait(false);
            SynthesisResult result;
            try
            {
                result = DecodeWav(bytes);
            }
            catch (InvalidDataException ex)
            {
                throw this.Fail($"The remote server sent audio we cannot play: {ex.Message}", ex);
            }

            if (Math.Abs(request.Pitch - 1f) > 0.01f)
            {
                result = result with { Samples = ResamplePitch(result.Samples, request.Pitch) };
            }

            this.lastStatus = $"Last line took {stopwatch.ElapsedMilliseconds} ms.";
            this.log?.Info($"Remote synthesis complete: {result.Samples.Length} samples @ {result.SampleRate} Hz in {stopwatch.ElapsedMilliseconds} ms.");
            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw; // caller cancelled; not an engine fault
        }
        catch (OperationCanceledException ex)
        {
            throw this.Fail(
                $"The remote server did not answer within {settings.Timeout.TotalSeconds:0} seconds.", ex);
        }
        catch (HttpRequestException ex)
        {
            throw this.Fail(
                $"Cannot reach the remote server at {settings.Endpoint.GetLeftPart(UriPartial.Authority)}. Is it running?", ex);
        }
    }

    public void Dispose()
    {
        if (this.disposed)
        {
            return;
        }

        this.disposed = true;
        this.http.Dispose();
    }

    private void ThrowIfDisposed()
    {
        if (this.disposed)
        {
            throw new SpeechSynthesisEngineDisposedException();
        }
    }

    private SpeechSynthesisException Fail(string message, Exception? inner = null)
    {
        this.lastStatus = message;
        this.log?.Warn($"Remote synthesis failed: {message}{(inner is null ? string.Empty : " (" + inner.Message + ")")}");
        return new SpeechSynthesisException(message, inner);
    }

    private string BuildBody(string model, string text, string voice, float speed)
    {
        using var stream = new MemoryStream();
        using (var json = new Utf8JsonWriter(stream))
        {
            json.WriteStartObject();
            json.WriteString("model", model);
            json.WriteString("input", text);
            json.WriteString("voice", voice);
            json.WriteString("response_format", "wav");
            json.WriteNumber("speed", Math.Round(speed, 3));

            // Extension hook: a voice that resolves to a reference clip is sent along so
            // cloning servers can use it. Stock OpenAI-style servers ignore unknown fields.
            if (this.referenceClipResolver?.Invoke(voice) is { } clipPath && File.Exists(clipPath))
            {
                json.WriteString("reference_wav", Convert.ToBase64String(File.ReadAllBytes(clipPath)));
            }

            json.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static async Task<string> ReadErrorDetailAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            text = text.Trim();
            if (text.Length == 0)
            {
                return string.Empty;
            }

            return ": " + (text.Length > 200 ? text[..200] + "…" : text);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return string.Empty;
        }
    }

    /// <summary>
    /// Decodes an in-memory RIFF/WAVE (16/24/32-bit PCM or 32-bit float, any channel count
    /// averaged to mono, any sample rate) to mono floats. Tolerates streaming headers whose
    /// data size is 0 or 0xFFFFFFFF by reading to the end of the buffer.
    /// </summary>
    internal static SynthesisResult DecodeWav(byte[] wav)
    {
        if (wav.Length < 12 || !wav.AsSpan(0, 4).SequenceEqual("RIFF"u8) || !wav.AsSpan(8, 4).SequenceEqual("WAVE"u8))
        {
            throw new InvalidDataException("the response is not a WAV file.");
        }

        int format = 0, channels = 0, sampleRate = 0, bits = 0;
        var dataStart = -1;
        var dataLength = 0;
        var pos = 12;
        while (pos + 8 <= wav.Length)
        {
            var id = wav.AsSpan(pos, 4);
            var size = BitConverter.ToUInt32(wav, pos + 4);
            var body = pos + 8;
            if (id.SequenceEqual("fmt "u8) && body + 16 <= wav.Length)
            {
                format = BitConverter.ToUInt16(wav, body);
                channels = BitConverter.ToUInt16(wav, body + 2);
                sampleRate = BitConverter.ToInt32(wav, body + 4);
                bits = BitConverter.ToUInt16(wav, body + 14);
                if (format == 0xFFFE && size >= 26 && body + 26 <= wav.Length)
                {
                    format = BitConverter.ToUInt16(wav, body + 24); // extensible: sub-format tag
                }
            }
            else if (id.SequenceEqual("data"u8))
            {
                dataStart = body;
                var remaining = wav.Length - body;
                dataLength = size == 0 || size > remaining ? remaining : (int)size;
                break;
            }

            var next = (long)body + size + (size & 1);
            if (next > wav.Length)
            {
                break;
            }

            pos = (int)next;
        }

        var supported = (format, bits) is (1, 16) or (1, 24) or (1, 32) or (3, 32);
        if (!supported)
        {
            throw new InvalidDataException(
                $"unsupported WAV encoding (format {format}, {bits}-bit); expected 16/24/32-bit PCM or 32-bit float.");
        }

        if (channels < 1 || sampleRate < 8000)
        {
            throw new InvalidDataException($"unusable WAV header ({channels} channel(s), {sampleRate} Hz).");
        }

        if (dataStart < 0 || dataLength == 0)
        {
            throw new InvalidDataException("the WAV file holds no audio.");
        }

        var bytesPerSample = bits / 8;
        var frames = dataLength / (bytesPerSample * channels);
        if (frames == 0)
        {
            throw new InvalidDataException("the WAV file holds no audio.");
        }

        var samples = new float[frames];
        for (var frame = 0; frame < frames; frame++)
        {
            var sum = 0f;
            for (var ch = 0; ch < channels; ch++)
            {
                var offset = dataStart + (((frame * channels) + ch) * bytesPerSample);
                sum += (format, bits) switch
                {
                    (3, _) => BitConverter.ToSingle(wav, offset),
                    (1, 16) => BitConverter.ToInt16(wav, offset) / 32768f,
                    (1, 24) => ((wav[offset] << 8) | (wav[offset + 1] << 16) | (wav[offset + 2] << 24)) / 2147483648f,
                    _ => BitConverter.ToInt32(wav, offset) / 2147483648f,
                };
            }

            samples[frame] = sum / channels;
        }

        return new SynthesisResult(samples, sampleRate);
    }

    /// <summary>Naive pitch shift by resampling (same colouring as the Kokoro adapter).</summary>
    private static float[] ResamplePitch(float[] samples, float pitch)
    {
        var outputLength = Math.Max(1, (int)(samples.Length / pitch));
        var output = new float[outputLength];
        for (var i = 0; i < outputLength; i++)
        {
            var source = i * pitch;
            var i0 = Math.Min((int)source, samples.Length - 1);
            var i1 = Math.Min(i0 + 1, samples.Length - 1);
            var frac = source - i0;
            output[i] = (samples[i0] * (1f - frac)) + (samples[i1] * frac);
        }

        return output;
    }

    private sealed record RemoteSettings(Uri Endpoint, TimeSpan Timeout, string Model);
}
