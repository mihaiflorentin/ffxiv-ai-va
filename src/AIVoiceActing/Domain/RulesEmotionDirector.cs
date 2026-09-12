namespace AIVoiceActing.Domain;

using AIVoiceActing.Ports;

/// <summary>
/// Always-available emotion director (zero footprint): adapts the pure
/// <see cref="EmotionRules"/> table to the <see cref="IEmotionDirector"/> port. No rule
/// logic lives here — delegation only — so the director and any direct rules consumer
/// can never drift apart.
/// </summary>
public sealed class RulesEmotionDirector : IEmotionDirector
{
    public static RulesEmotionDirector Instance { get; } = new();

    public Task<EmotionPlan> PlanAsync(EmotionContext context, CancellationToken cancellationToken) =>
        Task.FromResult(EmotionRules.Plan(context.Line));
}
