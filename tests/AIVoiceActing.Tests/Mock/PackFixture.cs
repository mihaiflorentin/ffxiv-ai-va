namespace AIVoiceActing.Tests.Mock;

using System.Text;

/// <summary>Builds throwaway voice pack folders (manifest, style tensors, clips) under a temp directory.</summary>
public static class PackFixture
{
    public static string NewDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "aiva-voicepacks", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>A minimal valid npy with the given shape and float32 data.</summary>
    public static byte[] Npy(string descr = "<f4", params int[] shape)
    {
        if (shape.Length == 0)
        {
            shape = [510, 1, 256];
        }

        var shapeText = shape.Length == 1 ? $"({shape[0]},)" : $"({string.Join(", ", shape)})";
        var header = $"{{'descr': '{descr}', 'fortran_order': False, 'shape': {shapeText}, }}";
        var padded = header.PadRight((((10 + header.Length + 1 + 63) / 64) * 64) - 10 - 1) + "\n";
        var count = shape.Aggregate(1, (a, b) => a * b);
        var itemSize = descr.EndsWith("8") ? 8 : 4;
        using var stream = new MemoryStream();
        stream.Write([0x93, (byte)'N', (byte)'U', (byte)'M', (byte)'P', (byte)'Y', 1, 0]);
        stream.Write(BitConverter.GetBytes((ushort)padded.Length));
        stream.Write(Encoding.ASCII.GetBytes(padded));
        var data = new byte[count * itemSize];
        for (var i = 0; i + 3 < data.Length; i += itemSize)
        {
            BitConverter.GetBytes(0.01f * (i % 7)).CopyTo(data, i);
        }

        stream.Write(data);
        return stream.ToArray();
    }

    public static void WriteTensor(string packDir, string relative, string descr = "<f4", params int[] shape)
    {
        var path = Path.Combine(packDir, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, Npy(descr, shape));
    }

    public static void WriteFile(string packDir, string relative, string text = "x")
    {
        var path = Path.Combine(packDir, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    /// <summary>A manifest JSON with the given voices (id, style file) and an optional license override.</summary>
    public static string Manifest(
        string packId,
        IEnumerable<(string Id, string File)> voices,
        string accent = "en-IE",
        string sex = "male",
        string? phonemizer = "en-gb",
        string? license = "\"license\": { \"spdx\": \"CC-BY-SA-4.0\", \"attribution\": \"Test data\" },")
    {
        var phonemizerJson = phonemizer is null ? string.Empty : $", \"phonemizer\": \"{phonemizer}\"";
        var voiceJson = string.Join(
            ",",
            voices.Select(v => $$"""
                { "id": "{{v.Id}}", "name": "{{v.Id}} name", "accent": "{{accent}}", "sex": "{{sex}}",
                  "quality": "experimental",
                  "realizations": [ { "kind": "StyleTensor", "file": "{{v.File}}"{{phonemizerJson}} } ] }
                """));
        return $$"""
            { "schema": 1, "id": "{{packId}}", "version": "1.0.0", "name": "{{packId}} pack",
              {{license}}
              "voices": [ {{voiceJson}} ] }
            """;
    }

    /// <summary>Writes a pack folder under <paramref name="root"/> with one valid tensor per voice.</summary>
    public static string WritePack(string root, string packId, params string[] voiceIds)
    {
        var dir = Path.Combine(root, packId);
        Directory.CreateDirectory(dir);
        foreach (var id in voiceIds)
        {
            WriteTensor(dir, $"style/{id}.npy");
        }

        File.WriteAllText(
            Path.Combine(dir, "voicepack.json"),
            Manifest(packId, voiceIds.Select(id => (id, $"style/{id}.npy"))));
        return dir;
    }
}
