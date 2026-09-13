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

    private volatile List<(Regex Pattern, string Replacement)> rules = [];

    private readonly Func<IReadOnlyDictionary<string, string>>? entriesFactory;

    private IReadOnlyDictionary<string, string> lastEntries;

    /// <summary>Snapshot form: rules compile once and never reload.</summary>
    public LexiconProcessor(IReadOnlyDictionary<string, string>? entries)
        : this(() => entries ?? NoEntries)
    {
        this.lastEntries = entries ?? NoEntries;
    }

    /// <summary>
    /// Reload-form: the factory runs on every <see cref="Apply"/>; a new dictionary
    /// instance (the file loader returns a stable instance until a file changes)
    /// triggers a rule rebuild. Word lists are tiny, so the reload is a dictionary walk.
    /// </summary>
    public LexiconProcessor(Func<IReadOnlyDictionary<string, string>> entriesFactory)
    {
        this.entriesFactory = entriesFactory;
        this.lastEntries = entriesFactory();
        this.RebuildRules(this.lastEntries);
    }

    /// <summary>A processor with no entries; <see cref="Apply"/> is a passthrough.</summary>
    public static LexiconProcessor Empty { get; } = new(NoEntries);

    public string Apply(string text)
    {
        if (this.entriesFactory is { } factory)
        {
            var entries = factory();
            if (!ReferenceEquals(entries, this.lastEntries))
            {
                this.lastEntries = entries;
                this.RebuildRules(entries);
            }
        }

        var result = text;
        foreach (var (pattern, replacement) in this.rules)
        {
            result = pattern.Replace(result, replacement);
        }

        return result;
    }

    private void RebuildRules(IReadOnlyDictionary<string, string> entries)
    {
        var rules = new List<(Regex Pattern, string Replacement)>();
        foreach (var (word, replacement) in entries)
        {
            if (string.IsNullOrWhiteSpace(word))
            {
                continue;
            }

            rules.Add((
                new Regex($@"\b{Regex.Escape(word.Trim())}\b", RegexOptions.Compiled | RegexOptions.IgnoreCase),
                replacement));
        }

        this.rules = rules;
    }
}
