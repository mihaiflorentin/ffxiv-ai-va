namespace AIVoiceActing.Container;

/// <summary>A preset's keybind as the monitor sees it (ids/values are VirtualKey wire values).</summary>
public sealed record PresetKeybind(int PresetId, string? Name, bool UseKeybind, int ModifierKey, int MajorKey);

/// <summary>What one framework tick decided.</summary>
public sealed record KeybindTickResult(bool TtsToggled, PresetKeybind? PresetSwitched)
{
    public static readonly KeybindTickResult None = new(false, null);
}

/// <summary>
/// Port of TextToTalk's CheckKeybindPressed/CheckTTSToggleKeybind/CheckPresetKeybind: the
/// TTS toggle (Ctrl+N) is checked first, then per-preset keybinds; a single sticky flag is
/// shared across both so one press fires exactly once and only resets when no bound
/// combination is held. Pure — the plugin feeds it IKeyState each framework tick.
/// </summary>
public sealed class KeybindMonitor
{
    private bool keysDown;

    public KeybindTickResult Tick(
        Func<int, bool> isKeyDown,
        bool useTtsToggle,
        int modifierKey,
        int majorKey,
        IReadOnlyList<PresetKeybind> presetKeybinds)
    {
        if (useTtsToggle && isKeyDown(modifierKey) && isKeyDown(majorKey))
        {
            if (this.keysDown)
            {
                return KeybindTickResult.None;
            }

            this.keysDown = true;
            return new KeybindTickResult(TtsToggled: true, PresetSwitched: null);
        }

        foreach (var preset in presetKeybinds)
        {
            if (!preset.UseKeybind)
            {
                continue;
            }

            if (isKeyDown(preset.ModifierKey) && isKeyDown(preset.MajorKey))
            {
                if (this.keysDown)
                {
                    return KeybindTickResult.None;
                }

                this.keysDown = true;
                return new KeybindTickResult(TtsToggled: false, PresetSwitched: preset);
            }
        }

        this.keysDown = false;
        return KeybindTickResult.None;
    }
}
