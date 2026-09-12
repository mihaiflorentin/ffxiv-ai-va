namespace AIVoiceActing.Infrastructure.Dalamud;

using global::Dalamud.Game.ClientState.Conditions;
using global::Dalamud.Plugin.Services;
using global::Dalamud.Utility;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;

/// <summary>
/// Framework-polled address cache + node reader for a Talk-family addon (ports TextToTalk's
/// AddonManager address refresh, AddonTalkManager/AddonBattleTalkManager readers, and
/// TalkUtils.ReadTalkAddon). The address is resolved lazily while logged in and re-resolved
/// from zero only; node reads are null-tolerant (a mid-layout addon reads as empty text).
/// "Talk" exposes the name/body nodes at fixed offsets 220/228; "_BattleTalk" exposes named
/// Speaker/Text nodes.
/// </summary>
public sealed unsafe class TalkAddonPoller
{
    private const int MaxTextNodes = 4;
    private const int MaxWalkDepth = 12;
    private readonly IClientState clientState;
    private readonly ICondition condition;
    private readonly IGameGui gui;
    private readonly string addonName;
    private readonly bool battleTalk;
    private nint address;

    public TalkAddonPoller(
        IClientState clientState,
        ICondition condition,
        IGameGui gui,
        string addonName)
    {
        this.clientState = clientState;
        this.condition = condition;
        this.gui = gui;
        this.addonName = addonName;
        this.battleTalk = addonName == "_BattleTalk";
    }

    /// <summary>Refreshes the cached addon address (call once per framework tick).</summary>
    public void UpdateAddress()
    {
        if (!this.clientState.IsLoggedIn || this.condition[ConditionFlag.CreatingCharacter])
        {
            this.address = nint.Zero;
            return;
        }

        if (this.address == nint.Zero)
        {
            this.address = this.gui.GetAddonByName(this.addonName);
        }
    }

    public bool IsVisible()
    {
        var addon = this.Base();
        return addon is not null && addon->IsVisible;
    }

    /// <summary>(speaker, text) node read; null when the addon is not resolvable right now.</summary>
    public (string? Speaker, string? Text)? ReadText()
    {
        if (this.address == nint.Zero)
        {
            return null;
        }

        return this.battleTalk
            ? ReadBattleTalkNodes((AtkUnitBase*)this.address)
            : ReadTalkAddon((AddonTalk*)this.address);
    }

    private static (string? Speaker, string? Text) ReadTalkAddon(AddonTalk* talkAddon)
    {
        if (talkAddon is null)
        {
            return (null, null);
        }

        return (
            ReadTextNode(talkAddon->AtkTextNode220),
            ReadTextNode(talkAddon->AtkTextNode228));
    }

    /// <summary>
    /// Generic reader for "_BattleTalk": the bundled FFXIVClientStructs has no typed
    /// AddonBattleTalk struct (TextToTalk ships a newer one), so the addon's text nodes
    /// are walked depth-first — first node is the speaker, the rest join into the body.
    /// </summary>
    private static (string? Speaker, string? Text) ReadBattleTalkNodes(AtkUnitBase* addon)
    {
        if (addon is null || !addon->IsVisible || addon->RootNode is null)
        {
            return (null, null);
        }

        var nodes = new List<string>(4);
        CollectTextNodes(addon->RootNode, depth: 0, nodes);
        return nodes.Count switch
        {
            0 => (null, null),
            1 => ("", nodes[0]),
            _ => (nodes[0], string.Join(" ", nodes.Skip(1))),
        };
    }

    private static void CollectTextNodes(AtkResNode* node, int depth, List<string> nodes)
    {
        if (node is null || depth > MaxWalkDepth || nodes.Count >= MaxTextNodes)
        {
            return;
        }

        if (node->Type == NodeType.Text)
        {
            var text = ReadTextNode((AtkTextNode*)node);
            if (text.Length > 0)
            {
                nodes.Add(text);
            }
        }

        for (var child = node->ChildNode;
             child is not null && nodes.Count < MaxTextNodes;
             child = child->NextSiblingNode)
        {
            CollectTextNodes(child, depth + 1, nodes);
        }
    }


    private static string ReadTextNode(AtkTextNode* textNode)
    {
        if (textNode is null)
        {
            return "";
        }

        var seString = textNode->NodeText.StringPtr.AsDalamudSeString();
        return seString.TextValue
            .Trim()
            .Replace("\n", "")
            .Replace("\r", "");
    }

    private AtkUnitBase* Base() => (AtkUnitBase*)this.address;
}
