namespace AIVoiceActing.UI.Pages;

using System.Numerics;
using AIVoiceActing.Domain;
using AIVoiceActing.Ports;
using AIVoiceActing.UI.Components;
using AIVoiceActing.UI.State;
using Dalamud.Bindings.ImGui;

/// <summary>
/// The old Test tab: speak a test line through the exact game path (rules, director context, or a
/// forced emotion), with race/tribe/sex or your own character. Renders go through the shared
/// <see cref="PreviewController"/>, so one test is in flight at a time.
/// </summary>
internal sealed class TestPanel(UiContext ctx)
{
    private readonly TestBenchModel test = new();
    private int lastDrawnFrame = -10;

    public void Draw()
    {
        // Fresh open: the shared "ui-test" context window starts empty, so the director
        // never reads leftovers from an earlier visit (the panel was not drawn last frame).
        var frame = ImGui.GetFrameCount();
        if (frame - this.lastDrawnFrame > 1)
        {
            ctx.Sessions().EndSession(TestBenchModel.ContextSessionId);
        }

        this.lastDrawnFrame = frame;

        var blocked = ctx.PreviewBlockedReason;
        var canSpeak = blocked is null;

        var text = this.test.Text;
        if (ImGui.InputTextMultiline("##test-text", ref text, 512, new Vector2(Ui.Fill(), Ui.Em(4))))
        {
            this.test.Text = text;
        }

        Controls.Checkbox("Use my character", () => this.test.UseMyCharacter, v => this.test.UseMyCharacter = v,
            "Speak the test line with your own character's race/gender voice assignment.");
        if (!this.test.UseMyCharacter)
        {
            var race = Races.All[Math.Clamp(this.test.RaceIndex, 0, Races.All.Count - 1)];
            Controls.Combo("Race", [.. Races.All.Select(r => r.Name)], () => this.test.RaceIndex, v =>
            {
                this.test.RaceIndex = v;
                this.test.TribeIndex = 0;
            }, "Race used for the test line's automatic voice assignment.");
            Controls.Combo("Tribe", [.. race.Tribes.Select(t => t.Name)], () => this.test.TribeIndex, v => this.test.TribeIndex = v, "Tribe refines the race assignment where voice sets differ.");
            Controls.Combo("Sex", ["Male", "Female"], () => (int)this.test.Sex, v => this.test.Sex = (byte)v, "Male or female voice set for the test.");
        }

        Controls.Combo("Emotion", TestBenchModel.Emotions, () => this.test.EmotionIndex, v => this.test.EmotionIndex = v);
        Controls.HelpMarker(
            "\"auto\" uses the rules table (or director) exactly like the game path; a fixed " +
            "emotion auditions that delivery with the slider below.");
        if (this.test.EmotionForced)
        {
            Controls.SliderFraction("Exaggeration", () => this.test.Exaggeration, v => this.test.Exaggeration = v, () => { },
                "How much expressive variation the synthesizer applies: 0 is flat narration, higher is theatrical.");
        }

        var plan = this.test.EmotionForced ? this.test.ForcedPlan() : this.test.RulesPlan();
        ImGui.TextDisabled(
            $"Plan: {plan.Emotion} @ {plan.Exaggeration:0.00}" +
            (plan.Tags.Count > 0 ? $" [{string.Join(", ", plan.Tags)}]" : string.Empty));

        var (myName, myWorld) = ctx.LocalPlayer() ?? default;
        var speaker = this.test.BuildSpeaker(this.test.UseMyCharacter ? myName : null, myWorld);

        if (Controls.Button("Speak", canSpeak, blocked))
        {
            // Rules path: a throwaway session id reads no history — exactly what the
            // rules table would produce in-game. The session is torn down when the line
            // settles so presses do not leak window entries.
            var sessionId = $"ui-rules-{Guid.NewGuid():N}";
            _ = ctx.Preview.Start(
                "test:rules",
                ct => ctx.Speech.SpeakAsync(sessionId, speaker, this.test.Text, ct),
                onSettled: () => ctx.Sessions().EndSession(sessionId));
        }

        ImGui.SameLine();
        if (Controls.Button("Speak with context", canSpeak, blocked))
        {
            // Director path: the shared window the cutscene pipeline feeds. The handler
            // appends the line AFTER planning, so the director never sees it twice.
            _ = ctx.Preview.Start(
                "test:context",
                ct => ctx.Speech.SpeakAsync(TestBenchModel.ContextSessionId, speaker, this.test.Text, ct));
        }

        ImGui.SameLine();
        if (Controls.Button(
                "Speak (forced emotion)",
                canSpeak && this.test.EmotionForced,
                !canSpeak ? blocked : "Pick a fixed emotion first."))
        {
            var voice = this.ResolveVoice(speaker);
            _ = ctx.Preview.StartDirect("test:forced", speaker, this.BuildForcedRequest(voice));
        }
    }

    /// <summary>The forced-emotion request through the game's text-processing chain
    /// (lexicon → stutter → style tags), so the audition matches what the game would say.</summary>
    private SynthesisRequest BuildForcedRequest(string voice)
    {
        var (processed, extractedTags) = ctx.Speech.PrepareText(this.test.Text);
        var plan = this.test.ForcedPlan();
        var tags = plan.Tags.Concat(extractedTags).Distinct().ToList();
        return new SynthesisRequest(
            voice,
            processed,
            Math.Clamp(plan.Exaggeration, 0f, 1f),
            tags);
    }

    /// <summary>
    /// Resolves the voice for a test speaker WITHOUT touching assignment state: an
    /// existing profile wins; otherwise a random slot pick runs over the voice-map slots
    /// with no store entry — auditioning never writes voice-assignments.json.
    /// </summary>
    private string ResolveVoice(SpeakerIdentity speaker)
    {
        var existing = ctx.Profiles.Entries.FirstOrDefault(e => e.SpeakerKey == speaker.Key);
        if (existing is not null)
        {
            return existing.ReferenceVoiceId;
        }

        var map = ctx.VoiceMap();
        var group = VoiceGroupResolver.Resolve(speaker.Race, speaker.Tribe, speaker.Sex, null, null);
        return VoiceAssigner.AssignSlot(map.SlotsFor(group, speaker.Race)).ReferenceVoiceId;
    }
}
