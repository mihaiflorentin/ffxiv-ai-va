namespace AIVoiceActing.Domain.Pipeline;

using System.Text.RegularExpressions;

/// <summary>
/// Sound-path classification for the voiced-line courtesy (pure port of SoundHandler's
/// regexes): which loaded .scd containers are the game's own voice lines (so synthesized
/// speech must yield) and which well-known non-voice trees are ignored outright.
/// </summary>
public static partial class VoiceLinePathMatcher
{
    public const string SoundContainerSuffix = ".scd";

    [GeneratedRegex(@"^(cut/.*/(vo_|voice)|sound/voice/Vo_Line/)")]
    private static partial Regex VoiceLineRegex();

    [GeneratedRegex(@"^(bgcommon|music|sound/(battle|foot|instruments|strm|vfx|voice/Vo_Emote|zingle))/")]
    private static partial Regex IgnoredSoundRegex();

    /// <summary>True when the sound path is a voice-acted line (cutscene/quest voice lines).</summary>
    public static bool IsVoiceLine(string path) => VoiceLineRegex().IsMatch(path);

    /// <summary>True when the sound path is a known non-voice tree (never a voice line).</summary>
    public static bool IsIgnoredSound(string path) => IgnoredSoundRegex().IsMatch(path);

    /// <summary>True when the path names a FFXIV sound container.</summary>
    public static bool IsSoundContainer(string path) =>
        path.EndsWith(SoundContainerSuffix, StringComparison.OrdinalIgnoreCase);
}
