namespace AIVoiceActing.UI.State;

/// <summary>
/// The content height each card measured on the frame it was last drawn. A card scrolled fully out
/// of view is skipped by ImGui, so its measurement that frame is only the padding: recording it would
/// shrink the card, shift the page, move the scroll and bring the card back, over and over (the
/// Settings page "scrolled forever"). Only measurements from frames where the card drew count.
/// </summary>
public sealed class CardHeights
{
    private readonly Dictionary<string, float> heights = new();

    public float Get(string id, float fallback) => this.heights.TryGetValue(id, out var h) ? h : fallback;

    public void Record(string id, float measured, bool drew)
    {
        if (drew)
        {
            this.heights[id] = measured;
        }
    }
}
