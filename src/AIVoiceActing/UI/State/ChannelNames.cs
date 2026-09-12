namespace AIVoiceActing.UI.State;

using System.Text;

/// <summary>
/// Pure channel-name surface for the channel checkbox grid (TTT FormatChatChannelName
/// parity, hand-written without the inverted read/write quirk): the full XivChatType +
/// AdditionalChatType union as wire-value ints, GM channels labelled "GM &lt;base&gt;",
/// other enums prettified ("TellOutgoing" → "Tell Outgoing"), unknown ids rendered
/// "[channel N]". Deliberately Dalamud-free so the unit tests compile it from source;
/// the wire values were verified against the installed API (XivChatType dump).
/// </summary>
public static class ChannelNames
{
    /// <summary>One channel row for the checkbox grid.</summary>
    /// <param name="Id">Game wire value shared by XivChatType and AdditionalChatType.</param>
    /// <param name="Name">Friendly display name.</param>
    public sealed record Channel(int Id, string Name);

    private static readonly IReadOnlyList<Channel> AllChannels =
    [
        new(1, "Debug"),
        new(2, "Urgent"),
        new(3, "Notice"),
        new(10, "Say"),
        new(11, "Shout"),
        new(12, "Tell Outgoing"),
        new(13, "Tell Incoming"),
        new(14, "Party"),
        new(15, "Alliance"),
        new(16, "Linkshell 1"),
        new(17, "Linkshell 2"),
        new(18, "Linkshell 3"),
        new(19, "Linkshell 4"),
        new(20, "Linkshell 5"),
        new(21, "Linkshell 6"),
        new(22, "Linkshell 7"),
        new(23, "Linkshell 8"),
        new(24, "Free Company"),
        new(27, "Novice Network"),
        new(28, "Custom Emotes"),
        new(29, "Standard Emotes"),
        new(30, "Yell"),
        new(32, "Cross-world Party"),
        new(36, "PvP Team"),
        new(37, "Cross-world Linkshell 1"),
        new(41, "Damage"),
        new(42, "Miss"),
        new(43, "Action"),
        new(44, "Item"),
        new(45, "Healing"),
        new(46, "Benefit"),
        new(47, "Benefit (self)"),
        new(48, "Buff Lost"),
        new(49, "Debuff Lost"),
        new(54, "Glamour"),
        new(55, "Alarm"),
        new(56, "Echo"),
        new(57, "System"),
        new(58, "System Error"),
        new(59, "Gathering System"),
        new(60, "Error"),
        new(61, "NPC Dialogue"),
        new(62, "Loot Notice"),
        new(64, "Progress"),
        new(65, "Loot Roll"),
        new(66, "Crafting"),
        new(67, "Gathering"),
        new(68, "NPC Dialogue (Announcements)"),
        new(69, "Free Company Announcement"),
        new(70, "Free Company Login"),
        new(71, "Retainer Sale"),
        new(72, "Periodic Recruitment"),
        new(73, "Sign"),
        new(74, "Random Number"),
        new(75, "Novice Network System"),
        new(76, "Orchestrion"),
        new(77, "PvP Team Announcement"),
        new(78, "PvP Team Login"),
        new(79, "Message Book"),
        new(80, "GM Tell"),
        new(81, "GM Say"),
        new(82, "GM Shout"),
        new(83, "GM Yell"),
        new(84, "GM Party"),
        new(85, "GM Free Company"),
        new(86, "GM Linkshell 1"),
        new(87, "GM Linkshell 2"),
        new(88, "GM Linkshell 3"),
        new(89, "GM Linkshell 4"),
        new(90, "GM Linkshell 5"),
        new(91, "GM Linkshell 6"),
        new(92, "GM Linkshell 7"),
        new(93, "GM Linkshell 8"),
        new(94, "GM Novice Network"),
        new(101, "Cross-world Linkshell 2"),
        new(102, "Cross-world Linkshell 3"),
        new(103, "Cross-world Linkshell 4"),
        new(104, "Cross-world Linkshell 5"),
        new(105, "Cross-world Linkshell 6"),
        new(106, "Cross-world Linkshell 7"),
        new(107, "Cross-world Linkshell 8"),
        new(2091, "Action Used On You"),
        new(2218, "Failed Action Used On You"),
        new(2219, "Action Readied By You"),
        new(2222, "Beneficial Effect On You"),
        new(2224, "Beneficial Effect On You Ended"),
        new(2729, "Damage Dealt By You"),
        new(2735, "Detrimental Effects Inflicted By You"),
        new(2874, "Enemy Defeated By You"),
        new(8235, "Action Used By Other Player"),
        new(8750, "Beneficial Effect On Other Player"),
        new(8751, "Detrimental Effect On Other Player"),
        new(8752, "Beneficial Effect On Other Player Ended"),
        new(8774, "Free Company Member Login"),
        new(10283, "Action Readied By Engaged Enemy"),
        new(10409, "Damage You Are Dealt"),
        new(10410, "Failed Attacks On You"),
        new(10929, "Detrimental Effect On Enemy Ended"),
    ];

    /// <summary>The whole grid: XivChatType union first, then the AdditionalChatType extras.</summary>
    public static IReadOnlyList<Channel> All() => AllChannels;

    /// <summary>Friendly name for any channel wire value; "[channel N]" when unknown.</summary>
    public static string FriendlyName(int channel) =>
        AllChannels.FirstOrDefault(c => c.Id == channel)?.Name ?? $"[channel {channel}]";
}
