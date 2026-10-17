namespace AIVoiceActing.Domain;

using AIVoiceActing.Ports;

/// <summary>
/// Small in-memory LRU of rendered lines, keyed by everything that shapes the audio
/// (engine, voice, text, intensity, pitch, pace, tags). Repeated lines (BattleTalk
/// barks, re-read dialogue, the Test tab) play instantly instead of re-synthesizing.
/// Bounded by total samples (about 40 MB of float audio); thread-safe.
/// </summary>
public sealed class SynthesisCache
{
    public const long MaxSamples = 10_000_000;

    private readonly object gate = new();
    private readonly LinkedList<(string Key, SynthesisResult Audio)> order = new();
    private readonly Dictionary<string, LinkedListNode<(string Key, SynthesisResult Audio)>> byKey = new(StringComparer.Ordinal);
    private long samples;

    public static string KeyFor(string engine, SynthesisRequest request) =>
        string.Join(
            '\u001f',
            engine,
            request.ReferenceVoiceId,
            request.Text,
            request.Exaggeration.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture),
            request.Pitch.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture),
            request.Speed.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture),
            string.Join(',', request.Tags));

    public bool TryGet(string key, out SynthesisResult audio)
    {
        lock (this.gate)
        {
            if (this.byKey.TryGetValue(key, out var node))
            {
                this.order.Remove(node);
                this.order.AddFirst(node);
                audio = node.Value.Audio;
                return true;
            }
        }

        audio = null!;
        return false;
    }

    public void Put(string key, SynthesisResult audio)
    {
        if (audio.Samples.Length == 0 || audio.Samples.Length > MaxSamples / 4)
        {
            return; // one huge line must not flush everything else
        }

        lock (this.gate)
        {
            if (this.byKey.Remove(key, out var existing))
            {
                this.order.Remove(existing);
                this.samples -= existing.Value.Audio.Samples.Length;
            }

            this.byKey[key] = this.order.AddFirst((key, audio));
            this.samples += audio.Samples.Length;
            while (this.samples > MaxSamples && this.order.Last is { } last)
            {
                this.order.RemoveLast();
                this.byKey.Remove(last.Value.Key);
                this.samples -= last.Value.Audio.Samples.Length;
            }
        }
    }

    public void Clear()
    {
        lock (this.gate)
        {
            this.order.Clear();
            this.byKey.Clear();
            this.samples = 0;
        }
    }

    public int Count
    {
        get
        {
            lock (this.gate)
            {
                return this.byKey.Count;
            }
        }
    }
}
