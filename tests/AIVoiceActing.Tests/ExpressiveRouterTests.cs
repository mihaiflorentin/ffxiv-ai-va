namespace AIVoiceActing.Tests;

using AIVoiceActing.Domain;
using AIVoiceActing.Ports;
using AIVoiceActing.Tests.Mock;
using Xunit;

/// <summary>Which lines go to the expressive engine, and that every failure lands on the main one.</summary>
public sealed class ExpressiveRouterTests
{
    private static SynthesisRequest Line(float exaggeration = 0.5f, params string[] tags) =>
        new("af_heart", "Hello there.", exaggeration, tags, Pitch: 1.1f);

    private static (ExpressiveRouter Router, FakeSpeechSynthesizer Main, FakeSpeechSynthesizer Expressive, List<SynthesisRequest> Prepared) Create(
        string? clip = "/clips/af_heart_p110.wav",
        bool expressivePresent = true)
    {
        var main = new FakeSpeechSynthesizer();
        var expressive = new FakeSpeechSynthesizer();
        var prepared = new List<SynthesisRequest>();
        var router = new ExpressiveRouter(
            () => main,
            () => expressivePresent ? expressive : null,
            (request, _) =>
            {
                prepared.Add(request);
                return Task.FromResult(clip);
            });
        return (router, main, expressive, prepared);
    }

    [Theory]
    [InlineData(0.5f, false)]
    [InlineData(0.7f, false)]
    [InlineData(0.75f, true)]
    [InlineData(0.9f, true)]
    [InlineData(0.2f, true)]
    public void Intensity_FarFromNeutral_GoesExpressive(float exaggeration, bool expected) =>
        Assert.Equal(expected, ExpressiveRouter.WantsExpressive(Line(exaggeration)));

    [Fact]
    public void AnyTag_GoesExpressive() =>
        Assert.True(ExpressiveRouter.WantsExpressive(Line(0.5f, "laugh")));

    [Fact]
    public async Task CalmLine_StaysOnMain_AndMakesNoClip()
    {
        var h = Create();
        await h.Router.SynthesizeAsync(Line(), CancellationToken.None);
        Assert.Single(h.Main.Calls);
        Assert.Empty(h.Expressive.Calls);
        Assert.Empty(h.Prepared);
    }

    [Fact]
    public async Task EmotionalLine_ClonesTheSpeakersClip()
    {
        var h = Create();
        await h.Router.SynthesizeAsync(Line(0.9f, "gasp"), CancellationToken.None);
        Assert.Empty(h.Main.Calls);
        var sent = Assert.Single(h.Expressive.Calls).Request;
        Assert.Equal("/clips/af_heart_p110.wav", sent.ReferenceVoiceId);
        Assert.Equal(["gasp"], sent.Tags);
    }

    [Fact]
    public async Task NoClip_NoEngine_NotReady_OrFailure_AllFallBackToMain()
    {
        var noClip = Create(clip: null);
        await noClip.Router.SynthesizeAsync(Line(0.9f), CancellationToken.None);
        Assert.Single(noClip.Main.Calls);

        var noEngine = Create(expressivePresent: false);
        await noEngine.Router.SynthesizeAsync(Line(0.9f), CancellationToken.None);
        Assert.Single(noEngine.Main.Calls);

        var notReady = Create();
        notReady.Expressive.IsReady = false;
        await notReady.Router.SynthesizeAsync(Line(0.9f), CancellationToken.None);
        Assert.Single(notReady.Main.Calls);
        Assert.Empty(notReady.Expressive.Calls);

        var failing = Create();
        failing.Expressive.Throw = new InvalidOperationException("native");
        await failing.Router.SynthesizeAsync(Line(0.9f), CancellationToken.None);
        Assert.Single(failing.Main.Calls);
        Assert.Equal("af_heart", failing.Main.Calls[0].Request.ReferenceVoiceId);
    }

    [Fact]
    public async Task Cancellation_IsNotSwallowedIntoFallback()
    {
        var h = Create();
        h.Expressive.Throw = new OperationCanceledException();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => h.Router.SynthesizeAsync(Line(0.9f), CancellationToken.None));
        Assert.Empty(h.Main.Calls);
    }

    [Fact]
    public void Readiness_FollowsTheMainEngine()
    {
        var h = Create();
        h.Main.IsReady = false;
        h.Main.NotReadyReason = "model missing";
        Assert.False(h.Router.IsReady);
        Assert.Equal("model missing", h.Router.NotReadyReason);
    }

    // ---- failure cooldown, retry, variant, status --------------------------------------------------

    [Fact]
    public async Task AFailedExpressiveLine_SitsTheEngineOut_UntilTheCooldownPasses()
    {
        var now = 0L;
        var main = new FakeSpeechSynthesizer();
        var expressive = new FakeSpeechSynthesizer { Throw = new InvalidOperationException("gpu lost") };
        var router = new ExpressiveRouter(() => main, () => expressive, (_, _) => Task.FromResult<string?>("/clip.wav"), clockMs: () => now);

        await router.SynthesizeAsync(Line(0.9f), CancellationToken.None); // fails, lands on main
        await router.SynthesizeAsync(Line(0.9f), CancellationToken.None); // skipped without a second attempt

        Assert.Single(expressive.Calls);
        Assert.Equal(2, main.Calls.Count);

        now = (long)ExpressiveRouter.FailureCooldown.TotalMilliseconds + 1;
        expressive.Throw = null;
        await router.SynthesizeAsync(Line(0.9f), CancellationToken.None);

        Assert.Equal(2, expressive.Calls.Count);
        Assert.Equal(2, main.Calls.Count);
    }

    [Fact]
    public async Task Retry_PassesTheModeToBothEngines_AndLetsTheExpressiveEngineTakeLinesAgain()
    {
        var main = new FakeSpeechSynthesizer();
        var expressive = new FakeSpeechSynthesizer { Throw = new InvalidOperationException("gpu lost") };
        var router = new ExpressiveRouter(() => main, () => expressive, (_, _) => Task.FromResult<string?>("/clip.wav"), clockMs: () => 0);
        await router.SynthesizeAsync(Line(0.9f), CancellationToken.None);
        expressive.Throw = null;

        await router.RetryAsync(DeviceMode.Gpu);
        await router.SynthesizeAsync(Line(0.9f), CancellationToken.None);

        Assert.Equal([DeviceMode.Gpu], main.Retries);
        Assert.Equal([DeviceMode.Gpu], expressive.Retries);
        Assert.Equal(2, expressive.Calls.Count);
    }

    [Fact]
    public async Task Retry_WithoutAnExpressiveEngine_OnlyRetriesTheMainOne()
    {
        var h = Create(expressivePresent: false);

        await h.Router.RetryAsync(DeviceMode.Cpu);

        Assert.Equal([DeviceMode.Cpu], h.Main.Retries);
        Assert.Empty(h.Expressive.Retries);
    }

    [Fact]
    public void RenderVariant_CoversBothEngines_SoAnExpressiveDeviceSwitchChangesTheCacheKey()
    {
        var h = Create();
        h.Main.RenderVariant = string.Empty;
        h.Expressive.RenderVariant = "q4f16/gpu";
        var gpu = h.Router.RenderVariant;
        h.Expressive.RenderVariant = "q4f16/cpu";
        var cpu = h.Router.RenderVariant;

        Assert.NotEqual(gpu, cpu);
        Assert.NotEqual(SynthesisCache.EngineKey("kokoro", gpu), SynthesisCache.EngineKey("kokoro", cpu));
        Assert.Equal("kokoro", SynthesisCache.EngineKey("kokoro", Create(expressivePresent: false).Router.RenderVariant));
    }

    [Fact]
    public void ExpressiveStatus_IsTheExpressiveEnginesOwn_AndNullWhileRoutingIsOff()
    {
        var h = Create();
        Assert.Equal(EngineState.Ready, h.Router.ExpressiveStatus!.State);
        h.Expressive.IsReady = false;
        h.Expressive.NotReadyReason = "Missing it";
        Assert.Equal("Missing it", h.Router.ExpressiveStatus!.Reason);
        Assert.Null(Create(expressivePresent: false).Router.ExpressiveStatus);
    }

    // ---- streaming: the request decides which engine would stream ----------------------------------------

    [Fact]
    public void OnlyAnEmotionalLine_StreamsWhenOnlyTheExpressiveEngineCan()
    {
        var h = Create();
        h.Expressive.SupportsStreaming = true;

        Assert.True(h.Router.SupportsStreaming);
        Assert.True(h.Router.SupportsStreamingFor(Line(0.9f, "laugh")));
        Assert.False(h.Router.SupportsStreamingFor(Line())); // a calm line goes to Kokoro, which renders whole
    }

    [Fact]
    public void NeitherEngineStreaming_MeansNothingStreams()
    {
        var h = Create();

        Assert.False(h.Router.SupportsStreaming);
        Assert.False(h.Router.SupportsStreamingFor(Line(0.9f, "laugh")));
    }

    [Fact]
    public async Task AnEmotionalLine_StreamsThroughTheExpressiveEngine_WithTheSpeakersClip()
    {
        var h = Create();
        h.Expressive.SupportsStreaming = true;
        var heard = new List<float>();

        var whole = await h.Router.SynthesizeStreamingAsync(Line(0.9f, "gasp"), s => heard.AddRange(s.ToArray()), CancellationToken.None);

        var sent = Assert.Single(h.Expressive.StreamingCalls);
        Assert.Equal("/clips/af_heart_p110.wav", sent.ReferenceVoiceId);
        Assert.Equal([0.1f, 0.2f, 0.3f, 0.4f, 0.5f], heard);
        Assert.Equal(heard, whole.Samples);
        Assert.Empty(h.Main.Calls);
    }

    [Fact]
    public async Task ACalmLine_ThroughTheStreamingDoor_IsRenderedWholeByTheMainEngine()
    {
        var h = Create();
        var pieces = 0;

        var whole = await h.Router.SynthesizeStreamingAsync(Line(), _ => pieces++, CancellationToken.None);

        Assert.Equal(1, pieces); // handed over in one go
        Assert.Single(h.Main.Calls);
        Assert.Equal(3, whole.Samples.Length);
        Assert.Empty(h.Expressive.StreamingCalls);
    }

    [Fact]
    public async Task AStreamFailureBeforeAnyAudio_FallsBackToMain_AndBenchesTheExpressiveEngine()
    {
        var now = 1_000L;
        var main = new FakeSpeechSynthesizer();
        var expressive = new FakeSpeechSynthesizer { SupportsStreaming = true, Throw = new InvalidOperationException("no") };
        var router = new ExpressiveRouter(() => main, () => expressive, (_, _) => Task.FromResult<string?>("/clip.wav"), clockMs: () => now);
        var heard = 0;

        await router.SynthesizeStreamingAsync(Line(0.9f), _ => heard++, CancellationToken.None);

        Assert.Single(main.Calls);
        Assert.Equal(1, heard);
        Assert.False(router.SupportsStreamingFor(Line(0.9f))); // benched: the line would go to the main engine
    }

    [Fact]
    public async Task AStreamFailureAfterAudioStarted_IsNotHiddenByTheMainEngine()
    {
        var main = new FakeSpeechSynthesizer();
        var expressive = new FakeSpeechSynthesizer { SupportsStreaming = true };
        expressive.AfterPiece = i =>
        {
            if (i == 0)
            {
                throw new InvalidOperationException("died mid-line");
            }
        };
        var router = new ExpressiveRouter(() => main, () => expressive, (_, _) => Task.FromResult<string?>("/clip.wav"));
        var heard = 0;

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => router.SynthesizeStreamingAsync(Line(0.9f), _ => heard++, CancellationToken.None));

        Assert.Equal(1, heard); // the first piece was out; the rest of the line is not re-voiced by another engine
        Assert.Empty(main.Calls);
    }
}
