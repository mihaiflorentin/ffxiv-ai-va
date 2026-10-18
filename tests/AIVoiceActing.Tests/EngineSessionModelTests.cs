namespace AIVoiceActing.Tests;

using AIVoiceActing.Ports;
using AIVoiceActing.Tests.Mock;
using AIVoiceActing.UI.State;
using Xunit;

public sealed class EngineSessionModelTests
{
    private readonly StatusCenter status = new();
    private readonly List<string> info = [];
    private readonly List<string> errors = [];

    private EngineSessionModel Create() => new(this.status, this.info.Add, (m, _) => this.errors.Add(m));

    private sealed class GatedSynth : ISpeechSynthesizer
    {
        public TaskCompletionSource Gate { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Exception? Fail { get; set; }

        public bool IsReady => false;

        public string NotReadyReason => "";

        public int WarmUps { get; private set; }

        public async Task WarmUpAsync(CancellationToken cancellationToken)
        {
            this.WarmUps++;
            await this.Gate.Task;
            if (this.Fail is { } failure)
            {
                throw failure;
            }
        }

        public Task<SynthesisResult> SynthesizeAsync(SynthesisRequest request, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    [Fact]
    public async Task WarmUp_FlagsLoadingUntilItSettles()
    {
        var model = this.Create();
        var synth = new GatedSynth();

        Assert.False(model.Warming);
        Assert.True(model.StartWarmUp("kokoro", synth));
        Assert.True(model.Warming);
        Assert.Equal("kokoro", model.WarmingKey);

        synth.Gate.SetResult();
        await model.Completion!;

        Assert.False(model.Warming);
        Assert.Null(model.WarmingKey);
        Assert.Equal(1, synth.WarmUps);
        Assert.False(this.status.HasError);
        Assert.Contains(this.info, m => m.StartsWith("Engine warm-up finished: kokoro", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SecondWarmUp_WhileRunning_IsIgnored()
    {
        var model = this.Create();
        var synth = new GatedSynth();
        model.StartWarmUp("kokoro", synth);

        Assert.False(model.StartWarmUp("turbo", new GatedSynth()));
        Assert.Equal("kokoro", model.WarmingKey);

        synth.Gate.SetResult();
        await model.Completion!;
        Assert.True(model.StartWarmUp("turbo", new FakeSpeechSynthesizer()));
        await model.Completion!;
    }

    [Fact]
    public async Task Failure_PostsAStickyError_AndAnotherSuccessClearsIt()
    {
        var model = this.Create();
        var synth = new GatedSynth { Fail = new InvalidOperationException("no GPU") };
        model.StartWarmUp("kokoro", synth);
        synth.Gate.SetResult();
        await model.Completion!;

        var error = Assert.Single(this.status.Active());
        Assert.Equal(Severity.Error, error.Severity);
        Assert.Equal("no GPU", error.Detail);
        Assert.Single(this.errors);
        Assert.False(model.Warming);

        model.StartWarmUp("kokoro", new FakeSpeechSynthesizer());
        await model.Completion!;
        Assert.False(this.status.HasError);
    }

    [Fact]
    public async Task Superseded_IsAnInfoMessage_NotAnError()
    {
        var model = this.Create();
        var synth = new GatedSynth { Fail = new SpeechSynthesisEngineDisposedException() };
        model.StartWarmUp("kokoro", synth);
        synth.Gate.SetResult();
        await model.Completion!;

        Assert.False(this.status.HasError);
        Assert.Empty(this.errors);
        Assert.Contains(this.status.Active(), m => m.Severity == Severity.Info);
        Assert.False(model.Warming);
    }

    [Fact]
    public void Reason_PrefersTheEnginesOwnText()
    {
        var ready = new FakeSpeechSynthesizer { IsReady = true };
        var silent = new FakeSpeechSynthesizer { IsReady = false, NotReadyReason = "  " };
        var worded = new FakeSpeechSynthesizer { IsReady = false, NotReadyReason = "Missing kokoro.onnx" };

        Assert.Equal(ModelsTabModel.EngineNotReadyHint, EngineSessionModel.Reason(ready));
        Assert.Equal(ModelsTabModel.EngineNotReadyHint, EngineSessionModel.Reason(silent));
        Assert.Equal("Missing kokoro.onnx", EngineSessionModel.Reason(worded));
    }
}
