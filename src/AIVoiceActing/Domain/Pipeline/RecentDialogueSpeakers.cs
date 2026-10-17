namespace AIVoiceActing.Domain.Pipeline;

/// <summary>
/// Short memory of who said what in the NPC-dialogue chat channel. The cutscene subtitle
/// surface carries no speaker, but the game echoes the same line to chat with the name, so
/// the subtitle source asks here to give the line its speaker's voice. Text is compared
/// after collapsing whitespace, case-insensitively; entries expire after
/// <see cref="WindowMs"/>. Thread-safe (chat and frame callbacks).
/// </summary>
public sealed class RecentDialogueSpeakers
{
    public const long WindowMs = 15_000;
    private const int Capacity = 32;

    private readonly object gate = new();
    private readonly LinkedList<(string Text, string Speaker, long At)> lines = new();

    public void Record(string text, string speaker, long nowMs)
    {
        var key = Normalize(text);
        if (key.Length == 0 || string.IsNullOrWhiteSpace(speaker))
        {
            return;
        }

        lock (this.gate)
        {
            this.lines.AddFirst((key, speaker.Trim(), nowMs));
            while (this.lines.Count > Capacity)
            {
                this.lines.RemoveLast();
            }
        }
    }

    /// <summary>The speaker of the most recent matching line inside the window, or null.</summary>
    public string? Find(string text, long nowMs)
    {
        var key = Normalize(text);
        lock (this.gate)
        {
            foreach (var (lineText, speaker, at) in this.lines)
            {
                if (nowMs - at > WindowMs)
                {
                    break;
                }

                if (string.Equals(lineText, key, StringComparison.OrdinalIgnoreCase))
                {
                    return speaker;
                }
            }
        }

        return null;
    }

    private static string Normalize(string text) =>
        string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}
