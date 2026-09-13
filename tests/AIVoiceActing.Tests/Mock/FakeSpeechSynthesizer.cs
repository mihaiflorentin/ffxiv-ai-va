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
}
