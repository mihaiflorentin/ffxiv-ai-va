namespace AIVoiceActing.UI.Components;

using Dalamud.Bindings.ImGui;

/// <summary>
/// "Are you sure?" modal that names the object and count. Focus starts on Cancel, so a stray
/// Enter never deletes anything; the destructive button uses the danger fill.
/// </summary>
public sealed class ConfirmDialog
{
    private const string ModalId = "confirm";

    private readonly ModalHost host;
    private string message = string.Empty;
    private string confirmLabel = "Confirm";
    private bool destructive = true;
    private Action? onConfirm;

    public ConfirmDialog(ModalHost host)
    {
        this.host = host;
        host.Register(ModalId, "Please confirm", this.DrawBody);
    }

    /// <summary>Asks the player; <paramref name="onConfirm"/> runs only if they confirm.</summary>
    public void Ask(string message, string confirmLabel, Action onConfirm, bool destructive = true)
    {
        this.message = message;
        this.confirmLabel = confirmLabel;
        this.onConfirm = onConfirm;
        this.destructive = destructive;
        this.host.Open(ModalId);
    }

    private void DrawBody()
    {
        ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + Ui.Em(24f));
        ImGui.TextWrapped(this.message);
        ImGui.PopTextWrapPos();
        Ui.Gap(Ui.Space3);

        if (ImGui.IsWindowAppearing())
        {
            ImGui.SetKeyboardFocusHere();
        }

        if (ImGui.Button("Cancel", Ui.Em(7f, 0f)))
        {
            this.host.Close(ModalId);
        }

        ImGui.SetItemDefaultFocus();
        ImGui.SameLine();
        var pressed = this.destructive
            ? Theme.DangerButton(this.confirmLabel + "##confirm-ok", Ui.Em(7f, 0f))
            : Theme.PrimaryButton(this.confirmLabel + "##confirm-ok", Ui.Em(7f, 0f));
        if (pressed)
        {
            var action = this.onConfirm;
            this.onConfirm = null;
            this.host.Close(ModalId);
            action?.Invoke();
        }
    }
}
