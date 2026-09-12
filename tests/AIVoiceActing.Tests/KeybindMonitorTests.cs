namespace AIVoiceActing.Tests;

using AIVoiceActing.Container;
using Xunit;

/// <summary>
/// Port of TextToTalk's keybind tick: one sticky flag shared by the TTS toggle (Ctrl+N) and
/// the preset keybinds — a press fires exactly once, held keys do not re-fire, and the flag
/// resets only on a tick where no bound combination is held. Preset tests use Shift+0 so
/// they never collide with the toggle's Ctrl+N.
/// </summary>
public sealed class KeybindMonitorTests
{
    private static readonly PresetKeybind Bound = new(1, "Battle", true, VirtualKeys.Shift, VirtualKeys.Key0);
    private static readonly PresetKeybind Unbound = new(2, "Quiet", false, VirtualKeys.Shift, VirtualKeys.Key0);

    private static KeybindTickResult Tick(
        KeybindMonitor monitor,
        HashSet<int> down,
        bool useTtsToggle = true,
        IReadOnlyList<PresetKeybind>? presets = null) =>
        monitor.Tick(down.Contains, useTtsToggle, VirtualKeys.Control, VirtualKeys.N, presets ?? [Bound, Unbound]);

    private static readonly HashSet<int> ToggleDown = new() { VirtualKeys.Control, VirtualKeys.N };
    private static readonly HashSet<int> PresetDown = new() { VirtualKeys.Shift, VirtualKeys.Key0 };

    [Fact]
    public void TtsToggle_FiresOncePerPress()
    {
        var monitor = new KeybindMonitor();

        Assert.True(Tick(monitor, ToggleDown).TtsToggled);
        Assert.False(Tick(monitor, ToggleDown).TtsToggled); // still held — sticky
        Assert.False(Tick(monitor, ToggleDown).TtsToggled);

        Tick(monitor, []); // released: resets
        Assert.True(Tick(monitor, ToggleDown).TtsToggled); // next press fires again
    }

    [Fact]
    public void TtsToggle_Disabled_DoesNotFire()
    {
        var monitor = new KeybindMonitor();

        Assert.False(Tick(monitor, ToggleDown, useTtsToggle: false).TtsToggled);
        Assert.Equal(KeybindTickResult.None, Tick(monitor, ToggleDown, useTtsToggle: false));
    }

    [Fact]
    public void PresetKeybind_SwitchesPreset_OncePerPress()
    {
        var monitor = new KeybindMonitor();

        var first = Tick(monitor, PresetDown);
        Assert.False(first.TtsToggled);
        Assert.Same(Bound, first.PresetSwitched);

        Assert.Null(Tick(monitor, PresetDown).PresetSwitched); // held
        Tick(monitor, []);
        Assert.Same(Bound, Tick(monitor, PresetDown).PresetSwitched);
    }

    [Fact]
    public void UnboundPreset_IsSkipped()
    {
        var monitor = new KeybindMonitor();
        var result = Tick(monitor, PresetDown, useTtsToggle: false, presets: [Unbound]);

        Assert.Equal(KeybindTickResult.None, result);
    }

    [Fact]
    public void ToggleAndPresetShareTheStickyFlag_ToggleWins()
    {
        var monitor = new KeybindMonitor();
        var down = new HashSet<int> { VirtualKeys.Control, VirtualKeys.N };
        var clashing = new PresetKeybind(3, "Clash", true, VirtualKeys.Control, VirtualKeys.N);

        Assert.True(Tick(monitor, down, presets: [clashing]).TtsToggled);
        Assert.Null(Tick(monitor, down, presets: [clashing]).PresetSwitched); // same press must not also switch
    }

    [Fact]
    public void NoKeysDown_IsANoopTick()
    {
        var monitor = new KeybindMonitor();
        var result = Tick(monitor, []);

        Assert.False(result.TtsToggled);
        Assert.Null(result.PresetSwitched);
    }
}
