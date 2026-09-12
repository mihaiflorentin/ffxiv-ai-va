namespace AIVoiceActing.Domain.Pipeline;

using AIVoiceActing.Domain;

/// <summary>
/// Chat text normalization (pure port of TalkUtils): dash/em-dash normalization and plain
/// world-suffix stripping. (TextToTalk strips worlds from payload-bearing SeStrings; the
/// Dalamud chat adapter ports that payload walk verbatim and uses this pure core for the
/// per-string replace, so both stay behaviorally aligned and testable.)
/// </summary>
public static class ChatTextNormalizer
{
    public static string NormalizePunctuation(string? text) => text?
        .Replace("─", " - ")
        .Replace("—", " - ")
        .Replace("–", "-") ?? "";

    /// <summary>
    /// Removes the "»World" suffix the game appends to player names in chat text.
    /// </summary>
    public static string StripWorldFromText(string text, string world) =>
        string.IsNullOrEmpty(world)
            ? text
            : text.Replace(SpeakerKey.WorldSuffixSeparator + world, "", StringComparison.Ordinal);
}
