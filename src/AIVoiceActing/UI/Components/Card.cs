namespace AIVoiceActing.UI.Components;

using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;

/// <summary>
/// A padded, rounded surface slightly lighter than the window. Usage:
/// <c>using (Card.Begin("overview")) { ... }</c>.
/// These ImGui bindings have no auto-resize child flag, so the card remembers its content
/// height from the previous frame; a card settles on its real height one frame after it
/// first appears or its content changes.
/// </summary>
public static class Card
{
    private static readonly Dictionary<string, float> Heights = new();

    public static CardScope Begin(string id, Vector4? background = null) => new(id, background);

    public readonly struct CardScope : IDisposable
    {
        private readonly ImRaii.ColorDisposable colours;
        private readonly ImRaii.StyleDisposable styles;
        private readonly string id;
        private readonly bool visible;

        internal CardScope(string id, Vector4? background)
        {
            this.id = id;
            this.colours = ImRaii.PushColor(ImGuiCol.ChildBg, background ?? Theme.Card);
            this.styles = ImRaii.PushStyle(ImGuiStyleVar.ChildRounding, Ui.Em(0.4f))
                .Push(ImGuiStyleVar.WindowPadding, new Vector2(Ui.Em(Ui.Space3), Ui.Em(Ui.Space3)));
            var height = Heights.TryGetValue(id, out var h) ? h : Ui.Em(3f);
            this.visible = ImGui.BeginChild(
                "##card-" + id,
                Vector2.UnitY * height,
                true,
                ImGuiWindowFlags.AlwaysUseWindowPadding | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse);
        }

        /// <summary>False when the card is clipped away; the body can be skipped.</summary>
        public bool Visible => this.visible;

        public void Dispose()
        {
            Heights[this.id] = ImGui.GetCursorPosY() + ImGui.GetStyle().WindowPadding.Y;
            ImGui.EndChild();
            this.styles.Dispose();
            this.colours.Dispose();
            Ui.Gap(Ui.Space3);
        }
    }
}
