namespace AIVoiceActing.Tests.Mock;

using AIVoiceActing.Domain;
using AIVoiceActing.Ports;

/// <summary>
/// Fake ISpeechSynthesizer (census fake pattern): records every request with its
/// cancellation token; <see cref="SynthesizeFunc"/> overrides the canned result,
/// <see cref="Throw"/> simulates engine failure.
/// </summary>
public sealed class FakeSpeechSynthesizer : ISpeechSynthesizer
{
    public bool IsReady { get; set; } = true;
    public string NotReadyReason { get; set; } = "";

    public int WarmUpCalls { get; private set; }

    public string RenderVariant { get; set; } = string.Empty;

    public List<DeviceMode> Retries { get; } = [];

    public Task RetryAsync(DeviceMode mode, CancellationToken cancellationToken = default)
    {
        this.Retries.Add(mode);
        return Task.CompletedTask;
    }

    public Exception? Throw { get; set; }

    public Func<SynthesisRequest, CancellationToken, SynthesisResult>? SynthesizeFunc { get; set; }

    public List<(SynthesisRequest Request, CancellationToken CancellationToken)> Calls { get; } = [];

    public SynthesisRequest? LastRequest =>
        this.Calls.Count == 0 ? null : this.Calls[^1].Request;

    public Task<SynthesisResult> SynthesizeAsync(SynthesisRequest request, CancellationToken cancellationToken)
    {
        this.Calls.Add((request, cancellationToken));
        if (this.Throw is { } failure)
        {
            return Task.FromException<SynthesisResult>(failure);
        }

        if (this.SynthesizeFunc is { } synthesize)
        {
            return Task.FromResult(synthesize(request, cancellationToken));
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromCanceled<SynthesisResult>(cancellationToken);
        }

        return Task.FromResult(new SynthesisResult([0f, 0.5f, -0.5f], 24000));
    }

    // ---- streaming ------------------------------------------------------------------------------------

    public bool SupportsStreaming { get; set; }

    public int StreamingSampleRate { get; set; } = 24000;

    /// <summary>The pieces a streamed line is delivered in; defaults to three short pieces.</summary>
    public Func<SynthesisRequest, IEnumerable<float[]>>? StreamPieces { get; set; }

    /// <summary>Runs between pieces (after each is delivered), to let a test interleave playback or cancellation.</summary>
    public Action<int>? AfterPiece { get; set; }

    public List<SynthesisRequest> StreamingCalls { get; } = [];

    public Task<SynthesisResult> SynthesizeStreamingAsync(
        SynthesisRequest request,
        Action<ReadOnlyMemory<float>> onSamples,
        CancellationToken cancellationToken)
    {
        this.StreamingCalls.Add(request);
        if (this.Throw is { } failure)
        {
            return Task.FromException<SynthesisResult>(failure);
        }

        var all = new List<float>();
        var index = 0;
        foreach (var piece in this.StreamPieces?.Invoke(request) ?? [[0.1f, 0.2f], [0.3f, 0.4f], [0.5f]])
        {
            cancellationToken.ThrowIfCancellationRequested();
            all.AddRange(piece);
            onSamples(piece);
            this.AfterPiece?.Invoke(index++);
        }

        return Task.FromResult(new SynthesisResult([.. all], this.StreamingSampleRate));
    }

    public Task WarmUpAsync(CancellationToken cancellationToken)
    {
        this.WarmUpCalls++;
        return Task.CompletedTask;
    }
}
