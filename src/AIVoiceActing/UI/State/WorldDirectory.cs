namespace AIVoiceActing.UI.State;

/// <summary>
/// Public worlds by id and name (from the game data), for the world picker and world-name
/// columns. The source is read lazily once; a failing or absent source yields no names and
/// raw ids are shown instead.
/// </summary>
public sealed class WorldDirectory
{
    private readonly Func<IReadOnlyList<(uint Id, string Name)>>? source;
    private Dictionary<uint, string>? names;
    private IReadOnlyList<(uint Id, string Name)>? sorted;

    public WorldDirectory(Func<IReadOnlyList<(uint Id, string Name)>>? source = null)
    {
        this.source = source;
    }

    /// <summary>World name, the raw id when unknown, or a dash for no world.</summary>
    public string Label(uint? world)
    {
        if (world is not { } id)
        {
            return "—";
        }

        var known = this.Names();
        return known.TryGetValue(id, out var name) ? name : id.ToString();
    }

    /// <summary>All worlds sorted by name; empty when no names are available.</summary>
    public IReadOnlyList<(uint Id, string Name)> Sorted()
    {
        return this.sorted ??= [.. this.Names()
            .OrderBy(w => w.Value, StringComparer.OrdinalIgnoreCase)
            .Select(w => (w.Key, w.Value))];
    }

    private Dictionary<uint, string> Names()
    {
        if (this.names is not null)
        {
            return this.names;
        }

        try
        {
            this.names = this.source is null ? [] : this.source().ToDictionary(w => w.Id, w => w.Name);
        }
        catch (Exception)
        {
            this.names = [];
        }

        return this.names;
    }
}
