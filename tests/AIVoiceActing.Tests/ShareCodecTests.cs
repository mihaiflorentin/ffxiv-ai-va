namespace AIVoiceActing.Tests;

using AIVoiceActing.Domain;
using AIVoiceActing.Ports;
using Xunit;

public sealed class ShareCodecTests
{
    private static readonly CastingPreset Preset = new(
        Name: "Round Trip",
        Buckets: new Dictionary<string, VoiceSlotDto[]>(StringComparer.Ordinal)
        {
            ["6|Male"] =
            [
                new VoiceSlotDto("zm_yunjian", 0.25f, 1.1f, 0.95f, 1.5f),
                new VoiceSlotDto("zm_yunyang", 0f, 1.18f, 1.1f, 1f),
            ],
            [CastingDefaults.UnknownBucketKey] = [new VoiceSlotDto("pf_dora")],
        },
        BeastTribes:
        [
            new BeastTribeCast(
                "ixal",
                "Ixal",
                ModelIds: [1106],
                Voices: [new VoiceSlotDto("pm_santa", 0f, 1.08f, 1.1f, 1f)],
                MaleVoices: [],
                FemaleVoices: [new VoiceSlotDto("ef_dora")]),
        ]);

    [Fact]
    public void Encode_Decode_PreservesPreset()
    {
        var encoded = ShareCodec.Encode(Preset);
        Assert.StartsWith("AIVA1:", encoded);

        var decoded = ShareCodec.Decode<CastingPreset>(encoded);
        Assert.Equal(Preset.Name, decoded.Name);
        Assert.Equal(2, decoded.Buckets.Count);
        Assert.Equal(Preset.Buckets["6|Male"], decoded.Buckets["6|Male"]);
        Assert.Equal(Preset.Buckets[CastingDefaults.UnknownBucketKey], decoded.Buckets[CastingDefaults.UnknownBucketKey]);

        var tribe = Assert.Single(decoded.BeastTribes);
        Assert.Equal("ixal", tribe.Key);
        Assert.Equal("Ixal", tribe.Name);
        Assert.Equal([1106], tribe.ModelIds);
        Assert.Equal(Preset.BeastTribes[0].Voices, tribe.Voices);
        Assert.Empty(tribe.MaleVoices);
        Assert.Equal(Preset.BeastTribes[0].FemaleVoices, tribe.FemaleVoices);
    }

    [Fact]
    public void Encode_Decode_WorksForArbitraryPayloadShapes()
    {
        var shares = new List<AssignmentShareProbe>
        {
            new("npc:sibold", "bm_lewis", 0.1f, 1f, 1f, 1.2f),
            new("pc:mu hlack@1177", "zf_xiaoyi", 0f, 1.18f, 1.1f, 1f),
        };

        var decoded = ShareCodec.Decode<List<AssignmentShareProbe>>(ShareCodec.Encode(shares));

        Assert.Equal(2, decoded.Count);
        Assert.Equal(shares[0], decoded[0]);
        Assert.Equal(shares[1], decoded[1]);
    }

    private sealed record AssignmentShareProbe(
        string SpeakerKey,
        string ReferenceVoiceId,
        float ExaggerationBias,
        float Pitch,
        float Speed,
        float Volume);

    [Fact]
    public void Decode_FlippedChecksum_Throws()
    {
        var encoded = ShareCodec.Encode(Preset);
        var parts = encoded.Split(':');
        var checksum = parts[1] == "aaaaaaaa" ? "bbbbbbbb" : "aaaaaaaa";
        var tampered = $"{parts[0]}:{checksum}:{parts[2]}";

        var ex = Assert.Throws<ShareCodecException>(() => ShareCodec.Decode<CastingPreset>(tampered));
        Assert.Contains("checksum", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not a share string")]
    [InlineData("AIVA1:aaaaaaaa:!!!not base64!!!")]
    public void Decode_Garbage_ThrowsShareCodecException(string encoded)
    {
        Assert.ThrowsAny<ShareCodecException>(() => ShareCodec.Decode<CastingPreset>(encoded));
    }

    [Fact]
    public void Decode_TruncatedPayload_ThrowsChecksumError()
    {
        var encoded = ShareCodec.Encode(Preset);
        var truncated = encoded[..^20];

        Assert.Throws<ShareCodecException>(() => ShareCodec.Decode<CastingPreset>(truncated));
    }
}
