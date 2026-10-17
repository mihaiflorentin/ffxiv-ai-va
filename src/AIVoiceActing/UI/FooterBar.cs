namespace AIVoiceActing.UI;

using AIVoiceActing.UI.Components;
using AIVoiceActing.UI.State;
using Dalamud.Bindings.ImGui;

/// <summary>
/// The always-visible footer, drawn below the scrolling page: engine status pill, queue count,
/// the latest status-centre message (with its actions and a dismiss for errors) and "Stop all".
/// Errors are never scrolled out of sight because the footer is not part of the page.
/// </summary>
internal sealed class FooterBar(UiContext ctx)
{
    /// <summary>Vertical space the footer needs, for the page child to leave at the bottom.</summary>
    public static float Height => ImGui.GetFrameHeightWithSpacing() + Ui.Em(Ui.Space2);

    public void Draw()
    {
        ImGui.Separator();
        var style = ImGui.GetStyle();
        var stopLabel = "Stop all";
        var stopWidth = ImGui.CalcTextSize(stopLabel).X + (style.FramePadding.X * 2f);
        var rightEdge = ImGui.GetWindowWidth() - style.WindowPadding.X;
        var stopX = rightEdge - stopWidth;

        this.DrawEnginePill();
        ImGui.SameLine();
        ImGui.TextDisabled($"Queue: {ctx.Queue.Depth}");
        var message = ctx.Status.Active().FirstOrDefault();
        if (message is not null)
        {
            ImGui.SameLine();
            this.DrawMessage(message, stopX - ImGui.GetCursorPosX() - Ui.Em(Ui.Space3));
        }

        ImGui.SameLine(stopX);
        var busy = ctx.Queue.Depth > 0 || ctx.Queue.NowPlaying is not null || ctx.Preview.State != PreviewState.Idle;
        if (Controls.Button(stopLabel, busy, "Nothing is speaking."))
        {
            ctx.Queue.Clear();
            ctx.Preview.Cancel();
        }
    }

    private void DrawEnginePill()
    {
        if (ctx.EngineSession.Warming)
        {
            StatusPill.Draw(Severity.Info, "Loading engine…");
        }
        else if (ctx.Synth.IsReady)
        {
            StatusPill.Draw(Severity.Success, "Ready");
        }
        else
        {
            StatusPill.Draw(Severity.Warning, "Not ready");
            Ui.Tooltip(ctx.EngineReason());
        }
    }

    private void DrawMessage(StatusMessage message, float maxWidth)
    {
        var text = message.Count > 1 ? $"{message.Title} (x{message.Count})" : message.Title;
        if (message.Detail is { Length: > 0 } detail)
        {
            text += ": " + detail;
        }

        var actionsWidth = 0f;
        for (var i = 0; i < message.Actions.Count; i++)
        {
            actionsWidth += ImGui.CalcTextSize(message.Actions[i].Label).X + (ImGui.GetStyle().FramePadding.X * 2f) + ImGui.GetStyle().ItemSpacing.X;
        }

        if (message.Severity == Severity.Error)
        {
            actionsWidth += ImGui.CalcTextSize("Dismiss").X + (ImGui.GetStyle().FramePadding.X * 2f) + ImGui.GetStyle().ItemSpacing.X;
        }

        StatusPill.Draw(message.Severity, Fit(text, maxWidth - actionsWidth));
        Ui.Tooltip(message.Detail is null ? message.Title : message.Title + "\n" + message.Detail);

        for (var i = 0; i < message.Actions.Count; i++)
        {
            ImGui.SameLine();
            if (ImGui.SmallButton(message.Actions[i].Label + "##status-action-" + i))
            {
                ctx.Status.Invoke(message.Id, i);
            }
        }

        if (message.Severity == Severity.Error)
        {
            ImGui.SameLine();
            if (ImGui.SmallButton("Dismiss##status-dismiss"))
            {
                ctx.Status.Dismiss(message.Id);
            }
        }
    }

    /// <summary>Truncates <paramref name="text"/> with an ellipsis so it fits in <paramref name="maxWidth"/>.</summary>
    private static string Fit(string text, float maxWidth)
    {
        if (maxWidth <= 0f)
        {
            return string.Empty;
        }

        if (ImGui.CalcTextSize(text).X <= maxWidth)
        {
            return text;
        }

        var length = text.Length;
        while (length > 1 && ImGui.CalcTextSize(text[..length] + "…").X > maxWidth)
        {
            length--;
        }

        return text[..length] + "…";
    }
}
