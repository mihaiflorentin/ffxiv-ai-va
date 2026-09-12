namespace AIVoiceActing.UI.State;

using AIVoiceActing.Domain;
using AIVoiceActing.Ports;

/// <summary>
/// Pure Test-tab brain: turns the picked speaker/emotion/exaggeration/text into either
/// (a) a handler call — the exact game path, where the rules table (or context director)
/// plans delivery — or (b) an explicit-emotion synthesis request rendered through the same
/// ISpeechSynthesizer + ISpeechQueue ports the handler itself drives. No private synthesis
/// pipeline exists; both buttons terminate in container-resolved ports.
/// </summary>
public sealed class TestBenchModel
{
    /// <summary>Session id for the rules path — unique per press, so no history is read.</summary>
    public const string ContextSessionId = "ui-test";

    /// <summary>Emotion dropdown entries; "auto" defers to the rules/director plan.</summary>
    public static readonly IReadOnlyList<string> Emotions =
        ["auto", "excited", "angry", "sad", "amused", "curious", "neutral"];

    /// <summary>The line a per-profile ▶ Test button speaks.</summary>
    public const string VoiceTestLine = "This is the voice I will use.";

    /// <summary>Dropdown index for a named emotion ("auto" → 0).</summary>
    public static int EmotionIndexOf(string emotion) => Emotions.ToList().IndexOf(emotion);
    public string Text { get; set; } = string.Empty;
    public bool UseMyCharacter { get; set; }
    public int RaceIndex { get; set; }
    public int TribeIndex { get; set; }
    public byte Sex { get; set; }
    public int EmotionIndex { get; set; }
    public float Exaggeration { get; set; } = 0.5f;

    public string Emotion => Emotions[Math.Clamp(this.EmotionIndex, 0, Emotions.Count - 1)];

    /// <summary>True when the user picked a concrete emotion (overrides the rules table).</summary>
    public bool EmotionForced => this.Emotion != "auto";

    /// <summary>The plan the rules table would give the current text (preview line).</summary>
    public EmotionPlan RulesPlan() => EmotionRules.Plan(this.Text);

    /// <summary>The plan an explicit emotion choice yields (slider-driven exaggeration).</summary>
    public EmotionPlan ForcedPlan() =>
        new(this.Emotion, Math.Clamp(this.Exaggeration, 0f, 1f), [], PauseBeforeMs: 0, Volume: 1f);

    /// <summary>
    /// Speaker identity for the current picker state. "My character" uses the local
    /// player's real pc-key (via the injected name/world); otherwise a deterministic
    /// synthetic NPC-style key carrying the picked race/tribe/sex.
    /// </summary>
    public SpeakerIdentity BuildSpeaker(string? myName, ushort? myWorld)
    {
        if (this.UseMyCharacter)
        {
            var name = string.IsNullOrWhiteSpace(myName) ? "Unknown Player" : myName.Trim();
            var world = myWorld ?? 0;
            return new SpeakerIdentity(
                SpeakerKey.ForPlayer(name, world),
                name,
                Race: null,
                Tribe: null,
                Sex: null,
                World: world);
        }

        var race = Races.All[Math.Clamp(this.RaceIndex, 0, Races.All.Count - 1)];
        var tribe = race.Tribes[Math.Clamp(this.TribeIndex, 0, race.Tribes.Count - 1)];
        var display = $"Test {race.Name} ({Races.SexName(this.Sex)})";
        return new SpeakerIdentity(
            SpeakerKey.ForNpc(display),
            display,
            Race: race.Id,
            Tribe: tribe.Id,
            Sex: this.Sex,
            World: null);
    }

    /// <summary>The explicit-emotion synthesis request (direct ISpeechSynthesizer path).</summary>
    public SynthesisRequest BuildDirectRequest(string referenceVoiceId, float bias)
    {
        var plan = this.ForcedPlan();
        return new SynthesisRequest(
            referenceVoiceId,
            this.Text,
            Math.Clamp(plan.Exaggeration + bias, 0f, 1f),
            plan.Tags);
    }

    /// <summary>The per-voice ▶ Test request through the same direct path.</summary>
    public static SynthesisRequest BuildVoiceTestRequest(string referenceVoiceId, float exaggeration) =>
        new(referenceVoiceId, VoiceTestLine, Math.Clamp(exaggeration, 0f, 1f), []);
}
