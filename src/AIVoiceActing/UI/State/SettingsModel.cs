namespace AIVoiceActing.UI.State;

/// <summary>Anchor keys the Settings page understands (see <see cref="NavModel.OpenAt"/>).</summary>
public static class SettingsAnchors
{
    public const string Quality = "quality";
    public const string Processor = "processor";
    public const string Downloads = "downloads";
    public const string Keybinds = "keybinds";
    public const string Advanced = "advanced";

    /// <summary>Opens Advanced with the casting presets showing (the Voices tab links here).</summary>
    public const string Presets = "casting-presets";

    /// <summary>Opens Advanced with the engines showing. Also the old "engine" link.</summary>
    public const string Engines = "engine";

    /// <summary>Opens Advanced with the test line showing. Also the old "test" link.</summary>
    public const string Test = "test";

    public const string Diagnostics = "diagnostics";

    /// <summary>The old "experimental" link: the delivery details in Advanced.</summary>
    public const string Delivery = "experimental";

    /// <summary>True for every anchor that lives inside the Advanced section.</summary>
    public static bool InsideAdvanced(string? anchor) =>
        anchor is Advanced or Presets or Engines or Test or Diagnostics or Delivery;
}

/// <summary>
/// "Reset to defaults" for the settings on the Settings tab: voice quality, processor, delivery,
/// keybinds and the display switches. Voices, characters, casting presets, what is read and the
/// onboarding state are left alone. Defaults are read from a fresh <see cref="Configuration"/>, so
/// they cannot drift from the real ones.
/// </summary>
public static class SettingsReset
{
    public static void Apply(Configuration config)
    {
        var defaults = new Configuration();
        config.Engine = defaults.Engine;
        config.Device = defaults.Device;
        config.DirectorEnabled = defaults.DirectorEnabled;
        config.LevelVoiceLoudness = defaults.LevelVoiceLoudness;
        config.StaleLineSeconds = defaults.StaleLineSeconds;
        config.KeepTempoWhenPitched = defaults.KeepTempoWhenPitched;
        config.StreamSentences = defaults.StreamSentences;
        config.ShowNowSpeaking = defaults.ShowNowSpeaking;
        config.RemoveStutter = defaults.RemoveStutter;
        config.UseKeybind = defaults.UseKeybind;
        config.ModifierKey = defaults.ModifierKey;
        config.MajorKey = defaults.MajorKey;
        config.ShowAdvanced = defaults.ShowAdvanced;
        config.ReduceMotion = defaults.ReduceMotion;
    }

    public const string ConfirmText =
        "Reset the settings on this tab to their defaults? Voice quality goes back to Fast, the processor " +
        "to Automatic, and the delivery options, keybinds and Remote server address are cleared. " +
        "Your voices, characters, casting presets and what is read stay as they are.";
}

/// <summary>
/// A one-shot request from Settings ("Run setup again") for Home's setup card, which owns the
/// flow. The page that asks and the card that answers share one instance through the UI context.
/// </summary>
public sealed class SetupRerunSignal
{
    private volatile bool requested;

    public void Request() => this.requested = true;

    /// <summary>True once per request.</summary>
    public bool Take()
    {
        var was = this.requested;
        this.requested = false;
        return was;
    }
}
