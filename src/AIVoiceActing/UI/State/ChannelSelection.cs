namespace AIVoiceActing.UI.State;

/// <summary>
/// Pure helpers over a preset's enabled-channel id list: "N of M on" counts and the
/// select/clear-a-category action the chat card offers. Ids the channel table does not know
/// (hand-edited config) are neither counted nor touched by category actions.
/// </summary>
public static class ChannelSelection
{
    /// <summary>How many of <paramref name="channels"/> are switched on in <paramref name="enabled"/>.</summary>
    public static int CountOn(IEnumerable<ChannelNames.Channel> channels, ICollection<int> enabled) =>
        channels.Count(c => enabled.Contains(c.Id));

    /// <summary>How many known channels are on, out of how many exist.</summary>
    public static (int On, int Total) Totals(ICollection<int> enabled)
    {
        var all = ChannelNames.All();
        return (CountOn(all, enabled), all.Count);
    }

    /// <summary>True when every channel in <paramref name="channels"/> is on (an empty group counts as not all on).</summary>
    public static bool AllOn(IReadOnlyCollection<ChannelNames.Channel> channels, ICollection<int> enabled) =>
        channels.Count > 0 && channels.All(c => enabled.Contains(c.Id));

    /// <summary>Switches every channel in <paramref name="channels"/> on or off, in place; other ids are left alone.</summary>
    public static void SetAll(IList<int> enabled, IEnumerable<ChannelNames.Channel> channels, bool on)
    {
        foreach (var channel in channels)
        {
            Set(enabled, channel.Id, on);
        }
    }

    /// <summary>Switches one channel on or off, in place, without ever duplicating an id.</summary>
    public static void Set(IList<int> enabled, int id, bool on)
    {
        if (on)
        {
            if (!enabled.Contains(id))
            {
                enabled.Add(id);
            }

            return;
        }

        while (enabled.Remove(id))
        {
        }
    }
}
