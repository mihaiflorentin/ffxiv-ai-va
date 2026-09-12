namespace AIVoiceActing.Infrastructure.Dalamud;

using global::Dalamud.Game.ClientState.Objects.Types;
using global::Dalamud.Game.ClientState.Objects.SubKinds;
using global::Dalamud.Game.Text.SeStringHandling;
using global::Dalamud.Game.Text.SeStringHandling.Payloads;
using System.Text.RegularExpressions;

/// <summary>
/// SeString helpers ported verbatim from TextToTalk's TalkUtils (the payload-walking half —
/// inherently Dalamud-bound). The plain-text normalization core lives in
/// Domain.Pipeline.ChatTextNormalizer.
/// </summary>
public static partial class SeStringUtils
{
    [GeneratedRegex(@"\p{L}+|\p{M}+|\p{N}+|\s+", RegexOptions.Compiled)]
    private static partial Regex SpeakableRegex();

    private static readonly Regex Speakable = SpeakableRegex();

    /// <summary>
    /// Best-effort speaker-name parse (TalkUtils.TryGetEntityName): the speakable-token
    /// join of the text, overridden by the raw player name when the payload carries one.
    /// </summary>
    public static bool TryGetEntityName(SeString input, out string name)
    {
        name = string.Join("", Speakable.Matches(input.TextValue));
        foreach (var payload in input.Payloads)
        {
            if (payload is PlayerPayload playerPayload)
            {
                name = playerPayload.PlayerName;
                return true;
            }
        }

        return name != string.Empty;
    }

    public static string GetPlayerNameWithoutWorld(SeString playerName)
    {
        if (playerName.Payloads.FirstOrDefault(p => p is PlayerPayload) is PlayerPayload player)
        {
            return player.PlayerName;
        }

        return playerName.TextValue;
    }

    public static string? GetPlayerWorldName(SeString playerName)
    {
        if (playerName.Payloads.FirstOrDefault(p => p is PlayerPayload) is PlayerPayload player)
        {
            return player.World.Value.Name.ToString();
        }

        return null;
    }

    /// <summary>
    /// The sender's home-world row id when the chat payload (or object) carries one — the
    /// ushort half of our "pc:name@world" speaker keys.
    /// </summary>
    public static ushort? GetPlayerWorldRowId(SeString playerName, IGameObject? speakerObject)
    {
        if (playerName.Payloads.FirstOrDefault(p => p is PlayerPayload) is PlayerPayload player)
        {
            return (ushort)player.World.Value.RowId;
        }

        if (speakerObject is IPlayerCharacter pc)
        {
            return (ushort)pc.HomeWorld.RowId;
        }

        return null;
    }

    /// <summary>TalkUtils.StripWorldFromNames, verbatim: remove the world name that the
    /// game appends after every player-name payload in the message body.</summary>
    public static string StripWorldFromNames(SeString message)
    {
        var world = "";
        var cleanString = new SeStringBuilder();
        foreach (var payload in message.Payloads)
        {
            switch (payload)
            {
                case PlayerPayload playerPayload:
                    world = playerPayload.World.Value.Name.ToString();
                    break;
                case TextPayload textPayload when world != "" && textPayload.Text != null &&
                    textPayload.Text.Contains(world):
                    cleanString.AddText(textPayload.Text.Replace(world, ""));
                    break;
                default:
                    cleanString.Add(payload);
                    break;
            }
        }

        return cleanString.Build().TextValue;
    }
}
