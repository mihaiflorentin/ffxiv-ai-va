namespace AIVoiceActing.UI.Pages;

/// <summary>Modifier and main-key choices shared by the global keybind and the per-preset hold-to-talk combos.</summary>
internal static class KeyOptions
{
    public static readonly (int Code, string Name)[] Modifiers =
    [
        (VirtualKeys.Control, "Ctrl"),
        (VirtualKeys.Shift, "Shift"),
        (18, "Alt"),
    ];

    public static readonly (int Code, string Name)[] MajorKeys =
    [
        .. Enumerable.Range(48, 10).Select(i => (i, ((char)i).ToString())),
        .. Enumerable.Range(65, 26).Select(i => (i, ((char)i).ToString())),
    ];
}
