namespace AIVoiceActing.UI.State;

using AIVoiceActing.Ports;

/// <summary>One entry of the precision picker.</summary>
public sealed record PrecisionChoice(string Value, string Label);

/// <summary>
/// Pure helpers behind the precision picker under Advanced: which choices an engine offers (only
/// when it ships more than one), how each reads with its download size, which download group holds
/// its files, and how the choice is stored in <c>Engine.PerEngine</c> (an absent entry means
/// "automatic", so choosing automatic removes it).
/// </summary>
public static class EnginePrecisionModel
{
    public const string Auto = "auto";

    private static readonly string[] Order = ["fp32", "fp16", "q4f16"];

    public static IReadOnlyList<PrecisionChoice> Choices(EngineInfo engine)
    {
        var sizes = engine.ApproxBytesByPrecision;
        if (sizes.Count < 2)
        {
            return [];
        }

        var choices = new List<PrecisionChoice> { new(Auto, "Automatic (recommended)") };
        foreach (var precision in Order.Where(sizes.ContainsKey))
        {
            choices.Add(new PrecisionChoice(precision, Label(precision, sizes[precision])));
        }

        return choices;
    }

    public static string Label(string precision, long bytes) => precision switch
    {
        "fp32" => $"Full size, {Labels.FileSize(bytes)} (fp32)",
        "fp16" => $"Lighter, {Labels.FileSize(bytes)} (fp16)",
        "q4f16" => $"Lightest, {Labels.FileSize(bytes)} (q4f16)",
        _ => $"{precision}, {Labels.FileSize(bytes)}",
    };

    /// <summary>The download group that holds the files of <paramref name="precision"/>; null for automatic or unknown.</summary>
    public static string? GroupFor(EngineInfo engine, string precision)
    {
        if (precision == Auto)
        {
            return null;
        }

        var groups = engine.AssetGroups.Where(g => g != DownloadsModel.EmotionGroup).ToList();
        if (precision == Order[0])
        {
            return groups.FirstOrDefault();
        }

        return groups.FirstOrDefault(g => g.EndsWith("-" + precision, StringComparison.OrdinalIgnoreCase));
    }

    public static string Current(EngineOptionsMap map, string engineKey) =>
        map.TryGetValue(engineKey, out var options) && !string.IsNullOrWhiteSpace(options.Precision)
            ? options.Precision
            : Auto;

    /// <summary>Stores the choice; an entry left with nothing but defaults is dropped so the config stays minimal.</summary>
    public static void Set(EngineOptionsMap map, string engineKey, string precision)
    {
        if (!map.TryGetValue(engineKey, out var options))
        {
            if (precision == Auto)
            {
                return;
            }

            options = map[engineKey] = new EngineOptions();
        }

        options.Precision = precision;
        if (precision == Auto && options.Device == "inherit" && options.Extra.Count == 0)
        {
            map.Remove(engineKey);
        }
    }
}
