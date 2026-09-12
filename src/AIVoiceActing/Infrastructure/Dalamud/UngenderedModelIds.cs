namespace AIVoiceActing.Infrastructure.Dalamud;

using System.Reflection;

/// <summary>
/// Ungendered model-id overrides, ported from TextToTalk's UngenderedOverrideManager: the
/// embedded <c>overridenModelIds.txt</c> lists "<code>id ; comment</code>" lines; comments
/// (after ";" or leading "#") and blank lines are skipped. Canonically ungendered actors keep
/// their sex byte at 0 (male), so this list is what forces the Ungendered voice group (see
/// Domain.VoiceGroupResolver).
/// </summary>
public static class UngenderedModelIds
{
    private const string ResourceName = "AIVoiceActing.Infrastructure.Dalamud.overridenModelIds.txt";

    /// <summary>Parses the file embedded in the plugin assembly (TTT's manifest-resource pattern).</summary>
    public static IReadOnlySet<int> Load()
    {
        var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName)
            ?? throw new FileNotFoundException("Failed to load ungendered model overrides file!");
        using var reader = new StreamReader(stream);
        return Parse(reader.ReadToEnd());
    }

    /// <summary>Parse core (pure, test-executable): mirrors TTT's ParseOverridesFile, plus "#" line skipping.</summary>
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
}
