namespace AIVoiceActing.Domain.Pipeline;

using AIVoiceActing.Ports;

/// <summary>Where a captured line came from (TextToTalk's TextSource equivalents).</summary>
public enum TextSource
{
    None,
    Talk,
    BattleTalk,
    Chat,
    CutsceneSubtitle,
}

/// <summary>
/// One candidate speech line flowing through the pipeline (pure port of TextToTalk's
/// TextEmitEvent). <see cref="ChatType"/> is the numeric XivChatType value (or an
/// <c>AdditionalChatType</c> extra) kept as an int so Domain stays Dalamud-free; the
/// chat adapter converts. <see cref="Ports.SpeakerHint"/> carries everything the speaker
/// directory needs to resolve an identity.
/// </summary>
/// <param name="Source">Capturing source.</param>
/// <param name="SpeakerName">Clean speaker name used for semantic dedupe (may be empty).</param>
/// <param name="Text">The expanded text to speak (may include the configured "says" prefix).</param>
/// <param name="RawText">The text before normalization / name composition.</param>
/// <param name="Hint">Capture-time speaker facts for identity resolution.</param>
/// <param name="ChatType">Numeric channel id; 0 for non-chat sources.</param>
public sealed record TextEmitEvent(
    TextSource Source,
    string SpeakerName,
    string Text,
    string RawText,
    SpeakerHint Hint,
    int ChatType)
{
    /// <summary>
    /// Equivalence port (TextToTalk TextEmitEvent.IsEquivalent): same speaker name and
    /// text. Used by the consecutive-line dedupe so a line announced by two surfaces
    /// (e.g. a talk addon and its chat echo) is spoken once.
    /// </summary>
    public bool IsEquivalent(TextEmitEvent? other) =>
        this.SpeakerName == other?.SpeakerName && this.Text == other.Text;
}

/// <summary>EqualityComparer port (TextEmitEventComparer) for the pipeline dedupe operator.</summary>
public sealed class TextEmitEventComparer : EqualityComparer<TextEmitEvent>
{
    private TextEmitEventComparer()
    {
    }

    public static TextEmitEventComparer Instance { get; } = new();

    public override bool Equals(TextEmitEvent? x, TextEmitEvent? y) => x?.IsEquivalent(y) ?? x == y;

    public override int GetHashCode(TextEmitEvent obj) => obj.GetHashCode();
}
