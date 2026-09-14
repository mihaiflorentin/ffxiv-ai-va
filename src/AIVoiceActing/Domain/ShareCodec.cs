namespace AIVoiceActing.Domain;

using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

/// <summary>Thrown by <see cref="ShareCodec"/> when a share string cannot be decoded.</summary>
public sealed class ShareCodecException(string message, Exception? inner = null)
    : Exception(message, inner);

/// <summary>
/// Clipboard share codec for presets and voice-assignment dumps: camelCase JSON →
/// UTF-8 → gzip → base64, framed as "AIVA1:&lt;md5-8&gt;:&lt;base64&gt;". The eight-hex
/// checksum catches clipboard mangling and stale paste-buffer fragments before a parse
/// is attempted. Pure — clipboard I/O lives in the UI layer.
/// </summary>
public static class ShareCodec
{
    private const string Prefix = "AIVA1";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    /// <summary>Encodes <paramref name="payload"/> into the framed share string.</summary>
    public static string Encode<T>(T payload)
    {
        var json = JsonSerializer.Serialize(payload, JsonOptions);
        using var compressed = new MemoryStream();
        using (var gzip = new GZipStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
        {
            gzip.Write(new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(json));
        }

        var base64 = Convert.ToBase64String(compressed.ToArray());
        return $"{Prefix}:{Checksum(base64)}:{base64}";
    }

    /// <summary>Decodes a framed share string; a wrong prefix, checksum mismatch, or
    /// parse failure throws <see cref="ShareCodecException"/>.</summary>
    public static T Decode<T>(string encoded)
    {
        var parts = (encoded ?? string.Empty).Trim().Split(':');
        if (parts.Length != 3 || !string.Equals(parts[0], Prefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new ShareCodecException(
                $"Not an {Prefix} share string (expected \"{Prefix}:<checksum>:<payload>\").");
        }

        if (!string.Equals(parts[1], Checksum(parts[2]), StringComparison.OrdinalIgnoreCase))
        {
            throw new ShareCodecException("Share string checksum mismatch: the text was truncated or mangled in transit.");
        }

        byte[] compressedBytes;
        try
        {
            compressedBytes = Convert.FromBase64String(parts[2]);
        }
        catch (FormatException e)
        {
            throw new ShareCodecException("Share string payload is not valid base64.", e);
        }

        string json;
        try
        {
            using var decompressed = new MemoryStream(compressedBytes);
            using var gzip = new GZipStream(decompressed, CompressionMode.Decompress);
            json = new StreamReader(gzip, Encoding.UTF8).ReadToEnd();
        }
        catch (Exception e) when (e is IOException or InvalidDataException)
        {
            throw new ShareCodecException("Share string payload is not readable gzip data.", e);
        }

        try
        {
            return JsonSerializer.Deserialize<T>(json, JsonOptions)
                ?? throw new ShareCodecException("Share string decoded to null.");
        }
        catch (JsonException e)
        {
            throw new ShareCodecException($"Share string is not a valid {typeof(T).Name} payload.", e);
        }
        catch (ShareCodecException)
        {
            throw;
        }
    }

    private static string Checksum(string base64) =>
        Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(base64)))[..8].ToLowerInvariant();
}
