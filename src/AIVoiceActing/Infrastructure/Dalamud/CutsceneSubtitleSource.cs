namespace AIVoiceActing.Infrastructure.Dalamud;

using AIVoiceActing.Domain;
using AIVoiceActing.Domain.Pipeline;
using global::Dalamud.Plugin.Services;
using global::Dalamud.Utility;
using AIVoiceActing.Ports;
using FFXIVClientStructs.FFXIV.Component.GUI;

/// <summary>
/// Cutscene-subtitle capture (new — TextToTalk never read subtitles). The subtitle surface
/// is NOT a verified addon: the addon name below is a single named constant
/// (best current guess — FFXIVClientStructs ships an AddonTalkSubtitle struct, so the
/// "_TalkSubtitle" unit base is the likely surface) to flip during
/// in-game verification, and every node read is null-tolerant and fails silent (one debug
/// log) so a wrong name costs nothing. The fallback already flows: cutscene NPC dialogue
/// reaches us through the chat NPC-dialogue channel via <see cref="ChatDialogueSource"/>.
/// Text collection walks the addon's visible AtkTextNode tree (depth-limited); the speaker
/// is not encoded in that surface, so lines resolve to an anonymous speaker.
/// </summary>
public sealed class CutsceneSubtitleSource : IDialogueSource, IDisposable
{
    /// <summary>UNVERIFIED — flip during in-game verification (see class doc).</summary>
    private const string SubtitleAddonName = "_TalkSubtitle";

    private const int MaxTextNodes = 8;
    private const int MaxWalkDepth = 12;

    private readonly IGameGui gui;
    private readonly ISpeakerDirectory directory;
    private readonly Func<bool> enabled;
    private readonly IGameConditions conditions;
    private readonly IPluginLog log;
    private readonly PipelineSource<TextEmitEvent> sink;

    private readonly SubtitleChangeTracker tracker = new();
    private IFramework.OnUpdateDelegate? updateHandler;
    private bool loggedMissingAddon;

    public CutsceneSubtitleSource(
        IFramework framework,
        IGameGui gui,
        ISpeakerDirectory directory,
        IGameConditions conditions,
        IPluginLog log,
        Func<bool> enabled,
        PipelineSource<TextEmitEvent> sink)
    {
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

        var hint = new SpeakerHint(
            Name: "(cutscene)", World: null, ObjectIndex: null, ModelCharaId: null,
            Race: null, Tribe: null, Sex: null);
        var identity = this.directory.Resolve(hint);

        // Change detection before emitting: the subtitle stays visible for many frames,
        // so the same (speaker, text) pair must not re-enter the pipeline per tick.
        if (!this.tracker.ShouldEmit(identity.DisplayName, text))
        {
            return;
        }

        this.sink.Emit(new TextEmitEvent(
            TextSource.CutsceneSubtitle, identity.DisplayName, text, text, hint, ChatType: 0));
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
