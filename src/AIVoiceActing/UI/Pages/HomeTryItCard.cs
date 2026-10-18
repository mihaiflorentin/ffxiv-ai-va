namespace AIVoiceActing.UI.Pages;

using System.Numerics;
using AIVoiceActing.UI.Components;
using AIVoiceActing.UI.State;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;

/// <summary>
/// "Try it": type a line, pick whose voice says it, press Speak. The result plays through the
/// same path as a line in game. The button stays visible when it cannot work and the reason sits
/// next to it as text, so nobody has to hover to learn why.
/// </summary>
internal sealed class HomeTryItCard(UiContext ctx)
{
    public const string PreviewKey = "home:try-it";

    private const int MaxLength = 400;
    private const float TextLines = 3f;
    private static readonly string[] VoiceChoices = ["Automatic", "My character"];

    private string text = "Well met, adventurer. The road ahead is long, but we will walk it together.";
    private int voiceChoice;

    public void Draw()
    {
        using var card = Card.Begin("home-try-it");
        if (!card.Visible)
        {
            return;
        }

        ImGui.TextUnformatted("Type a line and hear how it sounds.");

        var typed = this.text;
        if (ImGui.InputTextMultiline("##home-try-text", ref typed, MaxLength, new Vector2(Ui.Fill(), Ui.Em(TextLines * 1.4f))))
        {
            this.text = typed;
        }

        Ui.Gap(Ui.Space1);
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted("Voice");
        ImGui.SameLine(Ui.Em(FormRow.LabelColumnEm / 2f));
        this.voiceChoice = SegmentedControl.Draw("home-try-voice", VoiceChoices, this.voiceChoice);
        var mine = this.voiceChoice == 1;
        Ui.MutedText(mine
            ? "Speaks with the voice your own character gets."
            : "Picks a voice the way the game would for an NPC.");

        var blocked = this.BlockedReason(mine);
        Ui.Gap(Ui.Space1);
        if (ActionButton.Primary("Speak##home-try-speak", blocked is null, blocked))
        {
            HomeSpeech.Speak(ctx, PreviewKey, HomeSpeech.Speaker(ctx, mine), this.text);
        }

        ImGui.SameLine();
        this.DrawOutcome(blocked);
    }

    /// <summary>Why Speak cannot run right now; null when it can.</summary>
    private string? BlockedReason(bool mine)
    {
        if (!ctx.Synth.IsReady)
        {
            return ctx.EngineSession.Warming ? "The voices are still loading." : "Set up your voices first.";
        }

        if (ctx.Preview.Busy)
        {
            return "Preparing the voice…";
        }

        if (string.IsNullOrWhiteSpace(this.text))
        {
            return "Type a line first.";
        }

        return mine && ctx.LocalPlayer() is null ? "Log in to a character to use your own voice." : null;
    }

    private void DrawOutcome(string? blocked)
    {
        var preview = ctx.Preview;
        var ours = preview.Key == PreviewKey;
        if (blocked is not null)
        {
            Theme.IconText(preview.Busy ? FontAwesomeIcon.InfoCircle : FontAwesomeIcon.ExclamationTriangle, preview.Busy ? Theme.Info : Theme.Warn);
            ImGui.SameLine(0f, Ui.Em(Ui.Space2));
            Ui.MutedText(blocked);
        }
        else if (ours && preview.State == PreviewState.Playing)
        {
            StatusPill.Draw(Severity.Info, "Speaking…");
        }
        else if (ours && preview.State == PreviewState.Error)
        {
            StatusPill.Draw(Severity.Error, "That did not work: " + (preview.Error ?? "unknown problem"));
        }
    }
}
