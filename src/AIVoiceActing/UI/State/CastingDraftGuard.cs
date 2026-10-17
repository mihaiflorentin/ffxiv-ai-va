namespace AIVoiceActing.UI.State;

using AIVoiceActing.Ports;

/// <summary>What a player can do with the casting presets.</summary>
public enum PresetAction
{
    Select,
    Use,
    Duplicate,
    New,
    Import,
    Export,
    Delete,
}

public enum GuardVerdict
{
    Allow,

    /// <summary>Not now; <see cref="GuardResult.Message"/> says why and what to do instead.</summary>
    Blocked,

    /// <summary>Allowed after the player agrees; <see cref="GuardResult.Message"/> is the question.</summary>
    Confirm,
}

public sealed record GuardResult(GuardVerdict Verdict, string? Message = null, string? ConfirmLabel = null)
{
    public static GuardResult Allowed { get; } = new(GuardVerdict.Allow);

    public bool IsAllowed => this.Verdict == GuardVerdict.Allow;

    public static GuardResult Blocked(string message) => new(GuardVerdict.Blocked, message);
}

/// <summary>The part of the casting draft the guard reads.</summary>
/// <param name="Dirty">The Voices tab holds edits that were not saved.</param>
/// <param name="SelectedName">The casting being edited.</param>
/// <param name="ActiveName">The casting speakers are cast from.</param>
public sealed record CastingGuardState(bool Dirty, string SelectedName, string ActiveName);

/// <summary>
/// Pure rules that keep casting presets from losing work. Switching, duplicating, creating,
/// importing or deleting while the Voices tab has unsaved edits is blocked (those edits would be
/// dropped); deleting always asks; a name that is already taken never overwrites silently (the
/// store's Save replaces by name): Duplicate and New refuse it, Import asks first.
/// </summary>
public static class CastingDraftGuard
{
    public const string UnsavedMessage = "You have unsaved voice changes. Save or discard them on the Voices tab first.";
    public const string AlreadyInUseMessage = "This casting is already in use.";
    public const string BuiltInDeleteMessage = "The built-in casting cannot be deleted.";

    public static GuardResult Check(PresetAction action, CastingGuardState state)
    {
        if (action == PresetAction.Export)
        {
            return GuardResult.Allowed;
        }

        if (state.Dirty)
        {
            return GuardResult.Blocked(UnsavedMessage);
        }

        switch (action)
        {
            case PresetAction.Use:
                return state.SelectedName == state.ActiveName
                    ? GuardResult.Blocked(AlreadyInUseMessage)
                    : GuardResult.Allowed;
            case PresetAction.Delete:
                return DeleteCheck(state);
            default:
                return GuardResult.Allowed;
        }
    }

    /// <summary>The name typed in the Duplicate, New or Import dialog against the castings that exist.</summary>
    public static GuardResult CheckName(
        PresetAction action,
        string typedName,
        IEnumerable<string> existingNames,
        CastingGuardState state)
    {
        if (state.Dirty)
        {
            return GuardResult.Blocked(UnsavedMessage);
        }

        var name = typedName.Trim();
        if (name.Length == 0)
        {
            return GuardResult.Blocked("Please give the casting a name.");
        }

        if (name == CastingPreset.DefaultPresetName)
        {
            return GuardResult.Blocked("\"Default\" is the name of the built-in casting. Pick another name.");
        }

        var taken = existingNames.Any(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase));
        if (!taken)
        {
            return GuardResult.Allowed;
        }

        return action == PresetAction.Import
            ? new GuardResult(
                GuardVerdict.Confirm,
                $"A casting called \"{name}\" already exists. Importing replaces it with the pasted one.",
                "Replace")
            : GuardResult.Blocked($"A casting called \"{name}\" already exists. Pick another name.");
    }

    private static GuardResult DeleteCheck(CastingGuardState state)
    {
        if (state.SelectedName == CastingPreset.DefaultPresetName)
        {
            return GuardResult.Blocked(BuiltInDeleteMessage);
        }

        var inUse = state.SelectedName == state.ActiveName
            ? " It is the casting in use, so speakers without a voice of their own go back to the built-in casting."
            : string.Empty;
        return new GuardResult(
            GuardVerdict.Confirm,
            $"Delete the casting \"{state.SelectedName}\"?{inUse} Characters keep the voices you gave them.",
            "Delete");
    }
}
