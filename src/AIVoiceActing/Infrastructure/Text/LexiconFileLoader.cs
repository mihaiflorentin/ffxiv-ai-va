namespace AIVoiceActing.Infrastructure.Text;

using System.Security.Cryptography;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using AIVoiceActing.Ports;

/// <summary>
/// Loads .pls-style pronunciation-lexicon XML files (word → replacement: each
/// &lt;lexeme&gt;'s &lt;grapheme&gt; text is the word, its &lt;phoneme&gt; text the
/// replacement) from user-configured paths. Missing or invalid files warn through the
/// log sink and are skipped — a bad lexicon never blocks speech. The merged result is
/// cached against the file set's stamps (path + last write + length): unchanged files
/// return the same entry instance on every load, so the per-line reload in
/// <see cref="LexiconProcessor"/> is allocation-free until a file actually changes.
/// </summary>
public static class LexiconFileLoader
{
    private static readonly IReadOnlyDictionary<string, string> Empty = new Dictionary<string, string>();

    private static readonly object Gate = new();

    private static (string Key, IReadOnlyDictionary<string, string> Entries)? cache;

    /// <summary>
    /// Merges every file's entries in order (later files win on word collisions);
    /// returns an empty dictionary when no file parses.
    public static IReadOnlyDictionary<string, string> Load(IEnumerable<string> paths, ILogSink? log = null)
    {
        var list = paths as IReadOnlyList<string> ?? [.. paths];
        var key = CacheKey(list);
        lock (Gate)
        {
            if (cache is { } hit && hit.Key == key)
            {
                return hit.Entries;
            }
        }

        var merged = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in list)
        {
            foreach (var (word, replacement) in LoadOne(path, log))
            {
                merged[word] = replacement;
            }
        }

        lock (Gate)
        {
            cache = (key, merged);
        }

        return merged;
    }

    private static string CacheKey(IReadOnlyList<string> paths)
    {
        var key = new StringBuilder();
        foreach (var path in paths)
        {
            key.Append(path).Append(':');
            try
            {
                var info = new FileInfo(path);
                if (info.Exists)
                {
                    key.Append(info.LastWriteTimeUtc.Ticks).Append(':').Append(info.Length);
                }
                else
                {
                    key.Append("missing");
                }
            }
            catch (IOException)
            {
                key.Append("unreadable");
            }

            key.Append('|');
        }

        // Truncated hashes are fine here: this guards an in-memory cache, not data.
        return Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(key.ToString())));
    }

    private static IReadOnlyDictionary<string, string> LoadOne(string path, ILogSink? log)
    {
        try
        {
            return Parse(File.ReadAllText(path));
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or InvalidOperationException or XmlException)
        {
            log?.Warn($"Lexicon file \"{path}\" could not be loaded and is skipped ({ex.Message}).");
            return Empty;
        }
    }

    private static IReadOnlyDictionary<string, string> Parse(string xml)
    {
        var document = XDocument.Parse(xml);
        var entries = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var lexeme in document.Descendants().Where(e => e.Name.LocalName == "lexeme"))
        {
            var word = lexeme.Elements().FirstOrDefault(e => e.Name.LocalName == "grapheme")?.Value.Trim();
            var replacement = lexeme.Elements().FirstOrDefault(e => e.Name.LocalName == "phoneme")?.Value.Trim();
            if (!string.IsNullOrWhiteSpace(word) && !string.IsNullOrEmpty(replacement))
            {
                entries[word] = replacement;
            }
        }

        return entries;
    }
}
