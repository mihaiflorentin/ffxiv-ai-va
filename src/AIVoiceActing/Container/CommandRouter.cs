namespace AIVoiceActing.Container;

using System.Globalization;

/// <summary>Slash commands understood by <see cref="CommandRouter"/> (our prefixes).</summary>
public enum AivaCommand
{
    Unknown,
    OpenConfig,
    OpenStyles,
    CancelSpeech,
    ToggleTts,
    EnableTts,
    DisableTts,
    SwitchPreset,
    AdjustVolume,
}

/// <summary>One channel preset as the preset command sees it.</summary>
public sealed record PresetSummary(int Id, string? Name);

/// <summary>What a parsed command did and the chat lines to print (TTT prints to chat).</summary>
public sealed record CommandResult(AivaCommand Action, IReadOnlyList<string> Output)
{
    public static readonly CommandResult Silent = new(AivaCommand.Unknown, []);
}
/// <summary>
/// Pure command core (port of TextToTalk's MainCommandModule, renamed to our /aiva*
/// prefixes): parsing, validation and volume/preset math live here with every side effect
/// behind injected delegates, so tests drive it headless. Message tone is TextToTalk's.
/// Config mutation delegates also persist — the plugin wires them to config + Save().
/// </summary>
public sealed class CommandRouter
{
    public const string Usage =
        "Usage: AIVoiceActing commands: /aivaconfig (alias /aiva), /cancelspeech, /toggletts, " +
        "/enabletts, /disabletts, /aivapreset <name>, /aivavolume <0-200|+N|-N>, /aivastyles.";

    private readonly Func<bool> enabled;
    private readonly Action<bool> setEnabled;
    private readonly Action cancelSpeech;
    private readonly Func<int> currentPresetId;
    private readonly Func<IReadOnlyList<PresetSummary>> presets;
    private readonly Action<int> switchPreset;
    private readonly Func<float> volume;
    private readonly Action<float> setVolume;
    private readonly Func<Action?>? openConfig;
    private readonly Func<Action?>? openStyles;

    public CommandRouter(
        Func<bool> enabled,
        Action<bool> setEnabled,
        Action cancelSpeech,
        Func<int> currentPresetId,
        Func<IReadOnlyList<PresetSummary>> presets,
        Action<int> switchPreset,
        Func<float> volume,
        Action<float> setVolume,
        Func<Action?>? openConfig = null,
        Func<Action?>? openStyles = null)
    {
        this.enabled = enabled;
        this.setEnabled = setEnabled;
        this.cancelSpeech = cancelSpeech;
        this.currentPresetId = currentPresetId;
        this.presets = presets;
        this.switchPreset = switchPreset;
        this.volume = volume;
        this.setVolume = setVolume;
        this.openConfig = openConfig;
        this.openStyles = openStyles;
    }

    public CommandResult Execute(string command, string args) => command.Trim().ToLowerInvariant() switch
    {
        "/aivaconfig" or "/aiva" => this.ToggleConfig(),
        "/cancelspeech" => this.CancelTts(),
        "/toggletts" => this.ToggleTts(),
        "/enabletts" => this.EnableTts(),
        "/disabletts" => this.DisableTts(),
        "/aivapreset" => this.SwitchPreset(args),
        "/aivavolume" => this.AdjustVolume(args),
        "/aivastyles" => this.ToggleStyles(),
        _ => new CommandResult(AivaCommand.Unknown, [Usage]),
    };

    /// <summary>Opens the configuration window; logs headlessly when no UI is attached.</summary>
    private CommandResult ToggleConfig()
    {
        var open = this.openConfig?.Invoke();
        if (open is null)
        {
            return new CommandResult(
                AivaCommand.OpenConfig,
                ["The configuration window is not available in this build."]);
        }

        open();
        return CommandResult.Silent with { Action = AivaCommand.OpenConfig };
    }

    private CommandResult ToggleStyles()
    {
        var open = this.openStyles?.Invoke();
        if (open is null)
        {
            return new CommandResult(
                AivaCommand.OpenStyles,
                ["The styles window is not available in this build."]);
        }

        open();
        return CommandResult.Silent with { Action = AivaCommand.OpenStyles };
    }

    /// <summary>TextToTalk's CancelTts is silent: cancel and print nothing.</summary>
    private CommandResult CancelTts()
    {
        this.cancelSpeech();
        return CommandResult.Silent with { Action = AivaCommand.CancelSpeech };
    }

    private CommandResult ToggleTts()
    {
        return this.enabled()
            ? this.DisableTts()
            : this.EnableTts();
    }

    private CommandResult DisableTts()
    {
        this.setEnabled(false);
        this.cancelSpeech();
        return new CommandResult(AivaCommand.DisableTts, ["TTS disabled."]);
    }

    private CommandResult EnableTts()
    {
        this.setEnabled(true);
        return new CommandResult(AivaCommand.EnableTts, ["TTS enabled."]);
    }

    /// <summary>No arguments shows the current preset and the list (TextToTalk parity).</summary>
    private CommandResult SwitchPreset(string args)
    {
        var trimmed = (args ?? string.Empty).Trim();
        if (trimmed.Length == 0)
        {
            var all = this.presets();
            var currentId = this.currentPresetId();
            var current = all.FirstOrDefault(p => p.Id == currentId);
            var lines = new List<string>
            {
                $"Current preset: {current?.Name ?? (all.Count > 0 ? $"#{currentId}" : "(none)")}",
                $"Available presets: {string.Join(", ", all.Select(p => p.Name ?? $"#{p.Id}"))}",
            };
            return new CommandResult(AivaCommand.SwitchPreset, lines);
        }

        var match = this.presets().FirstOrDefault(p =>
            string.Equals(p.Name, trimmed, StringComparison.OrdinalIgnoreCase));
        if (match is null)
        {
            return new CommandResult(AivaCommand.SwitchPreset, [$"No preset named \"{trimmed}\" exists."]);
        }

        this.switchPreset(match.Id);
        return new CommandResult(
            AivaCommand.SwitchPreset,
            [$"AIVoiceActing preset -> {match.Name ?? $"#{match.Id}"}"]);
    }

    /// <summary>
    /// Absolute 0–200 percentage or relative +N/-N, clamped; no arguments shows the current
    /// volume. Port of TextToTalk's AdjustVolume (stored as a linear multiplier ÷100).
    /// </summary>
    private CommandResult AdjustVolume(string args)
    {
        var trimmed = (args ?? string.Empty).Trim();
        if (trimmed.Length == 0)
        {
            return new CommandResult(
                AivaCommand.AdjustVolume,
                [$"Current volume: {this.volume() * 100f:0}%"]);
        }

        var current = this.volume() * 100f;
        float target;
        if (trimmed[0] is '+' or '-')
        {
            if (!float.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out var delta))
            {
                return new CommandResult(
                    AivaCommand.AdjustVolume,
                    [$"Invalid volume adjustment: \"{trimmed}\". Use +N or -N."]);
            }

            target = current + delta;
        }
        else
        {
            if (!float.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
            {
                return new CommandResult(
                    AivaCommand.AdjustVolume,
                    [$"Invalid volume: \"{trimmed}\". Use a percentage (0-200), +N, or -N."]);
            }

            target = value;
        }

        var clamped = Math.Clamp(target, 0f, 200f);
        this.setVolume(clamped / 100f);
        return new CommandResult(AivaCommand.AdjustVolume, [$"Volume -> {clamped:0}%"]);
    }
}
