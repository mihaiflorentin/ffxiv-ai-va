namespace AIVoiceActing.UI.Components;

using Dalamud.Bindings.ImGui;

/// <summary>
/// Owns every modal of one window. Each modal id is registered once and drawn from a single
/// <see cref="Draw"/> call at the same point of the ID stack every frame, so OpenPopup and
/// BeginPopupModal always agree on the id (the old per-call-site pairs could drift apart and
/// leave a modal that never opens). Registering an id again replaces its body.
/// </summary>
public sealed class ModalHost
{
    private readonly Dictionary<string, Entry> entries = new();
    private readonly HashSet<string> pendingOpen = new();
    private readonly HashSet<string> pendingClose = new();

    public IReadOnlyCollection<string> RegisteredIds => this.entries.Keys;

    /// <summary>
    /// Registers (or replaces) the modal <paramref name="id"/>; <paramref name="body"/> runs each frame while it is open.
    /// A modal that draws its own nested popup passes <paramref name="escapeBlocked"/>, which returns true while
    /// that popup is open so Esc closes the inner popup only.
    /// </summary>
    public void Register(string id, string title, Action body, Func<bool>? escapeBlocked = null)
    {
        this.entries[id] = new Entry(title, body, escapeBlocked);
    }

    /// <summary>Opens a registered modal on the next <see cref="Draw"/>. Returns false for an unknown id.</summary>
    public bool Open(string id)
    {
        if (!this.entries.ContainsKey(id))
        {
            return false;
        }

        this.pendingOpen.Add(id);
        return true;
    }

    /// <summary>Closes the modal from inside or outside its body.</summary>
    public void Close(string id) => this.pendingClose.Add(id);

    public bool IsOpen(string id) => this.entries.ContainsKey(id) && ImGui.IsPopupOpen(PopupId(id, this.entries[id]));

    /// <summary>Call once per frame at the end of the window's Draw, outside any child or table.</summary>
    public void Draw()
    {
        foreach (var (id, entry) in this.entries)
        {
            var popup = PopupId(id, entry);
            if (this.pendingOpen.Remove(id))
            {
                ImGui.OpenPopup(popup);
            }

            ImGui.SetNextWindowPos(ImGui.GetMainViewport().GetCenter(), ImGuiCond.Appearing, Ui.CenterPivot);
            var open = true;
            if (!ImGui.BeginPopupModal(popup, ref open, ImGuiWindowFlags.AlwaysAutoResize))
            {
                this.pendingClose.Remove(id);
                continue;
            }

            try
            {
                entry.Body();
            }
            finally
            {
                if (this.pendingClose.Remove(id)
                    || (ImGui.IsKeyPressed(ImGuiKey.Escape) && entry.EscapeBlocked?.Invoke() != true))
                {
                    ImGui.CloseCurrentPopup();
                }

                ImGui.EndPopup();
            }
        }
    }

    // Visible title, stable id: the title may change without breaking the open/close pairing.
    private static string PopupId(string id, Entry entry) => entry.Title + "###aiva-modal-" + id;

    private sealed record Entry(string Title, Action Body, Func<bool>? EscapeBlocked);
}
