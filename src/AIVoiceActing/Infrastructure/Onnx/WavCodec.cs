namespace AIVoiceActing.Infrastructure.Onnx;

/// Minimal RIFF WAV codec (own implementation, no NAudio): 16-bit PCM or 32-bit IEEE
/// float, mono @ 24 kHz only — the Chatterbox reference clips (the pinned HF default
/// voice ships as float32) and the rendered output contract. Write produces the canonical
/// 44-byte PCM16 header. Scaling: int16 sample / 32768 on read, sample * 32768 clamped on
/// write, so round-trips through the 1/32768 grid are exact; float samples pass through.
/// </summary>
public static class WavCodec
{
    public const int SampleRate = 24000;

    /// <summary>Reads a 16-bit PCM or 32-bit IEEE float mono 24 kHz WAV into floats in [-1, 1].</summary>
    public static float[] ReadMono24k(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var reader = new BinaryReader(stream);

        var riffId = reader.ReadBytes(4);
        if (riffId is not [(byte)'R', (byte)'I', (byte)'F', (byte)'F'])
        {
            throw new InvalidDataException($"{path}: not a RIFF file.");
        }

        _ = reader.ReadUInt32(); // riff chunk size (ignored; chunk walk is authoritative)
        var waveId = reader.ReadBytes(4);
        if (waveId is not [(byte)'W', (byte)'A', (byte)'V', (byte)'E'])
        {
            throw new InvalidDataException($"{path}: not a WAVE file.");
        }

        int? format = null, channels = null, sampleRate = null, bits = null;
        byte[]? data = null;

        while (stream.Position < stream.Length)
        {
            var chunkId = reader.ReadBytes(4);
            var chunkSize = reader.ReadUInt32();
            if (chunkId is [(byte)'f', (byte)'m', (byte)'t', (byte)' '])
            {
                format = reader.ReadUInt16();
                channels = reader.ReadUInt16();
                sampleRate = reader.ReadInt32();
                _ = reader.ReadUInt32(); // byte rate
                _ = reader.ReadUInt16(); // block align
                bits = reader.ReadUInt16();
                var extra = (int)chunkSize - 16;
                if (extra > 0)
                {
                    stream.Seek(extra, SeekOrigin.Current);
                }
            }
            else if (chunkId is [(byte)'d', (byte)'a', (byte)'t', (byte)'a'])
            {
                data = reader.ReadBytes((int)chunkSize);
            }
            else
            {
                stream.Seek(chunkSize + (chunkSize & 1), SeekOrigin.Current); // chunks are word-aligned
            }
        }

        switch (format, bits)
        {
            case (1, 16):
            case (3, 32):
                break;
            default:
                throw new InvalidDataException(
                    $"{path}: expected 16-bit PCM (format 1) or 32-bit IEEE float (format 3), " +
                    $"got {bits?.ToString() ?? "unknown"}-bit format {format?.ToString() ?? "none"}.");
        }

        if (channels is not 1)
        {
            throw new InvalidDataException($"{path}: expected mono, got {channels?.ToString() ?? "unknown"} channel(s).");
        }

        if (sampleRate is not SampleRate)
        {
            throw new InvalidDataException($"{path}: expected {SampleRate} Hz, got {sampleRate?.ToString() ?? "unknown"} Hz.");
        }

        if (data is null || data.Length == 0)
        {
            throw new InvalidDataException($"{path}: no audio data chunk.");
        }

        if (format == 3)
        {
            var floats = new float[data.Length / 4];
            for (var i = 0; i < floats.Length; i++)
            {
                floats[i] = BitConverter.ToSingle(data, i * 4);
            }

            return floats;
        }

        var samples = new float[data.Length / 2];
        for (var i = 0; i < samples.Length; i++)
        {
            samples[i] = BitConverter.ToInt16(data, i * 2) / 32768f;
        }

        return samples;
    }

    /// <summary>Writes mono floats (clipped to [-1, 1]) as 16-bit PCM 24 kHz with a canonical 44-byte header.</summary>
    public static void WriteMono24k(string path, ReadOnlySpan<float> samples)
    {
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        using var writer = new BinaryWriter(stream);

        var dataSize = samples.Length * 2;
        writer.Write("RIFF"u8);
        writer.Write(36 + dataSize);
        writer.Write("WAVE"u8);
        writer.Write("fmt "u8);
        writer.Write(16);          // fmt chunk size (PCM)
        writer.Write((short)1);    // PCM
        writer.Write((short)1);    // mono
        writer.Write(SampleRate);
        writer.Write(SampleRate * 2); // byte rate
        writer.Write((short)2);    // block align
        writer.Write((short)16);   // bits per sample
        writer.Write("data"u8);
        writer.Write(dataSize);
        foreach (var sample in samples)
        {
            var clamped = Math.Clamp(sample, -1f, 1f);
            // *32768 keeps the read-side /32768 round-trip exact on the quantization grid;
            // full-scale 1.0 clamps to short.MaxValue (a -90 dB edge case).
            writer.Write((short)Math.Clamp(MathF.Round(clamped * 32768f), -32768f, 32767f));
        }
    }
}
