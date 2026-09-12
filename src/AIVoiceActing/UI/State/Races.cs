namespace AIVoiceActing.UI.State;

/// <summary>
/// Pure playable race/tribe tables for the Test tab's speaker picker (game race/tribe ids
/// — the same customize-data bytes the speaker directory resolves). Sex follows the game's
/// customize byte: 0 = male, 1 = female (see VoiceGroupResolver).
/// </summary>
public static class Races
{
    public sealed record Tribe(byte Id, string Name);

    public sealed record Race(byte Id, string Name, IReadOnlyList<Tribe> Tribes);

    public static readonly IReadOnlyList<Race> All =
    [
        new(1, "Hyur",
        [
            new(1, "Midlander"),
            new(2, "Highlander"),
        ]),
        new(2, "Elezen",
        [
            new(1, "Wildwood"),
            new(2, "Duskwight"),
        ]),
        new(3, "Lalafell",
        [
            new(1, "Plainsfolk"),
            new(2, "Dunesfolk"),
        ]),
        new(4, "Miqo'te",
        [
            new(1, "Seeker of the Sun"),
            new(2, "Keeper of the Moon"),
        ]),
        new(5, "Roegadyn",
        [
            new(1, "Sea Wolves"),
            new(2, "Hellsguard"),
        ]),
        new(6, "Au Ra",
        [
            new(1, "Raen"),
            new(2, "Xaela"),
        ]),
        new(7, "Hrothgar",
        [
            new(1, "The Lost"),
            new(2, "The Helions"),
        ]),
        new(8, "Viera",
        [
            new(1, "Rava"),
            new(2, "Veena"),
        ]),
    ];

    public static Race Default => All[0];

    /// <summary>First tribe of <paramref name="race"/>, for when the race combo moves.</summary>
    public static Tribe DefaultTribe(Race race) => race.Tribes[0];

    public static string SexName(byte sex) => sex switch
    {
        0 => "Male",
        1 => "Female",
        _ => "Unknown",
    };
}
