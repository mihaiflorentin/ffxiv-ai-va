namespace AIVoiceActing.UI.Components;

using AIVoiceActing.UI.State;
using Dalamud.Bindings.ImGui;

/// <summary>
/// Home world chooser for a player's name: a combo of world names when the game data is available,
/// a plain number box otherwise. The value is the world id as text, ready for a speaker key.
/// </summary>
public static class WorldPicker
{
    private const float ComboWidthEm = 9f;
    private const float NumberWidthEm = 5f;
    private const int MaxIdLength = 16;

    public static void Draw(WorldDirectory worlds, string id, ref string world)
    {
        var list = worlds.Sorted();
        if (list.Count > 0)
        {
            ImGui.SetNextItemWidth(Ui.Em(ComboWidthEm));
            var shown = uint.TryParse(world, out var selectedId) ? worlds.Label(selectedId) : "World";
            if (ImGui.BeginCombo("##world-" + id, shown))
            {
                foreach (var (worldId, worldName) in list)
                {
                    if (ImGui.Selectable(worldName, world == worldId.ToString()))
                    {
                        world = worldId.ToString();
                    }
                }

                ImGui.EndCombo();
            }

            Ui.Tooltip("The character's home world, to tell same-named characters apart.");
        }
        else
        {
            ImGui.SetNextItemWidth(Ui.Em(NumberWidthEm));
            ImGui.InputTextWithHint("##world-" + id, "World id", ref world, MaxIdLength);
            Ui.Tooltip("Numeric world id distinguishing same-named characters across worlds.");
        }
    }
}
