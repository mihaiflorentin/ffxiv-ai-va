namespace AIVoiceActing.Domain;

using System.Text;

/// <summary>
/// Minimal reader for the header of a NumPy <c>.npy</c> file, enough to validate a Kokoro
/// style tensor (little-endian float32, shape 510x1x256) without loading it.
/// </summary>
public static class NpyHeader
{
    public const int StyleRows = 510;
    public const int StyleColumns = 256;

    /// <summary>Header facts: the dtype descriptor, the shape and the byte offset where data starts.</summary>
    public sealed record Info(string Descr, int[] Shape, int DataOffset);

    /// <summary>Parses the header; null when the bytes are not a readable npy v1/v2/v3 header.</summary>
    public static Info? Parse(ReadOnlySpan<byte> bytes)
    {
        // magic (6) + version (2) + header length (2 for v1, 4 for v2/v3)
        if (bytes.Length < 10 || bytes[0] != 0x93 || !bytes[1..6].SequenceEqual("NUMPY"u8))
        {
            return null;
        }

        var major = bytes[6];
        int lengthSize;
        long headerLength;
        if (major == 1)
        {
            lengthSize = 2;
            headerLength = bytes[8] | (bytes[9] << 8);
        }
        else if (major is 2 or 3)
        {
            if (bytes.Length < 12)
            {
                return null;
            }

            lengthSize = 4;
            headerLength = (long)bytes[8] | ((long)bytes[9] << 8) | ((long)bytes[10] << 16) | ((long)bytes[11] << 24);
        }
        else
        {
            return null;
        }

        var start = 8 + lengthSize;
        if (headerLength <= 0 || start + headerLength > bytes.Length)
        {
            return null;
        }

        var text = Encoding.Latin1.GetString(bytes.Slice(start, (int)headerLength));
        var descr = ValueOf(text, "'descr'");
        var shapeText = ShapeOf(text);
        if (descr is null || shapeText is null)
        {
            return null;
        }

        var shape = new List<int>();
        foreach (var part in shapeText.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!int.TryParse(part, out var dim) || dim < 0)
            {
                return null;
            }

            shape.Add(dim);
        }

        return new Info(descr, [.. shape], start + (int)headerLength);
    }

    /// <summary>Null when the header describes a Kokoro style tensor, else the reason it does not.</summary>
    public static string? ValidateStyleTensor(ReadOnlySpan<byte> bytes)
    {
        if (Parse(bytes) is not { } info)
        {
            return "not a readable .npy file";
        }

        if (info.Descr is not ("<f4" or "float32"))
        {
            return $"expected float32 data but found \"{info.Descr}\"";
        }

        return info.Shape.Length == 3 && info.Shape[0] == StyleRows && info.Shape[1] == 1 && info.Shape[2] == StyleColumns
            ? null
            : $"expected shape {StyleRows}x1x{StyleColumns} but found {string.Join("x", info.Shape)}";
    }

    private static string? ValueOf(string header, string key)
    {
        var at = header.IndexOf(key, StringComparison.Ordinal);
        if (at < 0)
        {
            return null;
        }

        var open = header.IndexOf('\'', at + key.Length);
        if (open < 0)
        {
            return null;
        }

        var close = header.IndexOf('\'', open + 1);
        return close < 0 ? null : header[(open + 1)..close];
    }

    private static string? ShapeOf(string header)
    {
        var at = header.IndexOf("'shape'", StringComparison.Ordinal);
        if (at < 0)
        {
            return null;
        }

        var open = header.IndexOf('(', at);
        var close = open < 0 ? -1 : header.IndexOf(')', open);
        return close < 0 ? null : header[(open + 1)..close];
    }
}
