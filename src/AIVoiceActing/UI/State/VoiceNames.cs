namespace AIVoiceActing.UI.State;

using AIVoiceActing.Ports;

/// <summary>Voice ids turned into the names people read.</summary>
public static class VoiceNames
{
    /// <summary>The voice's display name; an id no pack provides comes back unchanged.</summary>
    public static string Display(IVoiceLibrary library, string voiceId) =>
        library.Find(voiceId)?.DisplayName ?? voiceId;

    /// <summary>True when no enabled pack declares the id.</summary>
    public static bool IsMissing(IVoiceLibrary library, string voiceId) => library.Find(voiceId) is null;

    /// <summary>"Heart, Michael and 2 more" for up to <paramref name="show"/> names; "No voices set" for none.</summary>
    public static string Summary(IVoiceLibrary library, IReadOnlyList<string> voiceIds, int show = 3)
    {
        if (voiceIds.Count == 0)
        {
            return "No voices set";
        }

        var names = voiceIds.Take(show).Select(id => Display(library, id)).ToList();
        var extra = voiceIds.Count - names.Count;
        return extra > 0 ? $"{string.Join(", ", names)} and {extra} more" : string.Join(", ", names);
    }
}
