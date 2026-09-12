namespace AIVoiceActing.Domain;

/// <summary>
/// Canonical speaker keys, stable across sessions (they feed the deterministic xxHash32 voice
/// assignment, so the format must never change): players are
/// <c>pc:{First} {Last}@{world}</c> with display title-case preserved, NPCs are
/// <c>npc:{full name lowercase}</c>. A trailing world/fold suffix ("…»Jenesis") is stripped
/// before key formation.
/// </summary>
public static class SpeakerKey
{
    /// <summary>Separator the game uses to append a world/fold name to a displayed name.</summary>
    public const char WorldSuffixSeparator = '»';

    public static string ForPlayer(string fullName, ushort world)
    {
        var name = StripSuffix(fullName);
        return $"pc:{name}@{world}";
    }

    public static string ForNpc(string fullName)
    {
        var name = StripSuffix(fullName);
        return $"npc:{name.ToLowerInvariant()}";
    }

    private static string StripSuffix(string? fullName)
    {
        var name = fullName?.Trim() ?? string.Empty;
        var separator = name.IndexOf(WorldSuffixSeparator);
        if (separator >= 0)
        {
            name = name[..separator].Trim();
        }

        return name;
    }
}
