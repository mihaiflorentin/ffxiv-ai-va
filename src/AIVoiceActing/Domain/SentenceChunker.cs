namespace AIVoiceActing.Domain;

/// <summary>
/// Splits a long line into sentence-sized chunks so the first one can start playing while
/// the rest synthesizes (time-to-first-audio drops from the whole line to one sentence).
/// Short lines stay whole: below <see cref="MinLineChars"/> the gap between chunks would
/// cost more than it saves. Pieces shorter than <see cref="MinChunkChars"/> merge into the
/// next one, so "Yes. Of course." never becomes two tiny renders.
/// </summary>
public static class SentenceChunker
{
    public const int MinLineChars = 120;
    public const int MinChunkChars = 40;

    /// <summary>
    /// Longest stretch of text one streamed render takes (about 200 speech tokens, under the model's 256 cap).
    /// Streaming engines pay a cold start for every render, so consecutive sentences share one up to this size.
    /// </summary>
    public const int StreamMaxChars = 300;

    public static IReadOnlyList<string> Split(string text)
    {
        var trimmed = text.Trim();
        if (trimmed.Length < MinLineChars)
        {
            return [trimmed];
        }

        var chunks = new List<string>();
        var pending = string.Empty;
        foreach (var sentence in Sentences(trimmed))
        {
            pending = pending.Length == 0 ? sentence : $"{pending} {sentence}";
            if (pending.Length >= MinChunkChars)
            {
                chunks.Add(pending);
                pending = string.Empty;
            }
        }

        if (pending.Length > 0)
        {
            if (chunks.Count > 0)
            {
                chunks[^1] = $"{chunks[^1]} {pending}";
            }
            else
            {
                chunks.Add(pending);
            }
        }

        return chunks;
    }

    /// <summary>
    /// The split for engines that stream: whole sentences are packed into one chunk while it stays within
    /// <paramref name="maxChars"/>, so an ordinary line is one render. A longer line still breaks only at
    /// sentence ends; a single sentence longer than the limit stays whole.
    /// </summary>
    public static IReadOnlyList<string> SplitForStreaming(string text, int maxChars = StreamMaxChars)
    {
        var trimmed = text.Trim();
        if (trimmed.Length <= maxChars)
        {
            return [trimmed];
        }

        var chunks = new List<string>();
        var pending = string.Empty;
        foreach (var sentence in Sentences(trimmed))
        {
            if (pending.Length > 0 && pending.Length + 1 + sentence.Length > maxChars)
            {
                chunks.Add(pending);
                pending = string.Empty;
            }

            pending = pending.Length == 0 ? sentence : $"{pending} {sentence}";
        }

        if (pending.Length > 0)
        {
            chunks.Add(pending);
        }

        return chunks;
    }

    private static List<string> Sentences(string trimmed)
    {
        var sentences = new List<string>();
        var start = 0;
        for (var i = 0; i < trimmed.Length; i++)
        {
            var ch = trimmed[i];
            if (ch is not ('.' or '!' or '?' or '…'))
            {
                continue;
            }

            // Absorb runs like "?!" or "..." and a closing quote before the boundary.
            var end = i + 1;
            while (end < trimmed.Length && trimmed[end] is '.' or '!' or '?' or '…' or '"' or '\'' or '”' or '’' or ')')
            {
                end++;
            }

            if (end < trimmed.Length && !char.IsWhiteSpace(trimmed[end]))
            {
                i = end - 1;
                continue; // "e.g." or "3.5": not a sentence end
            }

            sentences.Add(trimmed[start..end].Trim());
            start = end;
            i = end - 1;
        }

        if (start < trimmed.Length && trimmed[start..].Trim() is { Length: > 0 } tail)
        {
            sentences.Add(tail);
        }

        return sentences;
    }
}
