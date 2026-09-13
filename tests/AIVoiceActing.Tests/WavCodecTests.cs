namespace AIVoiceActing.Tests;

using AIVoiceActing.Infrastructure.Onnx;
using Xunit;

public sealed class WavCodecTests
{
    [Fact]
    public void RoundTrip_OnQuantizationGrid_IsExact()
    {
        var path = Path.Combine(Path.GetTempPath(), $"aiva-wav-{Guid.NewGuid():N}.wav");
        try
        {
            // Multiples of 1/32768: the int16 grid — read(side /32768) inverts write(*32768) exactly.
            float[] samples = [0f, 0.25f, -0.5f, 0.125f, 1f / 32768 * 32767, -1f];
            WavCodec.WriteMono24k(path, samples);
            var actual = WavCodec.ReadMono24k(path);
            Assert.Equal(samples, actual);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Write_ProducesCanonicalHeader()
    {
        var path = Path.Combine(Path.GetTempPath(), $"aiva-wav-{Guid.NewGuid():N}.wav");
        try
        {
            WavCodec.WriteMono24k(path, [0f, 0.5f]);
            var bytes = File.ReadAllBytes(path);
            Assert.Equal(44 + 4, bytes.Length);
            Assert.Equal("RIFF", System.Text.Encoding.ASCII.GetString(bytes, 0, 4));
            Assert.Equal(40u, BitConverter.ToUInt32(bytes, 4)); // 36 + dataSize
            Assert.Equal("WAVE", System.Text.Encoding.ASCII.GetString(bytes, 8, 4));
            Assert.Equal(24000, BitConverter.ToInt32(bytes, 24));
            Assert.Equal(16, (int)BitConverter.ToUInt16(bytes, 34)); // bits per sample
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Read_RejectsWrongSampleRate() =>
        RejectsCustom(rate: 44100, channels: 1, bits: 16, expectMessage: "44100");

    [Fact]
    public void Read_RejectsStereo() => RejectsCustom(rate: 24000, channels: 2, bits: 16);

    [Fact]
    public void Read_AcceptsFloat32()
    {
        var path = Path.Combine(Path.GetTempPath(), $"aiva-wav-{Guid.NewGuid():N}.wav");
        try
        {
            WriteFloat32(path, [0.25f, -0.5f, 1f]);
            Assert.Equal([0.25f, -0.5f, 1f], WavCodec.ReadMono24k(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Read_Rejects24BitPcm() =>
        RejectsCustom(rate: 24000, channels: 1, bits: 24, expectMessage: "expected 16-bit PCM");

    private static void RejectsCustom(int rate, int channels, int bits, short format = 1, string? expectMessage = null)
    {
        var path = Path.Combine(Path.GetTempPath(), $"aiva-wav-{Guid.NewGuid():N}.wav");
        try
        {
            WriteCustom(path, rate, channels, bits, format);
            var ex = Assert.Throws<InvalidDataException>(() => WavCodec.ReadMono24k(path));
            if (expectMessage is not null)
            {
                Assert.Contains(expectMessage, ex.Message);
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static void WriteCustom(string path, int rate, int channels, int bits, short format = 1)
    {
        using var writer = new BinaryWriter(File.Create(path));
        writer.Write("RIFF"u8);
        writer.Write(36 + 4);
        writer.Write("WAVE"u8);
        writer.Write("fmt "u8);
        writer.Write(16);
        writer.Write(format);
        writer.Write((short)channels);
        writer.Write(rate);
        writer.Write(rate * channels * bits / 8);
        writer.Write((short)(channels * bits / 8));
        writer.Write((short)bits);
        writer.Write("data"u8);
        writer.Write(4);
        writer.Write((short)0);
        writer.Write((short)0);
    }

    private static void WriteFloat32(string path, float[] samples)
    {
        using var writer = new BinaryWriter(File.Create(path));
        var dataSize = samples.Length * 4;
        writer.Write("RIFF"u8);
        writer.Write(36 + dataSize);
        writer.Write("WAVE"u8);
        writer.Write("fmt "u8);
        writer.Write(16);
        writer.Write((short)3); // IEEE float
        writer.Write((short)1); // mono
        writer.Write(24000);
        writer.Write(24000 * 4); // byte rate
        writer.Write((short)4); // block align
        writer.Write((short)32); // bits per sample
        writer.Write("data"u8);
        writer.Write(dataSize);
        foreach (var sample in samples)
        {
            writer.Write(sample);
        }
    }
}
