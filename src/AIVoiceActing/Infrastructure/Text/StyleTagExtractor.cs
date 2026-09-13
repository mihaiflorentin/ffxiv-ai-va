namespace AIVoiceActing.Infrastructure.Text;

using System.Text.RegularExpressions;

/// <summary>
/// Ad-hoc style tags (TextToTalk's /tttstyles semantics): tags wrapped in the configured
/// delimiter pair — e.g. <c>hello |laughs| world</c> — are stripped from the spoken text
/// and returned as delivery directions for the synthesizer. Disabled or delimiter-less
/// configurations are a strict passthrough.
/// </summary>
public static class StyleTagExtractor
{
    /// <param name="matchRegex">The delimiter-derived match pattern (Configuration.StyleRegex).</param>
    /// <param name="enabled">The AdHocStyleTagsEnabled toggle; false short-circuits.</param>
    /// <returns>The cleaned text and the extracted tags, in order, deduplicated.</returns>
    public static (string CleanText, IReadOnlyList<string> Tags) Extract(
        string text,
        string? matchRegex,
        bool enabled)
    {
        if (!enabled || string.IsNullOrEmpty(matchRegex))
        {
            return (text, []);
        }

        var matches = Regex.Matches(text, matchRegex);
        if (matches.Count == 0)
        {
            return (text, []);
        }

        var tags = new List<string>();
        foreach (var match in matches)
        {
            if (match is Match { Success: true, Groups: [_, { Success: true } tag] }
                && tag.Value.Trim() is { Length: > 0 } trimmed
                && !tags.Contains(trimmed, StringComparer.OrdinalIgnoreCase))
            {
                tags.Add(trimmed);
            }
        }

        var clean = Regex
            .Replace(Regex.Replace(text, matchRegex, " "), "\\s{2,}", " ")
            .Trim();
        return (clean, tags);
    }
}
