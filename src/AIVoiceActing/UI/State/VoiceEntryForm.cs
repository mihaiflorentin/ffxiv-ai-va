namespace AIVoiceActing.UI.State;

using AIVoiceActing.Domain;

/// <summary>
/// Pure add-form state for the Player/NPC voice tables (TTT parity: name + world inputs,
/// duplicate check, deferred error line). The ImGui window holds the text state; this type
/// validates and builds the canonical override key. Dalamud- and ImGui-free.
/// </summary>
public sealed class VoiceEntryForm
{
    private readonly bool player;

    /// <param name="player">True for the Player tab (world required); false for NPC.</param>
    public VoiceEntryForm(bool player) => this.player = player;

    public string Name { get; set; } = string.Empty;
    public string World { get; set; } = string.Empty;

    /// <summary>Clears both inputs after a successful submit.</summary>
    public void Reset()
    {
        this.Name = string.Empty;
        this.World = string.Empty;
    }

    /// <summary>Validation error for the current inputs, or null when submittable.</summary>
    public string? Validate(IReadOnlySet<string> existingKeys) =>
        this.TryBuildKey(out var key, out var error)
            ? existingKeys.Contains(key)
                ? "A voice is already assigned to this speaker."
                : null
            : error ?? "Invalid entry.";

    /// <summary>Canonical override key when valid, else null.</summary>
    public string? BuildKey() => this.TryBuildKey(out var key, out _) ? key : null;

    public bool TryBuildKey(out string key, out string? error)
    {
        var name = this.Name.Trim();
        if (name.Length == 0)
        {
            key = string.Empty;
            error = "Name is required.";
            return false;
        }

        if (name.StartsWith("pc:", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("npc:", StringComparison.OrdinalIgnoreCase))
        {
            key = string.Empty;
            error = "Name must not include the pc:/npc: prefix.";
            return false;
        }

        if (name.Contains(SpeakerKey.WorldSuffixSeparator))
        {
            key = string.Empty;
            error = $"Name must not contain the world-suffix character \"{SpeakerKey.WorldSuffixSeparator}\".";
            return false;
        }

        if (!this.player)
        {
            key = SpeakerKey.ForNpc(name);
            error = null;
            return true;
        }

        if (!ushort.TryParse(this.World.Trim(), out var world))
        {
            key = string.Empty;
            error = "World must be a world id (0-65535).";
            return false;
        }

        key = SpeakerKey.ForPlayer(name, world);
        error = null;
        return true;
    }
}
