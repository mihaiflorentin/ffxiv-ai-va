namespace AIVoiceActing.Infrastructure.Text;

using System.Text.RegularExpressions;

/// <summary>
/// Ports TextToTalk's <c>TalkUtils.RemoveStutters</c>: NPC dialogue often stutters
/// ("H-hello, nice to m-meet you..."), which no TTS reads naturally. Removes repeated
/// hyphenated letter prefixes (1-2 letters, case-insensitive, chained repetitions
/// collapse in repeated passes) and restores sentence capitalization when the removal
/// demoted the first character.
/// </summary>
public static class StutterRemover
{
    private static readonly Regex Stutter = new(
        @"(?<=\s|^)(\p{L}{1,2})-(?=\1)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static string Remove(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var startsCapitalized = char.IsUpper(text, 0);
        while (Stutter.IsMatch(text))
        {
            text = Stutter.Replace(text, string.Empty);
        }

        if (startsCapitalized && text.Length > 0 && !char.IsUpper(text, 0))
        {
            text = char.ToUpper(text[0]) + text[1..];
        }

        return text;
    }
}
