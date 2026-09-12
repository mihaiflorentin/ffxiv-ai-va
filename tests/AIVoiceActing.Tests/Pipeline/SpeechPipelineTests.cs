namespace AIVoiceActing.Tests.Pipeline;

using AIVoiceActing.Domain;
using AIVoiceActing.Domain.Handlers;
using AIVoiceActing.Domain.Pipeline;
using AIVoiceActing.Ports;
using AIVoiceActing.Tests.Mock;
using Xunit;

/// <summary>
/// End-to-end pipeline semantics over fakes: merge → dedupe → channel preset →
/// exclusions/triggers → PC rate limit → speech handler; plus the context-window feed via
/// the session id deriver.
/// </summary>
public sealed class SpeechPipelineTests
{
    private static SpeakerHint Hint(string name, ushort? world = null) =>
        new(name, world, null, null, null, null, null);

    private static TextEmitEvent TalkLine(string speaker, string text) =>
        new(TextSource.Talk, speaker, text, text, Hint(speaker), 0);

    private static TextEmitEvent ChatLine(string speaker, string text, int chatType = 57) =>
        new(TextSource.Chat, speaker, text, text, Hint(speaker, world: 66), chatType);

    private sealed class Harness
    {
        public PipelineSource<TextEmitEvent> Source { get; } = new();
        public FakeSpeechQueue Queue { get; } = new();
        public DialogueSessionFactory Sessions { get; } = new();
        public long Now { get; set; }
        public bool Enabled { get; set; } = true;
        public bool CutsceneActive { get; set; }
        public bool TalkVisible { get; set; }
        public IReadOnlyCollection<int>? EnabledChatTypes { get; set; }

        public SpeechPipeline Pipeline { get; }

        public Harness(
            IReadOnlyList<TriggerSpec>? good = null,
            IReadOnlyList<TriggerSpec>? bad = null,
            IReadOnlyCollection<int>? enabledChatTypes = null,
            bool rateLimitOn = false,
            double messagesPerSecond = 1)
        {
            var handler = new SpeechRequestHandler(
                lexicon: new FakeLexicon(),
                dialogueSessions: this.Sessions,
                synthesizer: new FakeSpeechSynthesizer(),
                queue: this.Queue,
                profileLookup: speaker => new VoiceProfile(
                    speaker.Key, "default", 0f, DateTimeOffset.UtcNow, false),
                directorFactory: () => null,
                log: null);
            this.Pipeline = new SpeechPipeline(
                [this.Source],
                enabled: () => this.Enabled,
                chatGate: new ChatChannelGate(
                    () => this.EnabledChatTypes ?? enabledChatTypes,
                    () => (this.EnabledChatTypes ?? enabledChatTypes) is null),
                textGate: new TextGate(() => good ?? [], () => bad ?? []),
                rateLimiter: new ConfiguredRateLimiter(
                    () => rateLimitOn, () => messagesPerSecond, () => this.Now),
                resolveSpeaker: hint => new SpeakerIdentity(
                    hint.World is { } world
                        ? $"pc:{hint.Name}@{world}"
                        : $"npc:{hint.Name.ToLowerInvariant()}",
                    hint.Name, hint.Race, hint.Tribe, hint.Sex, hint.World),
                cutsceneActive: () => this.CutsceneActive,
                talkVisible: () => this.TalkVisible,
                dialogueSessions: this.Sessions,
                handler: handler);
        }
    }

    [Fact]
    public void TalkLine_IsSpoken()
    {
        var h = new Harness();
        h.Source.Emit(TalkLine("Yda", "Hello"));
        Assert.Single(h.Queue.Enqueued);
        Assert.Equal("npc:yda", h.Queue.Enqueued[0].Speaker.Key);
    }

    [Fact]
    public void ConsecutiveEquivalentLines_CollapseAcrossSources()
    {
        var h = new Harness();
        h.Source.Emit(TalkLine("Yda", "Hello"));
        h.Source.Emit(ChatLine("Yda", "Hello")); // equivalent speaker+text, different source
        Assert.Single(h.Queue.Enqueued);
    }

    [Fact]
    public void DisabledPipeline_DropsEverything()
    {
        var h = new Harness();
        h.Enabled = false;
        h.Source.Emit(TalkLine("Yda", "Hello"));
        Assert.Empty(h.Queue.Enqueued);
    }

    [Fact]
    public void ChatChannelGate_FiltersChatOnly()
    {
        var h = new Harness(enabledChatTypes: [28]);
        h.Source.Emit(ChatLine("Yda", "wrong channel", chatType: 57));
        h.Source.Emit(ChatLine("Yda", "right channel", chatType: 28));
        h.Source.Emit(TalkLine("Yda", "talk bypasses the preset")); // non-chat skips the gate
        Assert.Equal(2, h.Queue.Enqueued.Count);
    }

    [Fact]
    public void Exclusion_WinsOverTrigger()
    {
        var good = new List<TriggerSpec> { new("secret", false) };
        var bad = new List<TriggerSpec> { new("secret", false) };
        var h = new Harness(good: good, bad: bad);

        h.Source.Emit(TalkLine("Yda", "the secret plan"));     // both match → dropped
        h.Source.Emit(TalkLine("Yda", "another secret word")); // exclusion absent → dropped
        Assert.Empty(h.Queue.Enqueued);
    }

    [Fact]
    public void Trigger_OnlyMatchingLinesPass_WhenTriggersConfigured()
    {
        var h = new Harness(good: [new TriggerSpec("gift", false)]);
        h.Source.Emit(TalkLine("Yda", "no match here"));
        h.Source.Emit(TalkLine("Yda", "a gift for you"));
        Assert.Single(h.Queue.Enqueued);
    }

    [Fact]
    public void RateLimit_AppliesToPcSpeakersOnly()
    {
        var h = new Harness(rateLimitOn: true, messagesPerSecond: 0 /* limit forever */);
        h.Source.Emit(ChatLine("Player", "one")); // pc:... → passes
        h.Source.Emit(ChatLine("Player", "two")); // pc:... → limited
        h.Source.Emit(TalkLine("Npc", "three"));  // npc:... → unlimited
        h.Source.Emit(TalkLine("Npc", "four"));
        Assert.Equal(3, h.Queue.Enqueued.Count);
    }

    [Fact]
    public void TalkLines_FeedTheTalkContextWindow()
    {
        var h = new Harness { TalkVisible = true };
        h.Source.Emit(TalkLine("Yda", "Hello"));
        Assert.Equal(1, h.Sessions.Count("talk:npc:yda"));
        Assert.Empty(h.Sessions.GetContext("talk:npc:other"));
    }

    [Fact]
    public void ChatLines_FeedNoContextWindow()
    {
        var h = new Harness();
        h.Source.Emit(ChatLine("Player", "Hi"));
        Assert.Single(h.Queue.Enqueued);
        Assert.Equal(0, h.Sessions.Count("cutscene"));
    }

    [Fact]
    public void VoiceLinePlayback_CancelsCurrentSpeech()
    {
        var h = new Harness();
        h.Pipeline.NotifyVoiceLinePlayback();
        Assert.Equal(1, h.Queue.CancelCurrentCalls);
    }
}
