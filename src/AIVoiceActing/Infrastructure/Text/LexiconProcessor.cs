namespace AIVoiceActing.Infrastructure.Text;

using System.Text.RegularExpressions;
using AIVoiceActing.Ports;

/// <summary>
/// Minimal PLS-style lexicon: whole-word, case-insensitive word → replacement
/// substitution applied before emotion planning and synthesis. Entries apply in
/// insertion order; each entry sees the output of the previous ones.
/// </summary>
public sealed class LexiconProcessor : ILexicon
{
    private static readonly IReadOnlyDictionary<string, string> NoEntries =
        new Dictionary<string, string>();

    private readonly List<(Regex Pattern, string Replacement)> rules = [];

    public LexiconProcessor(IReadOnlyDictionary<string, string>? entries)
    {
        foreach (var (word, replacement) in entries ?? NoEntries)
        {
            if (string.IsNullOrWhiteSpace(word))
            {
                continue;
            }

            this.rules.Add((
                new Regex($@"\b{Regex.Escape(word.Trim())}\b", RegexOptions.Compiled | RegexOptions.IgnoreCase),
                replacement));
        }
    }

    /// <summary>A processor with no entries; <see cref="Apply"/> is a passthrough.</summary>
    public static LexiconProcessor Empty { get; } = new(NoEntries);

    public string Apply(string text)
    {
        var result = text;
        foreach (var (pattern, replacement) in this.rules)
        {
            result = pattern.Replace(result, replacement);
        }

        return result;
    }
}
