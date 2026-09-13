namespace AIVoiceActing.Tests;

using AIVoiceActing.Domain;
using AIVoiceActing.Domain.Handlers;
using AIVoiceActing.Infrastructure.Text;
using AIVoiceActing.Ports;
using AIVoiceActing.Tests.Mock;
using Xunit;

public sealed class SpeechRequestHandlerTests
{
    private static readonly SpeakerIdentity Speaker =
        new("npc: sibold", "Sibold", Race: 1, Tribe: 1, Sex: 1, World: null);

    private static VoiceProfile Profile(float bias = 0f) =>
        new(Speaker.Key, "default", bias, DateTimeOffset.UnixEpoch, Custom: false);

    private static DialogueSessionFactory Sessions(params string[] lines)
    {
        var factory = new DialogueSessionFactory();
        foreach (var line in lines)
        {
            factory.Append("s", new DialogueLine(Speaker.Key, Speaker.DisplayName, line, DateTimeOffset.UnixEpoch));
        }

        return factory;
    }

    private sealed record Harness(
        FakeLexicon Lexicon,
        DialogueSessionFactory Sessions,
        FakeSpeechSynthesizer Synthesizer,
        FakeSpeechQueue Queue,
        FakeEmotionDirector Director,
        FakeLogSink Log,
        SpeechRequestHandler Handler,
        List<string> DirectorFactoryCalls)
    {
        public static Harness Create(
            DialogueSessionFactory? sessions = null,
            float bias = 0f,
            bool directorPresent = true,
            bool removeStutters = true,
            Func<string, (string, IReadOnlyList<string>)>? extractStyleTags = null,
            VoiceProfile? profile = null,
            Func<SpeakerIdentity, VoiceProfile?>? profileLookup = null)
        {
            var lexicon = new FakeLexicon();
            var factory = sessions ?? new DialogueSessionFactory();
            var synthesizer = new FakeSpeechSynthesizer();
            var queue = new FakeSpeechQueue();
            var director = new FakeEmotionDirector();
            var log = new FakeLogSink();
            var directorFactoryCalls = new List<string>();
            var handler = new SpeechRequestHandler(
                lexicon: lexicon,
                dialogueSessions: factory,
                synthesizer: () => synthesizer,
                queue: queue,
                profileLookup: profileLookup
                    ?? new Func<SpeakerIdentity, VoiceProfile?>(_ => profile ?? Profile(bias)),
                directorFactory: () =>
                {
                    directorFactoryCalls.Add("asked");
                    return directorPresent ? director : null;
                },
                removeStutters: removeStutters ? StutterRemover.Remove : null,
                extractStyleTags: extractStyleTags,
                log: log);
            return new Harness(lexicon, factory, synthesizer, queue, director, log, handler, directorFactoryCalls);
        }
    }

    [Fact]
    public async Task Pipeline_Order_Lexicon_Stutter_Director_Synthesis_Queue()
    {
        // "P-please FETCH the s-sword!" → lexicon swaps FETCH→BRING → stutter strips
        // "P-"/"s-" → the director and the synthesis request must both see the result.
        var h = Harness.Create();
        h.Lexicon.ApplyFunc = text => text.Replace("FETCH", "BRING");

        await h.Handler.SpeakAsync("s", Speaker, "P-please FETCH the s-sword!", CancellationToken.None);

        var line = "Please BRING the sword!";
        Assert.Equal(["P-please FETCH the s-sword!"], h.Lexicon.Calls);
        Assert.Single(h.Director.Requests);
        Assert.Equal(line, h.Director.Requests[0].Line);
        Assert.Equal(line, h.Synthesizer.LastRequest!.Text);
        Assert.Single(h.Queue.Enqueued);
        Assert.Equal(line, h.Queue.Enqueued[0].Request.Text);
        Assert.Same(Speaker, h.Queue.Enqueued[0].Speaker);
    }

    [Fact]
    public async Task Pipeline_FlowsSessionHistoryIntoDirectorContext()
    {
        var h = Harness.Create(sessions: Sessions("earlier line", "another"));

        await h.Handler.SpeakAsync("s", Speaker, "Now!", CancellationToken.None);

        var history = h.Director.Requests.Single().History;
        Assert.Equal(["earlier line", "another"], history.Select(l => l.Text));
    }

    [Fact]
    public async Task Bias_IsAddedToPlanExaggeration_AndClamped()
    {
        var h = Harness.Create(bias: 0.4f, directorPresent: false); // excited 0.75 + 0.4 = 1.15 → clamp to 1
        await h.Handler.SpeakAsync("s", Speaker, "Hello!", CancellationToken.None);
        Assert.Equal(1f, h.Synthesizer.LastRequest!.Exaggeration);

        var low = Harness.Create(bias: -2f); // neutral 0.5 - 2 → clamp to 0
        await low.Handler.SpeakAsync("s", Speaker, "Hello.", CancellationToken.None);
        Assert.Equal(0f, low.Synthesizer.LastRequest!.Exaggeration);

        var mid = Harness.Create(bias: 0.25f); // neutral 0.5 + 0.25
        await mid.Handler.SpeakAsync("s", Speaker, "Hello.", CancellationToken.None);
        Assert.Equal(0.75f, mid.Synthesizer.LastRequest!.Exaggeration);
    }

    [Fact]
    public async Task DirectorAbsent_FallsBackToRules()
    {
        var h = Harness.Create(directorPresent: false);

        // "haha nice" → rules: amused 0.6 + [laughs]; no director exists to say otherwise.
        await h.Handler.SpeakAsync("s", Speaker, "haha nice", CancellationToken.None);

        Assert.Equal(0.6f, h.Synthesizer.LastRequest!.Exaggeration);
        Assert.Equal(["laughs"], h.Synthesizer.LastRequest.Tags);
        Assert.Single(h.DirectorFactoryCalls); // the factory was consulted, and returned null
    }

    [Fact]
    public async Task Tags_AreCarriedOnTheSynthesisRequest()
    {
        var h = Harness.Create(directorPresent: false);

        // Rules: "oh sigh..." → sad with the [sigh] tag; the synthesizer renders the prefix.
        await h.Handler.SpeakAsync("s", Speaker, "oh sigh, fine", CancellationToken.None);

        Assert.Equal(["sigh"], h.Synthesizer.LastRequest!.Tags);
    }

    [Fact]
    public void PrepareText_RunsLexiconStutterTagsChain()
    {
        var h = Harness.Create(
            extractStyleTags: text => (text.Replace("|laughs|", " ").Trim(), ["laughs"]));
        h.Lexicon.ApplyFunc = text => text.Replace("FETCH", "BRING");

        var (processed, tags) = h.Handler.PrepareText("P-please FETCH the sword |laughs|");

        Assert.Equal("Please BRING the sword", processed);
        Assert.Equal(["laughs"], tags);
    }

    [Fact]
    public async Task Cancellation_PropagatesIntoSynthesis_AndAborts()
    {
        var h = Harness.Create();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<TaskCanceledException>(
            () => h.Handler.SpeakAsync("s", Speaker, "Hello", cts.Token));

        Assert.Empty(h.Queue.Enqueued);
        Assert.Equal(cts.Token, h.Synthesizer.Calls.Single().CancellationToken);
    }

    [Fact]
    public async Task CancelCurrent_CallsThroughToTheQueue()
    {
        var h = Harness.Create();

        h.Handler.CancelCurrent();

        Assert.Equal(1, h.Queue.CancelCurrentCalls);
    }

    [Fact]
    public async Task EngineNotReady_SkipsWithoutSynthesis()
    {
        var h = Harness.Create();
        h.Synthesizer.IsReady = false;

        await h.Handler.SpeakAsync("s", Speaker, "Hello", CancellationToken.None);

        Assert.Empty(h.Synthesizer.Calls);
        Assert.Empty(h.Queue.Enqueued);
        Assert.Contains(h.Log.Snapshot(), c => c.Level == "Warn");
    }

    [Fact]
    public async Task NoVoiceProfile_SkipsWithWarning()
    {
        var h = Harness.Create(profileLookup: _ => null);

        await h.Handler.SpeakAsync("s", Speaker, "Hello", CancellationToken.None);

        Assert.Empty(h.Synthesizer.Calls);
        Assert.Empty(h.Queue.Enqueued);
        Assert.Contains(h.Log.Snapshot(), c => c.Level == "Warn" && c.Message.Contains("npc: sibold"));
    }

    [Fact]
    public async Task SynthesisFailure_IsLoggedAndNotFatal()
    {
        var h = Harness.Create();
        h.Synthesizer.Throw = new SpeechSynthesisException("engine exploded");

        await h.Handler.SpeakAsync("s", Speaker, "Hello", CancellationToken.None);

        Assert.Empty(h.Queue.Enqueued);
        var error = Assert.Single(h.Log.Snapshot(), c => c.Level == "Error");
        Assert.Equal("engine exploded", error.Exception?.Message);
    }

    [Fact]
    public async Task EnqueuedItem_CarriesRenderedAudio()
    {
        var h = Harness.Create();
        var audio = new SynthesisResult([1f, 2f], 24000);
        h.Synthesizer.SynthesizeFunc = (_, _) => audio;

        await h.Handler.SpeakAsync("s", Speaker, "Hello", CancellationToken.None);

        Assert.Same(audio, h.Queue.Enqueued.Single().Audio);
    }
    [Fact]
    public async Task History_ExcludesTheCurrentLine_AndItAppendsAfterPlanning()
    {
        var h = Harness.Create();

        await h.Handler.SpeakAsync("s", Speaker, "Now!", CancellationToken.None);

        var request = h.Director.Requests.Single();
        Assert.Empty(request.History); // the director planned over the line's absence
        Assert.Equal("Now!", request.Line);
        Assert.Equal(1, h.Sessions.Count("s")); // then it landed for the next line
    }

    [Fact]
    public async Task NoProfile_SkipsBeforePlanning()
    {
        var h = Harness.Create(profileLookup: _ => null);

        await h.Handler.SpeakAsync("s", Speaker, "Hello", CancellationToken.None);

        Assert.Empty(h.DirectorFactoryCalls); // director inference never consulted
        Assert.Empty(h.Synthesizer.Calls);
        Assert.Empty(h.Queue.Enqueued);
    }

    [Fact]
    public async Task StyleTags_AreExtractedAndMerged_DirectorWins()
    {
        // The director plans [sigh]; the line carries |laughs| — both directions must
        // reach the synthesizer, the director's tags first, no duplicates.
        var h = Harness.Create(
            extractStyleTags: text => (text.Replace("|laughs|", " ").Trim(), ["laughs"]));
        h.Director.PlanFunc = _ => new EmotionPlan("sad", 0.3f, ["sigh"], 0, 1f);

        await h.Handler.SpeakAsync("s", Speaker, "fine |laughs|", CancellationToken.None);

        Assert.Equal(["sigh", "laughs"], h.Synthesizer.LastRequest!.Tags);
        Assert.Equal("fine", h.Synthesizer.LastRequest.Text);
    }

    [Fact]
    public async Task StyleTags_OverlappingTheDirectorPlan_Dedupe()
    {
        var h = Harness.Create(
            extractStyleTags: text => (text.Replace("|sigh|", " ").Trim(), ["sigh"]));
        h.Director.PlanFunc = _ => new EmotionPlan("sad", 0.3f, ["sigh"], 0, 1f);

        await h.Handler.SpeakAsync("s", Speaker, "oh |sigh|", CancellationToken.None);

        Assert.Equal(["sigh"], h.Synthesizer.LastRequest!.Tags);
    }

    [Fact]
    public async Task StyleTags_WithoutDirector_PassThroughTheRulesPlan()
    {
        var h = Harness.Create(
            directorPresent: false,
            extractStyleTags: text => (text.Replace("|laughs|", " ").Trim(), ["laughs"]));

        await h.Handler.SpeakAsync("s", Speaker, "haha |laughs|", CancellationToken.None);

        // Rules: amused 0.6 + its own [laughs]; extracted "laughs" dedupes away.
        Assert.Equal(0.6f, h.Synthesizer.LastRequest!.Exaggeration);
        Assert.Equal(["laughs"], h.Synthesizer.LastRequest.Tags);
        Assert.Equal("haha", h.Synthesizer.LastRequest.Text);
    }

    [Fact]
    public async Task Pipeline_RetriesOnceOnEngineDisposedMarker()
    {
        var h = Harness.Create();
        h.Synthesizer.SynthesizeFunc = (_, _) =>
        {
            // First call: the retired engine rejects the line; second: fresh engine.
            if (h.Synthesizer.Calls.Count == 1)
            {
                throw new SpeechSynthesisEngineDisposedException();
            }

            return new SynthesisResult([0f], 24000);
        };

        await h.Handler.SpeakAsync("s", Speaker, "Hello!", CancellationToken.None);

        Assert.Equal(2, h.Synthesizer.Calls.Count);
        Assert.Single(h.Queue.Enqueued);
        Assert.Contains(h.Log.Snapshot(), c => c.Message.Contains("Engine switched mid-line"));
    }

    [Fact]
    public async Task Pipeline_MarkerOnRetryToo_SkipsLineWithCleanError()
    {
        var h = Harness.Create();
        h.Synthesizer.Throw = new SpeechSynthesisEngineDisposedException();

        await h.Handler.SpeakAsync("s", Speaker, "Hello!", CancellationToken.None);

        Assert.Equal(2, h.Synthesizer.Calls.Count);
        Assert.Empty(h.Queue.Enqueued);
        Assert.Contains(h.Log.Snapshot(), c => c.Level == "Error");
    }
}
