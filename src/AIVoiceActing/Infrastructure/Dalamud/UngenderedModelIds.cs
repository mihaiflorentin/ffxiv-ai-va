namespace AIVoiceActing.Infrastructure.Dalamud;

using System.Reflection;

/// <summary>
/// Model-id voice overrides, ported from TextToTalk's UngenderedOverrideManager: the
/// embedded <c>overridenModelIds.txt</c> lists "<code>id ; comment</code>" lines (which
/// force the Ungendered voice group) and "<code>id ; Name ; setKey</code>" lines (which
/// additionally bind that model id to a named RaceVoiceMap set — the beast-tribe /
/// allied-society voice mechanism). Comments (after ";" or leading "#") and blank lines
/// are skipped. Canonically ungendered actors keep their sex byte at 0 (male), so the id
/// list is what forces the Ungendered voice group (see Domain.VoiceGroupResolver).
/// </summary>
public static class UngenderedModelIds
{
    private const string ResourceName = "AIVoiceActing.Infrastructure.Dalamud.overridenModelIds.txt";

    /// <summary>Parses the file embedded in the plugin assembly (TTT's manifest-resource pattern).</summary>
    public static IReadOnlySet<int> Load()
    {
        var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName);
        if (stream is null)
        {
            // Test hosts compile the sources without the embedded resource: no overrides.
            return new HashSet<int>();
        }

        using var reader = new StreamReader(stream);
        return Parse(reader.ReadToEnd());
    }

    /// <summary>Model id → named voice set, for beast-tribe/allied-society casting.</summary>
    public static IReadOnlyDictionary<int, string> LoadVoiceMap()
    {
        var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName);
        if (stream is null)
        {
            // Test hosts compile the sources without the embedded resource: no overrides.
            return new Dictionary<int, string>();
        }

        using var reader = new StreamReader(stream);
        return ParseVoiceMap(reader.ReadToEnd());
    }

    /// <summary>
    /// Parse core (pure, test-executable): "<code>id ; comment</code>" → Ungendered ids,
    /// mirroring TTT's ParseOverridesFile, plus "#" line skipping.
    /// </summary>
    public static IReadOnlySet<int> Parse(string fileData)
    {
        var ids = new HashSet<int>();
        foreach (var rawLine in fileData.Split('\r', '\n'))
        {
            if (string.IsNullOrWhiteSpace(rawLine))
            {
                continue;
            }

            var line = rawLine.TrimStart();
            if (line.StartsWith('#'))
            {
                continue;
            }

            line = line.Split(';')[0].Trim(); // Remove comments
            if (line.Length == 0)
            {
                continue;
            }

            try
            {
                ids.Add(int.Parse(line));
            }
            catch (Exception e)
            {
                throw new AggregateException($"Failed to parse model ID \"{line}\"!", e);
            }
        }

        return ids;
    }

    /// <summary>
    /// Parses the full override table (pure, test-executable): "<code>id ; Name</code>"
    /// forces the Ungendered group; "<code>id ; Name ; setKey</code>" additionally binds
    /// that model id to a named RaceVoiceMap set.
    /// </summary>
    public static IReadOnlyDictionary<int, string> ParseVoiceMap(string fileData)
    {
        var map = new Dictionary<int, string>();
        foreach (var rawLine in fileData.Split('\r', '\n'))
        {
            var line = rawLine.TrimStart();
            if (string.IsNullOrWhiteSpace(line) || line.StartsWith('#'))
            {
                continue;
            }

            var parts = line.Split(';');
            var idText = parts[0].Trim();
            if (idText.Length == 0 || parts.Length < 3 || parts[2].Trim().Length == 0)
            {
                continue;
            }

            try
            {
                map[int.Parse(idText)] = parts[2].Trim();
            }
            catch (Exception e)
            {
                throw new AggregateException($"Failed to parse model ID \"{idText}\"!", e);
            }
        }

        return map;
    }
}
