namespace AIVoiceActing.UI.State;

using AIVoiceActing.Domain;
using AIVoiceActing.Ports;

public enum VoiceFilterGroup
{
    Accent,
    Sex,
    Age,
}

/// <summary>One chip or checkbox of a filter group, with how many voices carry the value.</summary>
public sealed record FilterOption(string Value, string Label, int Count);

/// <summary>
/// One row of the voice list: a real voice, or a placeholder (<see cref="Voice"/> null) for an id
/// that a casting or character still uses but no enabled pack provides.
/// </summary>
public sealed record VoiceListRow(string Id, VoiceInfo? Voice)
{
    public bool Missing => this.Voice is null;
}

/// <summary>The list for one set of inputs, ready to draw. Rows are sorted by name; missing-voice rows come first.</summary>
public sealed record VoiceListResult(
    IReadOnlyList<VoiceListRow> Rows,
    int Shown,
    int Total,
    int MissingCount,
    bool Filtered,
    EmptyDescriptor? Empty,
    IReadOnlyList<FilterOption> Accents,
    IReadOnlyList<FilterOption> Sexes,
    IReadOnlyList<FilterOption> Ages)
{
    /// <summary>"24 of 54 shown".</summary>
    public string CountText => $"{this.Shown} of {this.Total} shown";

    public IReadOnlyList<FilterOption> Options(VoiceFilterGroup group) => group switch
    {
        VoiceFilterGroup.Accent => this.Accents,
        VoiceFilterGroup.Sex => this.Sexes,
        _ => this.Ages,
    };
}

/// <summary>
/// Search and filter state of one voice list (the library, or a picker), plus the pure function
/// that turns the library's voices into the rows to draw. Search is a case-insensitive
/// all-words match over display name, accent (label and code), description and id; the accent,
/// sex and age chips OR within a group and AND across groups. Filters are view state only and
/// never touch a stored selection. The result is cached by its inputs (the voice snapshot, the
/// filter revision and the referenced ids), so a frame that changed nothing does no work.
/// </summary>
public sealed class VoiceLibraryModel
{
    private static readonly string[] SexOrder = ["female", "male", "neutral"];
    private static readonly string[] AgeOrder = ["child", "young", "adult", "senior"];

    private readonly HashSet<string>[] selected =
    [
        new(StringComparer.OrdinalIgnoreCase),
        new(StringComparer.OrdinalIgnoreCase),
        new(StringComparer.OrdinalIgnoreCase),
    ];

    private string search = string.Empty;
    private int revision;

    private IReadOnlyList<VoiceInfo>? cachedVoices;
    private int cachedRevision = -1;
    private string[] cachedReferenced = [];
    private VoiceListResult? cached;

    /// <summary>How many times the result was actually rebuilt (tests assert the cache holds).</summary>
    public int ComputeCount { get; private set; }

    public string Search
    {
        get => this.search;
        set
        {
            var next = value ?? string.Empty;
            if (next != this.search)
            {
                this.search = next;
                this.revision++;
            }
        }
    }

    /// <summary>True when the search box has words or any chip is on.</summary>
    public bool HasFilters => this.search.Trim().Length > 0 || this.selected.Any(set => set.Count > 0);

    public int ActiveCount(VoiceFilterGroup group) => this.selected[(int)group].Count;

    public bool IsOn(VoiceFilterGroup group, string value) => this.selected[(int)group].Contains(value);

    public void Toggle(VoiceFilterGroup group, string value)
    {
        var set = this.selected[(int)group];
        if (!set.Remove(value))
        {
            set.Add(value);
        }

        this.revision++;
    }

    /// <summary>Clears the search and every chip.</summary>
    public void Clear()
    {
        this.search = string.Empty;
        foreach (var set in this.selected)
        {
            set.Clear();
        }

        this.revision++;
    }

    /// <summary>
    /// The list for the current filters. <paramref name="voices"/> is the library snapshot (a new
    /// list instance after every reload); <paramref name="referencedIds"/> are the voice ids
    /// casting and characters use, sorted and distinct, so unresolved ones can show as missing rows.
    /// </summary>
    public VoiceListResult Compute(IReadOnlyList<VoiceInfo> voices, IReadOnlyList<string>? referencedIds = null)
    {
        var referenced = referencedIds as string[] ?? [.. referencedIds ?? []];
        if (this.cached is not null
            && ReferenceEquals(voices, this.cachedVoices)
            && this.revision == this.cachedRevision
            && referenced.AsSpan().SequenceEqual(this.cachedReferenced))
        {
            return this.cached;
        }

        this.ComputeCount++;
        this.cachedVoices = voices;
        this.cachedRevision = this.revision;
        this.cachedReferenced = referenced;
        return this.cached = this.Build(voices, referenced);
    }

    private VoiceListResult Build(IReadOnlyList<VoiceInfo> voices, string[] referenced)
    {
        var words = this.search.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var chipsOn = this.selected.Any(set => set.Count > 0);

        var rows = new List<VoiceListRow>();
        var known = new HashSet<string>(voices.Select(v => v.Id), StringComparer.Ordinal);
        if (!chipsOn)
        {
            foreach (var id in referenced.Where(id => !known.Contains(id) && MatchesId(id, words)))
            {
                rows.Add(new VoiceListRow(id, null));
            }
        }

        var missing = rows.Count;
        var matched = voices
            .Where(v => this.PassesChips(v) && MatchesVoice(v, words))
            .OrderBy(v => v.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(v => v.Id, StringComparer.Ordinal);
        rows.AddRange(matched.Select(v => new VoiceListRow(v.Id, v)));

        var shown = rows.Count - missing;
        var filtered = this.HasFilters;
        return new VoiceListResult(
            rows,
            shown,
            voices.Count,
            missing,
            filtered,
            DescribeEmpty(rows.Count, voices.Count, filtered, this.search.Trim()),
            Options(voices, v => v.Accent, AccentOrder, Accents.LabelFor),
            Options(voices, v => v.Sex, id => Array.IndexOf(SexOrder, id), Labels.Sex),
            Options(voices, v => v.AgeBand, id => Array.IndexOf(AgeOrder, id), Labels.Age));
    }

    private bool PassesChips(VoiceInfo voice) =>
        Passes(this.selected[(int)VoiceFilterGroup.Accent], voice.Accent)
        && Passes(this.selected[(int)VoiceFilterGroup.Sex], voice.Sex)
        && Passes(this.selected[(int)VoiceFilterGroup.Age], voice.AgeBand);

    private static bool Passes(HashSet<string> chosen, string value) => chosen.Count == 0 || chosen.Contains(value);

    private static bool MatchesId(string id, string[] words) =>
        words.All(word => id.Contains(word, StringComparison.OrdinalIgnoreCase));

    private static bool MatchesVoice(VoiceInfo voice, string[] words)
    {
        if (words.Length == 0)
        {
            return true;
        }

        var accent = Accents.LabelFor(voice.Accent);
        return words.All(word =>
            voice.DisplayName.Contains(word, StringComparison.OrdinalIgnoreCase)
            || accent.Contains(word, StringComparison.OrdinalIgnoreCase)
            || Accents.MatchesSearch(voice.Accent, word)
            || voice.Description.Contains(word, StringComparison.OrdinalIgnoreCase)
            || voice.Id.Contains(word, StringComparison.OrdinalIgnoreCase));
    }

    private static EmptyDescriptor? DescribeEmpty(int rows, int total, bool filtered, string search)
    {
        if (rows > 0)
        {
            return null;
        }

        if (total == 0)
        {
            return new EmptyDescriptor(
                ListEmptyKind.Nothing,
                "No voices installed yet.",
                "Voices come with the plugin and with voice packs. Drop a pack into the packs folder, then rescan.",
                "Open packs folder");
        }

        return new EmptyDescriptor(
            ListEmptyKind.NoMatches,
            search.Length > 0 ? $"No voices match \"{search}\"." : "No voices match these filters.",
            filtered ? "Try fewer words, or clear the filters to see every voice." : string.Empty,
            "Clear filters");
    }

    private static int AccentOrder(string code)
    {
        for (var i = 0; i < Accents.All.Count; i++)
        {
            if (Accents.All[i].Code.Equals(code, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return int.MaxValue;
    }

    private static IReadOnlyList<FilterOption> Options(
        IReadOnlyList<VoiceInfo> voices,
        Func<VoiceInfo, string> pick,
        Func<string, int> order,
        Func<string, string> label)
    {
        return
        [
            .. voices
                .Select(pick)
                .Where(value => !string.IsNullOrEmpty(value))
                .GroupBy(value => value, StringComparer.OrdinalIgnoreCase)
                .Select(group => new FilterOption(group.Key, label(group.Key), group.Count()))
                .OrderBy(option => OrderKey(order, option.Value))
                .ThenBy(option => option.Label, StringComparer.OrdinalIgnoreCase),
        ];
    }

    private static int OrderKey(Func<string, int> order, string value)
    {
        var index = order(value.ToLowerInvariant());
        return index < 0 ? int.MaxValue : index;
    }
}
