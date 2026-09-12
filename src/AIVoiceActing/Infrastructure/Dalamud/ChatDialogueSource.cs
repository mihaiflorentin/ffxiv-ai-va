namespace AIVoiceActing.Infrastructure.Dalamud;

using AIVoiceActing.Domain;
using AIVoiceActing.Domain.Pipeline;
using AIVoiceActing.Ports;
using global::Dalamud.Game.Text;
using global::Dalamud.Game.Text.SeStringHandling;
using global::Dalamud.Plugin.Services;

/// <summary>
/// Chat-channel capture (port of ChatMessageHandler): world stripping from player-name
/// payloads, punctuation normalization, suppression of NPC dialogue that the visible
/// Talk/BattleTalk addon is already showing, the "says" prefix policy, the Only/Should
/// from-you gates and the TellOutgoing edge case. The channel id carries both XivChatType
/// and AdditionalChatType values as an int; the pipeline's channel-preset gate filters it.
/// </summary>
public sealed class ChatDialogueSource : IDialogueSource, IDisposable
{
    private readonly IChatGui chat;
    private readonly TalkAddonPoller talkPoller;
    private readonly TalkAddonPoller battleTalkPoller;
    private readonly ObjectTableHintProvider hints;
    private readonly ISpeakerDirectory directory;
    private readonly SpeakerAnnouncer announcer;
    private readonly FromYouGate fromYou;
    private readonly Func<bool> enabled;
    private readonly Func<bool> sayPlayerWorldName;
    private readonly Func<bool> readFromQuestTalkAddon;
    private readonly Func<bool> readFromBattleTalkAddon;
    private readonly Func<bool> skipMessagesFromYou;
    private readonly PipelineSource<TextEmitEvent> sink;
    private IChatGui.OnHandleableChatMessageDelegate? chatHandler;

    public ChatDialogueSource(
        IChatGui chat,
        TalkAddonPoller talkPoller,
        TalkAddonPoller battleTalkPoller,
        ObjectTableHintProvider hints,
        ISpeakerDirectory directory,
        SpeakerAnnouncer announcer,
        FromYouGate fromYou,
        Func<bool> enabled,
        Func<bool> sayPlayerWorldName,
        Func<bool> readFromQuestTalkAddon,
        Func<bool> readFromBattleTalkAddon,
        Func<bool> skipMessagesFromYou,
        PipelineSource<TextEmitEvent> sink)
    {
        this.chat = chat;
        this.talkPoller = talkPoller;
        this.battleTalkPoller = battleTalkPoller;
        this.hints = hints;
        this.directory = directory;
        this.announcer = announcer;
        this.fromYou = fromYou;
        this.enabled = enabled;
        this.sayPlayerWorldName = sayPlayerWorldName;
        this.readFromQuestTalkAddon = readFromQuestTalkAddon;
        this.readFromBattleTalkAddon = readFromBattleTalkAddon;
        this.skipMessagesFromYou = skipMessagesFromYou;
        this.sink = sink;
    }

    public event Action<DialogueLine>? LineCaptured;

    public void Start()
    {
        this.chatHandler = message =>
        {
            if (!this.enabled())
            {
                return;
            }

            this.ProcessChatMessage(message.LogKind, message.Sender, message.Message);
        };
        this.chat.ChatMessage += this.chatHandler;
    }

    private void ProcessChatMessage(XivChatType type, SeString sender, SeString messageText)
    {
        var textValue = messageText.TextValue;
        var rawText = textValue;

        if (!this.sayPlayerWorldName())
        {
            // Strip worlds from any player names (payload walk, TTT verbatim).
            textValue = SeStringUtils.StripWorldFromNames(messageText);
        }

        textValue = ChatTextNormalizer.NormalizePunctuation(textValue);

        // (TextToTalk#40) When the Talk addon is showing the same NPC dialogue, skip it.
        if (type == XivChatType.NPCDialogue &&
            this.readFromQuestTalkAddon() && this.talkPoller.IsVisible())
        {
            return;
        }

        if (type == XivChatType.NPCDialogueAnnouncements &&
            this.readFromBattleTalkAddon() && this.battleTalkPoller.IsVisible())
        {
            return;
        }

        var senderText = sender.TextValue;
        if (this.announcer.ShouldProcessSpeaker(senderText))
        {
            this.announcer.SetLastSpeaker(senderText);

            var speakerNameToSay = senderText;
            if (!this.sayPlayerWorldName())
            {
                speakerNameToSay = SeStringUtils.GetPlayerNameWithoutWorld(sender);
            }

            if (this.announcer.PartialName(speakerNameToSay) is { } partial)
            {
                speakerNameToSay = partial;
            }

            textValue = $"{speakerNameToSay} says {textValue}";
        }

        // Find the game object this speaker represents (cross-world players may be absent;
        // the player-link payload still carries the world).
        var speakerObject = this.hints.FindObject(sender);
        var hint = this.hints.FromChat(speakerObject, sender);

        var speakerNameForFilters = speakerObject?.Name.TextValue ?? senderText;
        if (!this.fromYou.OnlyMessagesFromYou(speakerNameForFilters))
        {
            return;
        }

        if (!this.fromYou.ShouldSayFromYou(speakerNameForFilters))
        {
            return;
        }

        // Edge case: the recipient is internally represented as the speaker.
        if (type == XivChatType.TellOutgoing && this.skipMessagesFromYou())
        {
            return;
        }

        var displayName = speakerObject?.Name.TextValue ?? senderText;
        this.sink.Emit(new TextEmitEvent(
            TextSource.Chat, displayName, textValue, rawText, hint, (int)type));
        this.LineCaptured?.Invoke(new DialogueLine(
            this.directory.Resolve(hint).Key, displayName, textValue, DateTimeOffset.UtcNow));
    }

    public void Dispose()
    {
        if (this.chatHandler is { } handler)
        {
            this.chat.ChatMessage -= handler;
            this.chatHandler = null;
        }
    }
}
