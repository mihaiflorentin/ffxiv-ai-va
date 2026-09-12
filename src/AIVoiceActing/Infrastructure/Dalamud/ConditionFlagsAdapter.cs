namespace AIVoiceActing.Infrastructure.Dalamud;

using AIVoiceActing.Ports;
using global::Dalamud.Game.ClientState.Conditions;
using global::Dalamud.Plugin.Services;

/// <summary>
/// <see cref="IGameConditions"/> over Dalamud's condition flags (member names verified
/// against the installed API, which has no plain <c>OccupiedInCutScene</c> flag and no
/// login flag on <c>ICondition</c> — login state comes from <c>IClientState</c>).
/// </summary>
public sealed class ConditionFlagsAdapter : IGameConditions
{
    private readonly ICondition condition;
    private readonly IClientState clientState;

    public ConditionFlagsAdapter(ICondition condition, IClientState clientState)
    {
        this.condition = condition;
        this.clientState = clientState;
    }

    /// <summary>Talk-style cutscene events (the game's only cutscene-occupation flag).</summary>
    public bool OccupiedInCutscene => this.condition[ConditionFlag.OccupiedInCutSceneEvent];

    /// <summary>Fullscreen or fading cutscenes (the two watching flags the API exposes).</summary>
    public bool WatchingCutscene =>
        this.condition[ConditionFlag.WatchingCutscene] || this.condition[ConditionFlag.WatchingCutscene78];

    public bool IsLoggedIn => this.clientState.IsLoggedIn;
}
