namespace AIVoiceActing.UI.State;

public enum ListEmptyKind
{
    /// <summary>The list has rows.</summary>
    None,

    /// <summary>There is nothing to list at all.</summary>
    Nothing,

    /// <summary>Rows exist but the search or filters hide every one of them.</summary>
    NoMatches,
}

/// <summary>
/// What an empty list says: one sentence about why, plus the action that fixes it. The draw
/// layer turns <see cref="ActionLabel"/> into the primary button of an empty state.
/// </summary>
public sealed record EmptyDescriptor(ListEmptyKind Kind, string Title, string Hint, string? ActionLabel);
