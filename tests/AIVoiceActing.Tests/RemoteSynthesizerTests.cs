namespace AIVoiceActing.Tests;

using System.Net;
using System.Text;
using System.Text.Json;
using AIVoiceActing.Infrastructure.Net;
using AIVoiceActing.Ports;
using AIVoiceActing.Tests.Mock;
using Xunit;

public sealed class RemoteSynthesizerTests
{
    private sealed class StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond)
        : HttpMessageHandler
    {
        public List<(Uri? Uri, string Body)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(ct);
            this.Requests.Add((request.RequestUri, body));
            return await respond(request, ct);
        }
    }

    private static byte[] Wav16(int sampleRate, short[] samples, int channels = 1)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        var dataSize = samples.Length * 2;
        w.Write("RIFF"u8);
        w.Write(36 + dataSize);
        w.Write("WAVE"u8);
        w.Write("fmt "u8);
        w.Write(16);
        w.Write((short)1);
        w.Write((short)channels);
        w.Write(sampleRate);
        w.Write(sampleRate * 2 * channels);
        w.Write((short)(2 * channels));
        w.Write((short)16);
        w.Write("data"u8);
        w.Write(dataSize);
        foreach (var s in samples)
        {
            w.Write(s);
        }

        return ms.ToArray();
    }

    private static HttpResponseMessage Ok(byte[] bytes) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };

    private static RemoteSynthesizer Make(
        StubHandler handler,
        Dictionary<string, string>? extra = null,
        FakeLogSink? log = null) =>
        new(() => extra ?? [], log, handler);

    private static SynthesisRequest Req(string text = "Hello there.") =>
        new("af_heart", text, Exaggeration: 0f, Tags: []);

    private static StubHandler Success() =>
        new((_, _) => Task.FromResult(Ok(Wav16(24000, [0, 16384, -16384, 8192]))));

    [Fact]
    public async Task Success_DecodesWavAndSendsOpenAiBody()
    {
        var handler = Success();
        using var synth = Make(handler);

        var result = await synth.SynthesizeAsync(Req(), CancellationToken.None);

        Assert.Equal(24000, result.SampleRate);
        Assert.Equal(4, result.Samples.Length);
        Assert.Equal(0.5f, result.Samples[1], 3);
        Assert.Equal(-0.5f, result.Samples[2], 3);

        var (uri, body) = Assert.Single(handler.Requests);
        Assert.Equal("http://127.0.0.1:8880/v1/audio/speech", uri!.ToString());
        using var json = JsonDocument.Parse(body);
        var root = json.RootElement;
        Assert.Equal("kokoro", root.GetProperty("model").GetString());
        Assert.Equal("Hello there.", root.GetProperty("input").GetString());
        Assert.Equal("af_heart", root.GetProperty("voice").GetString());
        Assert.Equal("wav", root.GetProperty("response_format").GetString());
        Assert.True(root.GetProperty("speed").GetDouble() > 0);
        Assert.False(root.TryGetProperty("reference_wav", out _));
        Assert.Contains("ms", synth.LastStatus);
    }

    [Fact]
    public async Task Success_StereoIsAveragedToMono_AtServerSampleRate()
    {
        var handler = new StubHandler((_, _) => Task.FromResult(Ok(Wav16(22050, [16384, -16384, 8192, 8192], channels: 2))));
        using var synth = Make(handler);

        var result = await synth.SynthesizeAsync(Req(), CancellationToken.None);

        Assert.Equal(22050, result.SampleRate);
        Assert.Equal(2, result.Samples.Length);
        Assert.Equal(0f, result.Samples[0], 3);
        Assert.Equal(0.25f, result.Samples[1], 3);
    }

    [Fact]
    public async Task ReferenceClip_IsSentBase64WhenResolved()
    {
        var clip = Path.Combine(Path.GetTempPath(), $"aiva-remote-{Guid.NewGuid():N}.wav");
        var clipBytes = Wav16(24000, [1, 2, 3]);
        await File.WriteAllBytesAsync(clip, clipBytes);
        try
        {
            var handler = Success();
            using var synth = new RemoteSynthesizer(() => new Dictionary<string, string>(), null, handler, id => id == "af_heart" ? clip : null);

            await synth.SynthesizeAsync(Req(), CancellationToken.None);

            using var json = JsonDocument.Parse(handler.Requests[0].Body);
            Assert.Equal(Convert.ToBase64String(clipBytes), json.RootElement.GetProperty("reference_wav").GetString());
        }
        finally
        {
            File.Delete(clip);
        }
    }

    [Fact]
    public async Task ServerError_ThrowsWithStatusAndDetail_AndReportsIt()
    {
        var handler = new StubHandler((_, _) => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.InternalServerError) { Content = new StringContent("model exploded") }));
        using var synth = Make(handler);

        var ex = await Assert.ThrowsAsync<SpeechSynthesisException>(() => synth.SynthesizeAsync(Req(), CancellationToken.None));

        Assert.Contains("500", ex.Message);
        Assert.Contains("model exploded", ex.Message);
        Assert.Equal(ex.Message, synth.LastStatus);
        Assert.True(synth.IsReady); // a failed line does not unconfigure the engine
    }

    [Fact]
    public async Task ConnectionRefused_ThrowsFriendlyError()
    {
        var handler = new StubHandler((_, _) => throw new HttpRequestException("refused"));
        using var synth = Make(handler);

        var ex = await Assert.ThrowsAsync<SpeechSynthesisException>(() => synth.SynthesizeAsync(Req(), CancellationToken.None));

        Assert.Contains("Cannot reach the remote server", ex.Message);
        Assert.Contains("127.0.0.1:8880", ex.Message);
    }

    [Theory]
    [InlineData("not a wav at all")]
    [InlineData("")]
    public async Task BadWav_Throws(string payload)
    {
        var handler = new StubHandler((_, _) => Task.FromResult(Ok(Encoding.ASCII.GetBytes(payload))));
        using var synth = Make(handler);

        var ex = await Assert.ThrowsAsync<SpeechSynthesisException>(() => synth.SynthesizeAsync(Req(), CancellationToken.None));

        Assert.Contains("cannot play", ex.Message);
    }

    [Fact]
    public async Task WavWithoutAudio_Throws()
    {
        var handler = new StubHandler((_, _) => Task.FromResult(Ok(Wav16(24000, []))));
        using var synth = Make(handler);

        await Assert.ThrowsAsync<SpeechSynthesisException>(() => synth.SynthesizeAsync(Req(), CancellationToken.None));
    }

    [Fact]
    public async Task Timeout_ThrowsEngineError_NotCancellation()
    {
        var handler = new StubHandler(async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return Ok([]);
        });
        using var synth = Make(handler, new() { ["timeoutSeconds"] = "1" });

        var ex = await Assert.ThrowsAsync<SpeechSynthesisException>(() => synth.SynthesizeAsync(Req(), CancellationToken.None));

        Assert.Contains("did not answer within 1 seconds", ex.Message);
    }

    [Fact]
    public async Task Cancellation_PropagatesAsOperationCanceled()
    {
        var started = new TaskCompletionSource();
        var handler = new StubHandler(async (_, ct) =>
        {
            started.SetResult();
            await Task.Delay(Timeout.Infinite, ct);
            return Ok([]);
        });
        using var synth = Make(handler);
        using var cts = new CancellationTokenSource();

        var task = synth.SynthesizeAsync(Req(), cts.Token);
        await started.Task;
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
    }

    [Theory]
    [InlineData("http://localhost:8880")]
    [InlineData("http://127.0.0.1:9000")]
    [InlineData("http://127.5.5.5")]
    [InlineData("http://[::1]:8880")]
    [InlineData("https://LOCALHOST")]
    public void Loopback_IsAllowed(string url)
    {
        using var synth = Make(Success(), new() { ["url"] = url });

        Assert.True(synth.IsReady, synth.NotReadyReason);
        Assert.Equal(string.Empty, synth.NotReadyReason);
    }

    [Theory]
    [InlineData("http://192.168.1.20:8880")]
    [InlineData("http://example.com")]
    [InlineData("http://10.0.0.5")]
    public async Task LanAddress_IsRejectedUnlessAllowed(string url)
    {
        var handler = Success();
        using var synth = Make(handler, new() { ["url"] = url });

        Assert.False(synth.IsReady);
        Assert.Contains("not on this computer", synth.NotReadyReason);
        await Assert.ThrowsAsync<SpeechSynthesisException>(() => synth.SynthesizeAsync(Req(), CancellationToken.None));
        Assert.Empty(handler.Requests); // nothing leaves the machine

        using var allowed = Make(handler, new() { ["url"] = url, ["allowNonLoopback"] = "true" });
        Assert.True(allowed.IsReady);
        await allowed.SynthesizeAsync(Req(), CancellationToken.None);
        Assert.Single(handler.Requests);
    }

    [Theory]
    [InlineData("ftp://localhost")]
    [InlineData("localhost:8880")]
    [InlineData("garbage")]
    public void InvalidUrl_IsNotReady(string url)
    {
        using var synth = Make(Success(), new() { ["url"] = url });

        Assert.False(synth.IsReady);
        Assert.Contains("not a valid", synth.NotReadyReason);
    }

    [Fact]
    public void Defaults_AreReady_WithNoConfiguration()
    {
        using var synth = Make(Success());

        Assert.True(synth.IsReady);
    }

    [Fact]
    public async Task Dispose_ThenSynthesize_ThrowsDisposedMarker()
    {
        var synth = Make(Success());
        synth.Dispose();

        await Assert.ThrowsAsync<SpeechSynthesisEngineDisposedException>(() => synth.SynthesizeAsync(Req(), CancellationToken.None));
    }

    [Fact]
    public async Task CustomUrlAndModel_AreUsed()
    {
        var handler = Success();
        using var synth = Make(handler, new() { ["url"] = "http://localhost:5000/", ["model"] = "tts-1" });

        await synth.SynthesizeAsync(Req(), CancellationToken.None);

        Assert.Equal("http://localhost:5000/v1/audio/speech", handler.Requests[0].Uri!.ToString());
        using var json = JsonDocument.Parse(handler.Requests[0].Body);
        Assert.Equal("tts-1", json.RootElement.GetProperty("model").GetString());
    }
}
