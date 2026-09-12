namespace AIVoiceActing.UI.State;

using AIVoiceActing.Domain;

/// <summary>
/// Read view over one persisted speaker key (UI table rows): splits
/// <c>pc:{First} {Last}@{world}</c> / <c>npc:{full name lowercase}</c> back into display
/// columns. Parsing never throws — an unrecognized key still yields a row.
/// </summary>
public sealed record SpeakerKeyView(
    string Key,
    string Name,
    ushort? World,
    bool IsPlayer)
{
    public static SpeakerKeyView Parse(string key)
    {
        if (key.StartsWith("pc:", StringComparison.Ordinal))
        {
            var body = key["pc:".Length..];
            var at = body.LastIndexOf('@');
            if (at >= 0
                && ushort.TryParse(body[(at + 1)..], out var world))
            {
                return new SpeakerKeyView(key, body[..at], world, IsPlayer: true);
            }

            return new SpeakerKeyView(key, body, null, IsPlayer: true);
        }

        if (key.StartsWith("npc:", StringComparison.Ordinal))
        {
            return new SpeakerKeyView(key, key["npc:".Length..], null, IsPlayer: false);
        }

        return new SpeakerKeyView(key, key, null, IsPlayer: false);
    }

    /// <summary>Builds the canonical key for a manual override (delegates to
    /// <see cref="SpeakerKey"/> so the deterministic-hash format stays canonical).</summary>
    public static string ForPlayer(string name, ushort world) => SpeakerKey.ForPlayer(name, world);

    public static string ForNpc(string name) => SpeakerKey.ForNpc(name);
}
