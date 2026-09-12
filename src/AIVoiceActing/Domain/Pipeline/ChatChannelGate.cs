namespace AIVoiceActing.Domain.Pipeline;

/// <summary>
/// Channel-preset gate (pure port of ChatTypeMap.IsChatTypeEnabled): the channel list and
/// enable-all flag are read live per call from the active preset. Channels are ints — the
/// XivChatType numeric value or an AdditionalChatType extra — so Domain stays Dalamud-free.
/// </summary>
public sealed class ChatChannelGate
{
    private readonly Func<IReadOnlyCollection<int>?> enabledChatTypes;
    private readonly Func<bool> enableAllChatTypes;

    public ChatChannelGate(
        Func<IReadOnlyCollection<int>?> enabledChatTypes,
        Func<bool> enableAllChatTypes)
    {
        this.enabledChatTypes = enabledChatTypes;
        this.enableAllChatTypes = enableAllChatTypes;
    }

    public bool IsEnabled(int chatType) =>
        this.enableAllChatTypes() || this.enabledChatTypes()?.Contains(chatType) == true;
}
