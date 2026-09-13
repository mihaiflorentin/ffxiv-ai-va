namespace AIVoiceActing.Tests;

using AIVoiceActing.Domain;
using AIVoiceActing.Domain.Handlers;
using AIVoiceActing.Ports;
using AIVoiceActing.Tests.Mock;
using Xunit;

/// <summary>
/// The DefaultExaggeration baseline (Configuration → SpeechRequestHandler): a neutral
/// emotion plan becomes the configured baseline delivery intensity, while any explicit
/// emotion keeps its rule/director value. Clamped to the 0..1 contract.
/// </summary>
public sealed class DefaultExaggerationTests
{
    private static readonly SpeakerIdentity Speaker =
        new("npc: sibold", "Sibold", Race: 1, Tribe: 1, Sex: 1, World: null);

    private static SpeechRequestHandler Handler(
        Func<float>? defaultExaggeration,
        out FakeSpeechSynthesizer synthesizer,
        out FakeSpeechQueue queue)
    {
        synthesizer = new FakeSpeechSynthesizer();
        queue = new FakeSpeechQueue();
        var synthesizerLocal = synthesizer;
        return new SpeechRequestHandler(
            lexicon: new FakeLexicon(),
            dialogueSessions: new DialogueSessionFactory(),
            synthesizer: () => synthesizerLocal,
            queue: queue,
            profileLookup: _ => new VoiceProfile(
                Speaker.Key, "default", 0f, DateTimeOffset.UnixEpoch, Custom: false),
            directorFactory: () => null,
            defaultExaggeration: defaultExaggeration,
            log: new FakeLogSink());
    }

    private static async Task<float> SynthesizeExaggerationAsync(
        SpeechRequestHandler handler, FakeSpeechQueue queue, string text)
    {
        await handler.SpeakAsync("s", Speaker, text, CancellationToken.None);
        return Assert.Single(queue.Enqueued).Request.Exaggeration;
    }

    [Fact]
    public async Task NeutralLine_UsesConfiguredBaseline()
    {
        var handler = Handler(() => 0.8f, out _, out var queue);

        Assert.Equal(0.8f, await SynthesizeExaggerationAsync(handler, queue, "plain line."));
    }

    [Fact]
    public async Task EmphaticLine_KeepsItsRuleValue_OverTheBaseline()
    {
        var handler = Handler(() => 0.8f, out _, out var queue);

        Assert.Equal(0.75f, await SynthesizeExaggerationAsync(handler, queue, "look out!"));
    }

    [Fact]
    public async Task Baseline_IsClampedToContractRange()
    {
        var high = Handler(() => 1.5f, out _, out var queue);
        Assert.Equal(1f, await SynthesizeExaggerationAsync(high, queue, "plain line."));

        var low = Handler(() => -0.5f, out _, out var lowQueue);
        Assert.Equal(0f, await SynthesizeExaggerationAsync(low, lowQueue, "plain line."));
    }

    [Fact]
    public async Task NoBaseline_NeutralKeepsTheRulesDefault()
    {
        var handler = Handler(null, out _, out var queue);

        Assert.Equal(0.5f, await SynthesizeExaggerationAsync(handler, queue, "plain line."));
    }
}
