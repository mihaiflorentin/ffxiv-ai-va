namespace AIVoiceActing.UI.Components;

using System.Numerics;
using AIVoiceActing.Domain;
using AIVoiceActing.Ports;
using AIVoiceActing.UI.State;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;

/// <summary>
/// The scrolling list of voices shared by the library and the voice picker: one fixed-height
/// row per voice with its fingerprint (which pulses while the voice previews), name, accent /
/// sex / age tags, quality, description, licence and credit, a preview button and one row action
/// ("Use for..." in the library, "Choose" in the picker). Rows are clipped, so a long list costs
/// only what is on screen. A voice an installed pack no longer provides shows as a "Missing"
/// row so nothing that is still cast to it disappears silently.
/// </summary>
public sealed class VoiceRowsView(UiContext ctx)
{
    private const float FingerprintColumnEm = 4f;
    private const float ActionsColumnEm = 12f;
    private const int RowLines = 3;
    private const float FingerprintDropEm = 0.15f;

    private IReadOnlyList<VoicePackInfo>? packsSeen;
    private Dictionary<string, string> packNames = new(StringComparer.Ordinal);

    /// <summary>The line previews speak; empty speaks the stock sample.</summary>
    public string PreviewLine { get; set; } = string.Empty;

    /// <summary>
    /// Draws the list, or the empty state when there is nothing to list; the empty state's action
    /// clears the filters of <paramref name="model"/> or opens the packs folder.
    /// </summary>
    public void Draw(
        string id,
        VoiceLibraryModel model,
        VoiceListResult result,
        float heightEm,
        string actionLabel,
        Action<VoiceInfo> onAction,
        string? currentId = null)
    {
        if (result.Empty is { } empty)
        {
            if (EmptyState.Draw(FontAwesomeIcon.Search, empty.Title, empty.Hint, empty.ActionLabel))
            {
                if (empty.Kind == ListEmptyKind.Nothing)
                {
                    ctx.TryOpen(ctx.PacksDir());
                }
                else
                {
                    model.Clear();
                }
            }

            return;
        }

        this.RefreshPackNames();
        var blocked = ctx.VoicesNotReadyReason;
        var rowHeight = (ImGui.GetTextLineHeightWithSpacing() * RowLines) + Ui.Em(Ui.Space2);

        ImGui.PushID(id);
        if (ImGui.BeginTable(
                "##voice-rows",
                3,
                ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY | ImGuiTableFlags.BordersInnerH,
                new Vector2(Ui.Fill(), Ui.Em(heightEm))))
        {
            ImGui.TableSetupColumn("##fingerprint", ImGuiTableColumnFlags.WidthFixed, Ui.Em(FingerprintColumnEm));
            ImGui.TableSetupColumn("Voice", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("##actions", ImGuiTableColumnFlags.WidthFixed, Ui.Em(ActionsColumnEm));

            var clipper = ImGui.ImGuiListClipper();
            clipper.Begin(result.Rows.Count);
            while (clipper.Step())
            {
                for (var i = clipper.DisplayStart; i < clipper.DisplayEnd; i++)
                {
                    this.DrawRow(result.Rows[i], rowHeight, blocked, actionLabel, onAction, currentId);
                }
            }

            clipper.End();
            clipper.Destroy();
            ImGui.EndTable();
        }

        ImGui.PopID();
    }

    private void DrawRow(
        VoiceListRow row,
        float rowHeight,
        string? blocked,
        string actionLabel,
        Action<VoiceInfo> onAction,
        string? currentId)
    {
        ImGui.PushID(row.Id);
        ImGui.TableNextRow(ImGuiTableRowFlags.None, rowHeight);

        ImGui.TableNextColumn();
        ImGui.SetCursorPosY(ImGui.GetCursorPosY() + Ui.Em(FingerprintDropEm));
        var voice = row.Voice;
        VoiceFingerprint.Draw(
            row.Id,
            PreviewButton.IsActive(ctx.Preview, row.Id),
            ctx.Config.ReduceMotion,
            dim: voice is null || !voice.Available);

        ImGui.TableNextColumn();
        if (voice is null)
        {
            DrawMissing(row.Id);
        }
        else
        {
            this.DrawDetails(voice, currentId);
        }

        ImGui.TableNextColumn();
        if (voice is not null)
        {
            this.DrawActions(voice, rowHeight, blocked, actionLabel, onAction);
        }

        ImGui.PopID();
    }

    private static void DrawMissing(string id)
    {
        ImGui.TextUnformatted(id);
        ImGui.SameLine();
        Chip.Tag("Missing", Theme.Error);
        Ui.MutedTextInline("A casting or character still uses this voice, but no installed pack has it.");
        Ui.MutedTextInline("Turn its pack back on below, or pick another voice where it is used.");
    }

    private void DrawDetails(VoiceInfo voice, string? currentId)
    {
        ImGui.TextUnformatted(voice.DisplayName);
        ImGui.SameLine();
        Chip.Tag(Accents.LabelFor(voice.Accent));
        ImGui.SameLine();
        Chip.Tag(Labels.Sex(voice.Sex));
        ImGui.SameLine();
        Chip.Tag(Labels.Age(voice.AgeBand));
        if (voice.Quality == VoiceQuality.Experimental)
        {
            ImGui.SameLine();
            Chip.Tag("Experimental", Theme.Warn);
        }

        if (!voice.Available)
        {
            ImGui.SameLine();
            Chip.Tag("Unavailable", Theme.Error);
        }

        if (voice.Id == currentId)
        {
            ImGui.SameLine();
            Chip.Tag("Current", Theme.Ok);
        }

        var width = ImGui.GetContentRegionAvail().X;
        var second = voice.Available ? voice.Description : voice.UnavailableReason ?? "This voice's files are missing.";
        Ui.MutedTextInline(Ui.Ellipsize(second, width));
        Ui.MutedTextInline(Ui.Ellipsize(this.CreditLine(voice), width));
    }

    private void DrawActions(VoiceInfo voice, float rowHeight, string? blocked, string actionLabel, Action<VoiceInfo> onAction)
    {
        ImGui.SetCursorPosY(ImGui.GetCursorPosY() + ((rowHeight - ImGui.GetFrameHeight()) * 0.5f) - Ui.Em(Ui.Space1));
        var why = voice.Available ? blocked : voice.UnavailableReason ?? "This voice's files are missing.";
        PreviewButton.Draw("##preview", ctx.Preview, voice.Id, why, () => ctx.PreviewVoice(voice.Id, null, this.PreviewLine));
        ImGui.SameLine();
        if (ActionButton.Secondary(actionLabel + "##use", voice.Available, voice.UnavailableReason ?? "This voice cannot be used right now."))
        {
            onAction(voice);
        }
    }

    /// <summary>"Pack name, licence" plus the credit line a pack asks for; stock voices say they come with the plugin.</summary>
    private string CreditLine(VoiceInfo voice)
    {
        var source = voice.Quality == VoiceQuality.Stock
            ? "Comes with the plugin"
            : this.packNames.GetValueOrDefault(voice.PackId, voice.PackId);
        var credit = string.IsNullOrWhiteSpace(voice.Attribution) ? string.Empty : $" · {voice.Attribution}";
        return $"{source} · {voice.License}{credit}";
    }

    private void RefreshPackNames()
    {
        var packs = ctx.VoiceLibrary.Packs;
        if (ReferenceEquals(packs, this.packsSeen))
        {
            return;
        }

        this.packsSeen = packs;
        this.packNames = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pack in packs)
        {
            this.packNames[pack.Id] = pack.Name;
        }
    }
}
