namespace AIVoiceActing.Infrastructure.Dalamud.GameEnums;

using AIVoiceActing.Domain.Pipeline;
using global::Dalamud.Game.Text;

/// <summary>
/// Dalamud-bound channel facts (partial port of TextToTalk's ChatTypeMap): GM-channel →
/// base-channel mapping for preset normalization. The pure enabled-check lives in
/// <see cref="ChatChannelGate"/>; the localized name lookups (excel-backed) arrive with the
/// UI/configuration step.
/// </summary>
public static class ChatTypeMap
{
    public static readonly IReadOnlyDictionary<XivChatType, XivChatType> GmBaseChatTypes =
        new Dictionary<XivChatType, XivChatType>
        {
            [XivChatType.GmTell] = XivChatType.TellIncoming,
            [XivChatType.GmSay] = XivChatType.Say,
            [XivChatType.GmShout] = XivChatType.Shout,
            [XivChatType.GmYell] = XivChatType.Yell,
            [XivChatType.GmParty] = XivChatType.Party,
            [XivChatType.GmFreeCompany] = XivChatType.FreeCompany,
            [XivChatType.GmLinkshell1] = XivChatType.Ls1,
            [XivChatType.GmLinkshell2] = XivChatType.Ls2,
            [XivChatType.GmLinkshell3] = XivChatType.Ls3,
            [XivChatType.GmLinkshell4] = XivChatType.Ls4,
            [XivChatType.GmLinkshell5] = XivChatType.Ls5,
            [XivChatType.GmLinkshell6] = XivChatType.Ls6,
            [XivChatType.GmLinkshell7] = XivChatType.Ls7,
            [XivChatType.GmLinkshell8] = XivChatType.Ls8,
            [XivChatType.GmNoviceNetwork] = XivChatType.NoviceNetwork,
        };
}
