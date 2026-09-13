namespace AIVoiceActing.UI.State;

using System.Text;

/// <summary>
/// Pure channel-name surface for the channel checkbox grid (TTT FormatChatChannelName
/// parity, hand-written without the inverted read/write quirk): the full XivChatType +
/// AdditionalChatType union as wire-value ints, GM channels labelled "GM &lt;base&gt;",
/// other enums prettified ("TellOutgoing" → "Tell Outgoing"), unknown ids rendered
/// "[channel N]". Channels carry a <see cref="Channel.Category"/> so the settings UI can
/// group them (Speech / Party / Linkshells &amp; Free Company / Combat / Loot &amp;
/// Crafting / System / GM). Deliberately Dalamud-free so the unit tests compile it from
/// source; the wire values were verified against the installed API (XivChatType dump).
/// </summary>
public static class ChannelNames
{
    /// <summary>One channel row for the checkbox grid.</summary>
    /// <param name="Id">Game wire value shared by XivChatType and AdditionalChatType.</param>
    /// <param name="Name">Friendly display name.</param>
    /// <param name="Category">Settings-UI group key (see <see cref="CategoryOrder"/>).</param>
    public sealed record Channel(int Id, string Name, string Category);

    /// <summary>Display order of the category groups in the settings UI.</summary>
    public static readonly string[] CategoryOrder =
    [
        "Speech",
        "Party",
        "Linkshells & Free Company",
        "Combat",
        "Loot & Crafting",
        "System",
        "GM",
    ];

    private static readonly IReadOnlyList<Channel> AllChannels =
    [
        new(1, "Debug", "System"),
        new(2, "Urgent", "System"),
        new(3, "Notice", "System"),
        new(10, "Say", "Speech"),
        new(11, "Shout", "Speech"),
        new(12, "Tell Outgoing", "Speech"),
        new(13, "Tell Incoming", "Speech"),
        new(14, "Party", "Party"),
        new(15, "Alliance", "Party"),
        new(16, "Linkshell 1", "Linkshells & Free Company"),
        new(17, "Linkshell 2", "Linkshells & Free Company"),
        new(18, "Linkshell 3", "Linkshells & Free Company"),
        new(19, "Linkshell 4", "Linkshells & Free Company"),
        new(20, "Linkshell 5", "Linkshells & Free Company"),
        new(21, "Linkshell 6", "Linkshells & Free Company"),
        new(22, "Linkshell 7", "Linkshells & Free Company"),
        new(23, "Linkshell 8", "Linkshells & Free Company"),
        new(24, "Free Company", "Linkshells & Free Company"),
        new(27, "Novice Network", "Linkshells & Free Company"),
        new(28, "Custom Emotes", "Speech"),
        new(29, "Standard Emotes", "Speech"),
        new(30, "Yell", "Speech"),
        new(32, "Cross-world Party", "Party"),
        new(36, "PvP Team", "Party"),
        new(37, "Cross-world Linkshell 1", "Linkshells & Free Company"),
        new(41, "Damage", "Combat"),
        new(42, "Miss", "Combat"),
        new(43, "Action", "Combat"),
        new(44, "Item", "Combat"),
        new(45, "Healing", "Combat"),
        new(46, "Benefit", "Combat"),
        new(47, "Benefit (self)", "Combat"),
        new(48, "Buff Lost", "Combat"),
        new(49, "Debuff Lost", "Combat"),
        new(54, "Glamour", "System"),
        new(55, "Alarm", "System"),
        new(56, "Echo", "Speech"),
        new(57, "System", "System"),
        new(58, "System Error", "System"),
        new(59, "Gathering System", "Loot & Crafting"),
        new(60, "Error", "System"),
        new(61, "NPC Dialogue", "Speech"),
        new(62, "Loot Notice", "Loot & Crafting"),
        new(64, "Progress", "Loot & Crafting"),
        new(65, "Loot Roll", "Loot & Crafting"),
        new(66, "Crafting", "Loot & Crafting"),
        new(67, "Gathering", "Loot & Crafting"),
        new(68, "NPC Dialogue (Announcements)", "Speech"),
        new(69, "Free Company Announcement", "Linkshells & Free Company"),
        new(70, "Free Company Login", "Linkshells & Free Company"),
        new(71, "Retainer Sale", "Loot & Crafting"),
        new(72, "Periodic Recruitment", "System"),
        new(73, "Sign", "System"),
        new(74, "Random Number", "System"),
        new(75, "Novice Network System", "Linkshells & Free Company"),
        new(76, "Orchestrion", "System"),
        new(77, "PvP Team Announcement", "Party"),
        new(78, "PvP Team Login", "Party"),
        new(79, "Message Book", "System"),
        new(80, "GM Tell", "GM"),
        new(81, "GM Say", "GM"),
        new(82, "GM Shout", "GM"),
        new(83, "GM Yell", "GM"),
        new(84, "GM Party", "GM"),
        new(85, "GM Free Company", "GM"),
        new(86, "GM Linkshell 1", "GM"),
        new(87, "GM Linkshell 2", "GM"),
        new(88, "GM Linkshell 3", "GM"),
        new(89, "GM Linkshell 4", "GM"),
        new(90, "GM Linkshell 5", "GM"),
        new(91, "GM Linkshell 6", "GM"),
        new(92, "GM Linkshell 7", "GM"),
        new(93, "GM Linkshell 8", "GM"),
        new(94, "GM Novice Network", "GM"),
        new(101, "Cross-world Linkshell 2", "Linkshells & Free Company"),
        new(102, "Cross-world Linkshell 3", "Linkshells & Free Company"),
        new(103, "Cross-world Linkshell 4", "Linkshells & Free Company"),
        new(104, "Cross-world Linkshell 5", "Linkshells & Free Company"),
        new(105, "Cross-world Linkshell 6", "Linkshells & Free Company"),
        new(106, "Cross-world Linkshell 7", "Linkshells & Free Company"),
        new(107, "Cross-world Linkshell 8", "Linkshells & Free Company"),
        new(2091, "Action Used On You", "System"),
        new(2218, "Failed Action Used On You", "System"),
        new(2219, "Action Readied By You", "System"),
        new(2222, "Beneficial Effect On You", "System"),
        new(2224, "Beneficial Effect On You Ended", "System"),
        new(2729, "Damage Dealt By You", "System"),
        new(2735, "Detrimental Effects Inflicted By You", "System"),
        new(2874, "Enemy Defeated By You", "System"),
        new(8235, "Action Used By Other Player", "System"),
        new(8750, "Beneficial Effect On Other Player", "System"),
        new(8751, "Detrimental Effect On Other Player", "System"),
        new(8752, "Beneficial Effect On Other Player Ended", "System"),
        new(8774, "Free Company Member Login", "Linkshells & Free Company"),
        new(10283, "Action Readied By Engaged Enemy", "System"),
        new(10409, "Damage You Are Dealt", "System"),
        new(10410, "Failed Attacks On You", "System"),
        new(10929, "Detrimental Effect On Enemy Ended", "System"),
    ];

    /// <summary>The whole grid: XivChatType union first, then the AdditionalChatType extras.</summary>
    public static IReadOnlyList<Channel> All() => AllChannels;

    /// <summary>Channels grouped by category, in <see cref="CategoryOrder"/> order.</summary>
    public static IEnumerable<(string Category, IReadOnlyList<Channel> Channels)> ByCategory()
    {
        foreach (var category in CategoryOrder)
        {
            var channels = AllChannels.Where(c => c.Category == category).ToArray();
            if (channels.Length > 0)
            {
                yield return (category, channels);
            }
        }
    }

    /// <summary>One-line description of what a category covers (settings-UI tooltip).</summary>
    public static string CategoryDescription(string category) => category switch
    {
        "Speech" => "Spoken chat and roleplay: say, shout, yells, tells, emotes, and NPC dialogue.",
        "Party" => "Group play: party, alliance, cross-world party, and PvP team chat.",
        "Linkshells & Free Company" => "Social channels: linkshells, cross-world linkshells, free company chat and logins, novice network.",
        "Combat" => "Battle log lines. Mostly noise — enable selectively (e.g. only 'Damage You Are Dealt').",
        "Loot & Crafting" => "Loot notices, loot rolls, crafting and gathering results, retainer sales.",
        "System" => "Server messages, errors, alarms, signs, recruitment, orchestrion, and debug output.",
        "GM" => "Game-master broadcasts. Rare; usually left on.",
        _ => string.Empty,
    };

    /// <summary>Friendly name for any channel wire value; "[channel N]" when unknown.</summary>
    public static string FriendlyName(int channel) =>
        AllChannels.FirstOrDefault(c => c.Id == channel)?.Name ?? $"[channel {channel}]";
}
