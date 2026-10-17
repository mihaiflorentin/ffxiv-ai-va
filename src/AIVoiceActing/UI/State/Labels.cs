namespace AIVoiceActing.UI.State;

using System.Globalization;

/// <summary>
/// Plain-language copy. Technical terms map to what a player controls; the jargon list must
/// not appear in any UI string outside the Advanced section.
/// </summary>
public static class Labels
{
    public const string ProcessorLabel = "Processor";
    public const string Expressiveness = "Expressiveness";
    public const string AnyGender = "Any gender";
    public const string Duplicate = "Duplicate";
    public const string CreatureModels = "Creature models";

    private static readonly Dictionary<string, string> Map = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Execution provider"] = ProcessorLabel,
        ["Exaggeration"] = Expressiveness,
        ["Bias"] = Expressiveness,
        ["Pool (unknown gender)"] = AnyGender,
        ["Fork"] = Duplicate,
        ["Model-id bindings"] = CreatureModels,
    };

    private static readonly string[] Jargon =
    {
        "execution provider", "onnx", "model id", "model-id", "bias", "exaggeration", "pool", "rtf", "directml", "regex",
    };

    /// <summary>Plain wording for a technical label; text with no mapping comes back unchanged.</summary>
    public static string Plain(string label) => Map.TryGetValue(label, out var plain) ? plain : label;

    /// <summary>True when the text contains a word reserved for Advanced (whole-word match).</summary>
    public static bool ContainsJargon(string text)
    {
        foreach (var term in Jargon)
        {
            var from = 0;
            while ((from = text.IndexOf(term, from, StringComparison.OrdinalIgnoreCase)) >= 0)
            {
                var before = from == 0 || !char.IsLetterOrDigit(text[from - 1]);
                var end = from + term.Length;
                var after = end >= text.Length || !char.IsLetterOrDigit(text[end]);
                if (before && after)
                {
                    return true;
                }

                from = end;
            }
        }

        return false;
    }

    /// <summary>Friendly name for an execution-provider setting value.</summary>
    public static string Processor(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw) || raw.Equals("auto", StringComparison.OrdinalIgnoreCase))
        {
            return "Automatic";
        }

        if (raw.Equals("cpu", StringComparison.OrdinalIgnoreCase))
        {
            return "CPU";
        }

        return "Graphics card";
    }

    /// <summary>"Light (2 threads)" style description of the background-load setting.</summary>
    public static string BackgroundLoad(int threads)
    {
        var name = threads <= 2 ? "Light" : threads <= 4 ? "Balanced" : "Fast";
        return $"{name} ({threads} threads)";
    }

    public static string FileSize(long bytes)
    {
        const double Kb = 1024;
        const double Mb = Kb * 1024;
        const double Gb = Mb * 1024;
        var inv = CultureInfo.InvariantCulture;
        return bytes switch
        {
            >= (long)Gb => (bytes / Gb).ToString("0.0", inv) + " GB",
            >= (long)Mb => (bytes / Mb).ToString("0.0", inv) + " MB",
            >= (long)Kb => (bytes / Kb).ToString("0.0", inv) + " KB",
            _ => bytes.ToString(inv) + " B",
        };
    }
}
