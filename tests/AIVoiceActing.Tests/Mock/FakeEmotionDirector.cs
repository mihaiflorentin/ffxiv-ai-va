namespace AIVoiceActing.Tests.Mock;

using AIVoiceActing.Domain;
using AIVoiceActing.Ports;

/// <summary>
/// Fake IEmotionDirector (census fake pattern): records the context it was given;
/// <see cref="PlanFunc"/> overrides the canned <see cref="Plan"/>.
/// </summary>
public sealed class FakeEmotionDirector : IEmotionDirector
{
    public EmotionPlan Plan { get; set; } = EmotionPlan.Neutral;

    public Func<EmotionContext, EmotionPlan>? PlanFunc { get; set; }

    public List<EmotionContext> Requests { get; } = [];

    public Task<EmotionPlan> PlanAsync(EmotionContext context, CancellationToken cancellationToken)
    {
        this.Requests.Add(context);
        return Task.FromResult(this.PlanFunc?.Invoke(context) ?? this.Plan);
    }
}
