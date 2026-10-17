namespace AIVoiceActing.Infrastructure.Dalamud;

using AIVoiceActing.Domain;
using AIVoiceActing.Domain.Pipeline;
using global::Dalamud.Plugin.Services;
using global::Dalamud.Utility;
using AIVoiceActing.Ports;
using FFXIVClientStructs.FFXIV.Component.GUI;

/// <summary>
/// Cutscene-subtitle capture (new — TextToTalk never read subtitles). The subtitle surface
/// is the "TalkSubtitle" addon (name verified against FFXIVClientStructs' addon list and
/// Echoglossian's cutscene reader). Every node read is null-tolerant and fails silent
/// (one debug log). The fallback already flows: cutscene NPC dialogue reaches us through
/// the chat NPC-dialogue channel via <see cref="ChatDialogueSource"/>.
/// Text collection walks the addon's visible AtkTextNode tree (depth-limited); the speaker
/// is not encoded in that surface, so the source waits briefly for the same line's echo in
/// the NPC-dialogue chat channel (<see cref="RecentDialogueSpeakers"/>) and takes the
/// speaker from there; only when none arrives does the line resolve to an anonymous speaker.
/// </summary>
public sealed class CutsceneSubtitleSource : IDialogueSource, IDisposable
{
    /// <summary>Verified unit name (FFXIVClientStructs addon list — no leading underscore).</summary>
    private const string SubtitleAddonName = "TalkSubtitle";

    private const int MaxTextNodes = 8;
    private const int MaxWalkDepth = 12;

    private readonly IGameGui gui;
    private readonly ISpeakerDirectory directory;
    private readonly Func<bool> enabled;
    private readonly IGameConditions conditions;
    private readonly IPluginLog log;
    private readonly PipelineSource<TextEmitEvent> sink;

    private readonly SubtitleChangeTracker tracker = new();
    private readonly RecentDialogueSpeakers? recentSpeakers;
    private readonly ObjectTableHintProvider? hints;
    private string? pendingText;
    private long pendingSince;

    /// <summary>How long a new subtitle waits for its chat echo before going out anonymous.</summary>
    private const long SpeakerWaitMs = 400;
    private IFramework.OnUpdateDelegate? updateHandler;
    private bool loggedMissingAddon;

    public CutsceneSubtitleSource(
        IFramework framework,
        IGameGui gui,
        ISpeakerDirectory directory,
        IGameConditions conditions,
        IPluginLog log,
        Func<bool> enabled,
        PipelineSource<TextEmitEvent> sink,
        RecentDialogueSpeakers? recentSpeakers = null,
        ObjectTableHintProvider? hints = null)
    {
        this.recentSpeakers = recentSpeakers;
        this.hints = hints;
        this.Framework = framework;
        this.gui = gui;
        this.directory = directory;
        this.conditions = conditions;
        this.log = log;
        this.enabled = enabled;
        this.sink = sink;
    }

    private IFramework Framework { get; }

    public void Start()
    {
        this.updateHandler = _ => this.OnTick();
        this.Framework.Update += this.updateHandler;
    }

    private void OnTick()
    {
        if (!this.enabled() || !this.conditions.WatchingCutscene)
        {
            return;
        }

        var text = this.ReadSubtitleText();
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        var now = Environment.TickCount64;
        if (this.pendingText != text)
        {
            this.pendingText = text;
            this.pendingSince = now;
        }

        var speakerName = this.recentSpeakers?.Find(text, now);
        if (speakerName is null && this.recentSpeakers is not null && now - this.pendingSince < SpeakerWaitMs)
        {
            return; // give the chat echo a moment to name the speaker
        }

        // The event keeps the "(cutscene)" display name so the consecutive-line dedupe
        // behaves as before; only the voice identity comes from the named speaker.
        var hint = speakerName is not null && this.hints is not null
            ? this.hints.ForTalk(speakerName)
            : new SpeakerHint(
                Name: speakerName ?? "(cutscene)", World: null, ObjectIndex: null, ModelCharaId: null,
                Race: null, Tribe: null, Sex: null);
        const string displayName = "(cutscene)";

        // Change detection before emitting: the subtitle stays visible for many frames,
        // so the same (speaker, text) pair must not re-enter the pipeline per tick.
        if (!this.tracker.ShouldEmit(displayName, text))
        {
            return;
        }

        this.sink.Emit(new TextEmitEvent(
            TextSource.CutsceneSubtitle, displayName, text, text, hint, ChatType: 0));
    }

    private string? ReadSubtitleText()
    {
        nint address = this.gui.GetAddonByName(SubtitleAddonName);
        if (address == nint.Zero)
        {
            if (!this.loggedMissingAddon)
            {
                this.loggedMissingAddon = true;
                this.log.Debug(
                    $"Cutscene subtitle addon \"{SubtitleAddonName}\" not found; " +
                    "subtitle capture stays silent (fallback: NPC-dialogue chat channel).");
            }

            return null;
        }

        unsafe
        {
            var addon = (AtkUnitBase*)address;
            if (addon is null || !addon->IsVisible || addon->RootNode is null)
            {
                return null;
            }

            var collected = new List<string>(MaxTextNodes);
            Walk(addon->RootNode, depth: 0, collected);
            return collected.Count == 0 ? null : string.Join(" ", collected);
        }
    }

    private static unsafe void Walk(AtkResNode* node, int depth, List<string> collected)
    {
        if (node is null || depth > MaxWalkDepth || collected.Count >= MaxTextNodes)
        {
            return;
        }

        if (node->Type == NodeType.Text)
        {
            var text = ((AtkTextNode*)node)->NodeText.StringPtr.AsDalamudSeString().TextValue;
            if (!string.IsNullOrWhiteSpace(text))
            {
                collected.Add(text.Trim());
            }
        }

        for (var child = node->ChildNode;
             child is not null && collected.Count < MaxTextNodes;
             child = child->NextSiblingNode)
        {
            Walk(child, depth + 1, collected);
        }
    }

    public void Dispose()
    {
        if (this.updateHandler is { } handler)
        {
            this.Framework.Update -= handler;
            this.updateHandler = null;
        }
    }
}
