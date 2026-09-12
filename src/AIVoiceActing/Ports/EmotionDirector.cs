namespace AIVoiceActing.Ports;

using AIVoiceActing.Domain;

/// <summary>Input context for planning the delivery of the current line.</summary>
/// <param name="Speaker">Resolved identity of the current speaker.</param>
/// <param name="Line">The line to perform.</param>
/// <param name="History">Recent dialogue window, oldest first, current line excluded.</param>
public sealed record EmotionContext(
    SpeakerIdentity Speaker,
    string Line,
    IReadOnlyList<DialogueLine> History);

/// <summary>Driven port that turns dialogue context into an <see cref="EmotionPlan"/>.</summary>
public interface IEmotionDirector
{
    Task<EmotionPlan> PlanAsync(EmotionContext context, CancellationToken cancellationToken);
}
