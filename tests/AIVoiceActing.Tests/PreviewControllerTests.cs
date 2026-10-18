namespace AIVoiceActing.Tests;

using AIVoiceActing.Domain;
using AIVoiceActing.Ports;
using AIVoiceActing.Tests.Mock;
using AIVoiceActing.UI.State;
using Xunit;

public sealed class PreviewControllerTests
{
    private static readonly SpeakerIdentity Speaker = new("npc:Alisaie", "Alisaie", null, null, null, null);
    private static readonly SynthesisRequest Request = new("af_heart", "Hello there.", 0.5f, []);

    private readonly FakeSpeechQueue queue = new();
    private readonly StatusCenter status = new();
    private readonly List<string> errors = [];
    private DateTimeOffset now = new(2026, 10, 17, 12, 0, 0, TimeSpan.Zero);

    /// <summary>Synthesizer whose renders wait for the test, so phases can be observed.</summary>
    private sealed class GatedSynth : ISpeechSynthesizer
    {
        public List<TaskCompletionSource<SynthesisResult>> Pending { get; } = [];

        public List<CancellationToken> Tokens { get; } = [];

        public bool IsReady => true;

        public string NotReadyReason => "";

        public Task WarmUpAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<SynthesisResult> SynthesizeAsync(SynthesisRequest request, CancellationToken cancellationToken)
        {
            var tcs = new TaskCompletionSource<SynthesisResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            cancellationToken.Register(() => tcs.TrySetCanceled(cancellationToken));
            this.Pending.Add(tcs);
            this.Tokens.Add(cancellationToken);
            return tcs.Task;
        }
    }

    private static readonly SynthesisResult Audio = new([0f, 0.5f], 24000);

    private PreviewController Create(ISpeechSynthesizer synth) => new(
        () => synth,
        this.queue,
        this.status,
        () => this.now,
        logError: (m, _) => this.errors.Add(m));

    [Fact]
    public void StartsIdle()
    {
        var preview = this.Create(new GatedSynth());
        Assert.Equal(PreviewState.Idle, preview.State);
        Assert.False(preview.Busy);
        Assert.Null(preview.Key);
    }

    [Fact]
    public async Task Walks_Idle_Synthesizing_Playing_Idle()
    {
        var synth = new GatedSynth();
        var preview = this.Create(synth);

        var task = preview.StartDirect("af_heart", Speaker, Request);
        Assert.Equal(PreviewState.Synthesizing, preview.State);
        Assert.True(preview.Busy);
        Assert.Equal("af_heart", preview.Key);

        synth.Pending[0].SetResult(Audio);
        await task;

        Assert.Equal(PreviewState.Playing, preview.State);
        var item = Assert.Single(this.queue.Enqueued);
        Assert.Equal(Request, item.Request);
        Assert.Same(Audio, item.Audio);
        Assert.Contains(this.status.Active(), m => m.Title.StartsWith("Spoke test line", StringComparison.Ordinal));

        // The queue shows the item playing, then drains.
        this.queue.NowPlaying = item;
        preview.Tick();
        Assert.Equal(PreviewState.Playing, preview.State);
        this.queue.NowPlaying = null;
        this.queue.Enqueued.Clear();
        preview.Tick();
        Assert.Equal(PreviewState.Idle, preview.State);
        Assert.Null(preview.Key);
    }

    [Fact]
    public async Task Playing_WithoutEverSeeingTheItem_EndsAfterTheGracePeriod()
    {
        var synth = new GatedSynth();
        var preview = this.Create(synth);
        var task = preview.StartDirect("k", Speaker, Request);
        synth.Pending[0].SetResult(Audio);
        await task;
        this.queue.Enqueued.Clear(); // played instantly between frames

        preview.Tick();
        Assert.Equal(PreviewState.Playing, preview.State);

        this.now += PreviewController.PlayGrace;
        preview.Tick();
        Assert.Equal(PreviewState.Idle, preview.State);
    }

    [Fact]
    public async Task StartingAnother_WhileSynthesizing_CancelsThePrevious_SingleFlight()
    {
        var synth = new GatedSynth();
        var preview = this.Create(synth);

        var first = preview.StartDirect("one", Speaker, Request);
        var second = preview.StartDirect("two", Speaker, Request with { Text = "Second." });

        Assert.True(synth.Tokens[0].IsCancellationRequested);
        Assert.False(synth.Tokens[1].IsCancellationRequested);
        await first; // cancelled quietly
        Assert.Equal(PreviewState.Synthesizing, preview.State);
        Assert.Equal("two", preview.Key);

        synth.Pending[1].SetResult(Audio);
        await second;

        Assert.Equal(PreviewState.Playing, preview.State);
        var item = Assert.Single(this.queue.Enqueued);
        Assert.Equal("Second.", item.Request.Text);
        Assert.False(this.status.HasError);
        Assert.Empty(this.errors);
    }

    [Fact]
    public async Task StartingAnother_WhilePlaying_StopsTheCurrentAudio()
    {
        var synth = new GatedSynth();
        var preview = this.Create(synth);
        var first = preview.StartDirect("one", Speaker, Request);
        synth.Pending[0].SetResult(Audio);
        await first;
        Assert.Equal(0, this.queue.CancelCurrentCalls);

        _ = preview.StartDirect("two", Speaker, Request);

        Assert.Equal(1, this.queue.CancelCurrentCalls);
        Assert.Equal(PreviewState.Synthesizing, preview.State);
    }

    [Fact]
    public async Task Failure_MovesToError_AndSurfacesAStickyStatus()
    {
        var synth = new FakeSpeechSynthesizer { Throw = new InvalidOperationException("model missing") };
        var preview = this.Create(synth);

        await preview.StartDirect("k", Speaker, Request);

        Assert.Equal(PreviewState.Error, preview.State);
        Assert.Equal("model missing", preview.Error);
        Assert.False(preview.Busy);
        Assert.Empty(this.queue.Enqueued);
        Assert.Single(this.errors);
        var message = Assert.Single(this.status.Active(), m => m.Severity == Severity.Error);
        Assert.Equal("model missing", message.Detail);

        // The next press starts clean.
        synth.Throw = null;
        await preview.StartDirect("k", Speaker, Request);
        Assert.Equal(PreviewState.Playing, preview.State);
        Assert.Null(preview.Error);
    }

    [Fact]
    public async Task Cancel_ReturnsToIdle_AndStopsAudioOnlyWhenPlaying()
    {
        var synth = new GatedSynth();
        var preview = this.Create(synth);
        var task = preview.StartDirect("k", Speaker, Request);

        preview.Cancel();
        Assert.Equal(PreviewState.Idle, preview.State);
        Assert.Equal(0, this.queue.CancelCurrentCalls);
        Assert.True(synth.Tokens[0].IsCancellationRequested);
        await task;
        Assert.Equal(PreviewState.Idle, preview.State);

        var second = preview.StartDirect("k", Speaker, Request);
        synth.Pending[1].SetResult(Audio);
        await second;
        preview.Cancel();
        Assert.Equal(1, this.queue.CancelCurrentCalls);
        Assert.Equal(PreviewState.Idle, preview.State);
    }

    [Fact]
    public async Task OnSettled_RunsForEveryRender_IncludingSupersededAndFailed()
    {
        var synth = new GatedSynth();
        var preview = this.Create(synth);
        var settled = 0;

        var first = preview.Start("one", ct => synth.SynthesizeAsync(Request, ct), () => settled++);
        var second = preview.Start("two", _ => throw new InvalidOperationException("boom"), () => settled++);
        await first;
        await second;

        Assert.Equal(2, settled);
        Assert.Equal(PreviewState.Error, preview.State);
    }
}
