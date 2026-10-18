namespace AIVoiceActing.UI.Components;

using Dalamud.Bindings.ImGui;

/// <summary>
/// Label column (14 em) plus a control column that fills the rest, with optional helper text
/// underneath. <c>using (FormRow.Begin("Volume", "How loud voices are.")) { ImGui.SliderFloat(...); }</c>
/// </summary>
public static class FormRow
{
    public const float LabelColumnEm = 14f;

    public static FormRowScope Begin(string label, string? helper = null)
    {
        return new FormRowScope(label, helper);
    }

    public readonly struct FormRowScope : IDisposable
    {
        private readonly string? helper;

        internal FormRowScope(string label, string? helper)
        {
            this.helper = helper;
            ImGui.AlignTextToFramePadding();
            ImGui.TextUnformatted(label);
            ImGui.SameLine(Ui.Em(LabelColumnEm));
            ImGui.SetNextItemWidth(Ui.Fill());
        }

        public void Dispose()
        {
            if (!string.IsNullOrEmpty(this.helper))
            {
                ImGui.Dummy(new System.Numerics.Vector2(Ui.Em(LabelColumnEm), 0f));
                ImGui.SameLine();
                Ui.MutedText(this.helper);
            }
        }
    }
}
