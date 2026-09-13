namespace AIVoiceActing.Infrastructure.Dalamud;

using AIVoiceActing.Domain;
using AIVoiceActing.Domain.Pipeline;
using AIVoiceActing.Ports;
using global::Dalamud.Plugin.Services;

/// <summary>
/// Quest-dialogue capture from the "Talk" addon (port of AddonTalkHandler): framework
/// polling with config gates, consecutive-sample dedupe and the voiced-line courtesy via
/// the pure <see cref="AddonTalkProcessor"/>, then speaker resolution, the "says" prefix
/// policy and the from-you gate before emitting into the pipeline sink. Advancing or
/// closing the addon raises <see cref="SpeechInterrupted"/> — the container wires it to
/// <c>SpeechQueue.CancelCurrent</c> (TTT merged its advance/close events into a cancel
/// stream).
/// </summary>
public sealed class TalkDialogueSource : IDialogueSource, IDisposable
{
    private readonly IFramework framework;
    private readonly TalkAddonPoller poller;
    private readonly ObjectTableHintProvider hints;
    private readonly ISpeakerDirectory directory;
    private readonly Func<bool> enabled;
    private readonly Func<bool> readFromAddon;
    private readonly AddonTalkProcessor processor;
    private readonly SpeakerAnnouncer announcer;
    private readonly FromYouGate fromYou;
    private readonly PipelineSource<TextEmitEvent> sink;
    private IFramework.OnUpdateDelegate? updateHandler;

    public TalkDialogueSource(
        IFramework framework,
        TalkAddonPoller poller,
        ObjectTableHintProvider hints,
        ISpeakerDirectory directory,
        Func<bool> enabled,
        Func<bool> readFromAddon,
        Func<bool> skipVoicedQuestText,
        SpeakerAnnouncer announcer,
        FromYouGate fromYou,
        PipelineSource<TextEmitEvent> sink)
    {
        this.framework = framework;
        this.poller = poller;
        this.hints = hints;
        this.directory = directory;
        this.enabled = enabled;
        this.readFromAddon = readFromAddon;
        this.processor = new AddonTalkProcessor(skipVoicedQuestText);
        this.announcer = announcer;
        this.fromYou = fromYou;
        this.sink = sink;
    }


    /// <summary>The on-screen line moved on or closed; current speech is stale.</summary>
    public event Action? SpeechInterrupted;

    public void Start()
    {
        this.updateHandler = _ => this.OnTick();
        this.framework.Update += this.updateHandler;
    }

    /// <summary>The game's own voice line just played — re-sample the addon so the voiced
    /// text is suppressed (TTT SoundHandler's VoiceLinePlayback poll source).</summary>
    public void PollOnVoiceLine() => this.Poll(AddonPollSource.VoiceLinePlayback);

    private void OnTick()
    {
        if (!this.enabled() || !this.readFromAddon())
        {
            return;
        }

        this.poller.UpdateAddress();
        this.Poll(AddonPollSource.FrameworkUpdate);
    }

    private void Poll(AddonPollSource pollSource)
    {
        var sample = this.poller.IsVisible()
            ? this.poller.ReadText() is { } read
                ? new AddonTalkState(read.Speaker, read.Text, pollSource)
                : AddonTalkState.Closed
            : AddonTalkState.Closed;

        var result = this.processor.Poll(sample);
        // Advanced fires on the open→closed transition and on every changed line; closed
        // ticks after the first are no-ops (review round 1: no per-tick cancel spam).
        if (result.Advanced)
        {
            this.SpeechInterrupted?.Invoke();
        }

        if (result.Decision != TalkDecision.Speak)
        {
            return;
        }

        this.Emit(result);
    }

    private void Emit(AddonTalkResult result)
    {
        // TTT order: says-prefix first, then identity resolution, then the from-you gate.
        var text = result.Text;
        if (this.announcer.ShouldProcessSpeaker(result.Speaker))
        {
            this.announcer.SetLastSpeaker(result.Speaker);
            var speakerNameToSay = this.announcer.PartialName(result.Speaker) ?? result.Speaker;
            text = SpeakerAnnouncer.ComposeSaid(speakerNameToSay, text);
        }

        var hint = this.hints.ForTalk(result.Speaker);
        var identity = this.directory.Resolve(hint);
        if (!this.fromYou.ShouldSayFromYou(result.Speaker))
        {
            return;
        }

        this.sink.Emit(new TextEmitEvent(
            TextSource.Talk, identity.DisplayName, text, result.RawText, hint, ChatType: 0));
    }

    public void Dispose()
    {
        if (this.updateHandler is { } handler)
        {
            this.framework.Update -= handler;
            this.updateHandler = null;
        }
    }
}
