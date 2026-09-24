using System.Buffers.Binary;
using System.Security.Cryptography;
using Puck.Assets.Textures;
using Xunit;

namespace Puck.Assets.Tests;

/// <summary>
/// Laws for the block codecs in <c>Puck.Assets.Textures</c>. Each encoder is held by its own decoder, the test oracle: a
/// representable block (one value, or two endpoints the format holds exactly) round-trips exactly; natural content (a
/// seeded image of gradients, noise and hard edges) decodes within a stated bound; and encoding is a function of its
/// input, pinned by the SHA-256 of a fixed image's blocks, so a machine whose encoder wrote other bytes fails here. The
/// decoders read hand-built blocks field by field as the formats lay them out, and refuse the modes they do not read.
/// </summary>
public sealed class TextureCodecLawTests {
    private const int Side = 64;

    // A deterministic generator for test content: xorshift32.
    private sealed class Seeded(uint seed) {
        private uint m_state = seed;

        public uint Next() {
            m_state ^= (m_state << 13);
            m_state ^= (m_state >> 17);
            m_state ^= (m_state << 5);

            return m_state;
        }
        public int Next(int bound) =>
            ((int)(Next() % ((uint)bound)));
    }

    // A Side-square image of `channels` bytes per texel: smooth gradients, low-amplitude noise, and a hard-edged disc of
    // another color.
    private static byte[] Natural(int channels, uint seed) {
        var random = new Seeded(seed: seed);
        var texels = new byte[((Side * Side) * channels)];

        for (var y = 0; (y < Side); y++) {
            for (var x = 0; (x < Side); x++) {
                var inside = ((((x - 40) * (x - 40)) + ((y - 24) * (y - 24))) < 196);

                for (var channel = 0; (channel < channels); channel++) {
                    var gradient = ((x * (1 + channel)) + (y * (3 - channel)));
                    var value = (inside ? (255 - (channel * 60)) : (gradient + (random.Next(bound: 9) - 4)));

                    texels[((((y * Side) + x) * channels) + channel)] = ((byte)Math.Clamp(max: 255, min: 0, value: value));
                }
            }
        }

        return texels;
    }
    // Half bits of an HDR image as emission makes one: a warm color whose strength ramps from 0 to about 24 with noise,
    // and a disc of a cool color at strength 60.
    private static byte[] NaturalHdr(uint seed) {
        var random = new Seeded(seed: seed);
        var texels = new byte[((Side * Side) * 8)];

        for (var y = 0; (y < Side); y++) {
            for (var x = 0; (x < Side); x++) {
                var inside = ((((x - 20) * (x - 20)) + ((y - 44) * (y - 44))) < 100);

                for (var channel = 0; (channel < 4); channel++) {
                    var strength = (inside ? 60.0 : ((((x + y) / 5.0) * (1.0 + (random.Next(bound: 100) / 1000.0))) + 0.001));
                    var chroma = (inside ? ((double[])[0.2, 0.5, 1.0, 1.0]) : ((double[])[1.0, 0.6, 0.3, 1.0]))[channel];
                    var value = ((channel == 3) ? 1.0 : (strength * chroma));

                    BinaryPrimitives.WriteUInt16LittleEndian(destination: texels.AsSpan(start: ((((y * Side) + x) * 8) + (channel * 2))), value: BitConverter.HalfToUInt16Bits(value: ((Half)value)));
                }
            }
        }

        return texels;
    }
    private static string Sha(byte[] bytes) =>
        Convert.ToHexStringLower(inArray: SHA256.HashData(source: bytes));
    // The largest absolute difference, and the root-mean-square difference, between two byte images.
    private static (int Maximum, double Rms) Difference(byte[] expected, byte[] actual) {
        var maximum = 0;
        var sum = 0.0;

        for (var index = 0; (index < expected.Length); index++) {
            var difference = Math.Abs(value: (expected[index] - actual[index]));

            maximum = Math.Max(val1: maximum, val2: difference);
            sum += (difference * difference);
        }

        return (maximum, Math.Sqrt(d: (sum / expected.Length)));
    }

    [Fact]
    public void Bc4RoundTripsRepresentableBlocksExactlyAndStaysWithinItsBound() {
        Span<byte> block = stackalloc byte[Bc4Codec.BlockBytes];
        Span<byte> decoded = stackalloc byte[16];
        Span<byte> again = stackalloc byte[16];
        Span<byte> values = stackalloc byte[16];
        Span<byte> palette = stackalloc byte[8];
        var random = new Seeded(seed: 17);

        for (var value = 0; (value < 256); value++) {
            values.Fill(value: ((byte)value));
            Bc4Codec.EncodeBlock(block: block, values: values);
            Bc4Codec.DecodeBlock(block: block, values: decoded);
            Assert.True(condition: decoded.SequenceEqual(other: values), userMessage: $"uniform {value}");
        }

        // A block decoded from endpoints e0 > e1 that uses both of them is representable: it re-encodes exactly.
        for (var trial = 0; (trial < 2000); trial++) {
            var high = ((byte)(1 + random.Next(bound: 255)));
            var low = ((byte)random.Next(bound: high));

            Bc4Codec.Palette(e0: high, e1: low, palette: palette);

            for (var texel = 0; (texel < 16); texel++) {
                values[texel] = palette[((texel < 2) ? texel : random.Next(bound: 8))];
            }

            Bc4Codec.EncodeBlock(block: block, values: values);
            Bc4Codec.DecodeBlock(block: block, values: again);
            Assert.True(condition: again.SequenceEqual(other: values), userMessage: $"trial {trial}");

            // Any block stays within (max - min) / 10 + 1.
            for (var texel = 0; (texel < 16); texel++) {
                values[texel] = ((byte)random.Next(bound: 256));
            }

            Bc4Codec.EncodeBlock(block: block, values: values);
            Bc4Codec.DecodeBlock(block: block, values: decoded);

            var spread = (values.ToArray().Max() - values.ToArray().Min());

            for (var texel = 0; (texel < 16); texel++) {
                Assert.True(condition: (Math.Abs(value: (decoded[texel] - values[texel])) <= ((spread / 10) + 1)), userMessage: $"trial {trial} texel {texel}");
            }
        }
    }
    [Fact]
    public void Bc4DecodesItsTwoPalettesAsTheFormatDefines() {
        Span<byte> palette = stackalloc byte[8];

        // e0 > e1: six values between, each the nearest integer to the exact interpolation.
        Bc4Codec.Palette(e0: 200, e1: 10, palette: palette);
        Assert.Equal(expected: [200, 10, 173, 146, 119, 91, 64, 37], actual: palette.ToArray());
        // e0 <= e1: four values between, then 0 and 255.
        Bc4Codec.Palette(e0: 10, e1: 200, palette: palette);
        Assert.Equal(expected: [10, 200, 48, 86, 124, 162, 0, 255], actual: palette.ToArray());

        // Texel i's 3-bit index sits at bit 3 i of bytes 2 to 7: here texel 0 is 1, texel 1 is 7, texel 15 is 5.
        var indices = (1UL | (7UL << 3)) | (5UL << 45);
        byte[] block = [200, 10, ((byte)indices), ((byte)(indices >> 8)), ((byte)(indices >> 16)), ((byte)(indices >> 24)), ((byte)(indices >> 32)), ((byte)(indices >> 40))];
        Span<byte> decoded = stackalloc byte[16];

        Bc4Codec.DecodeBlock(block: block, values: decoded);
        Assert.Equal(expected: ((byte)10), actual: decoded[0]);
        Assert.Equal(expected: ((byte)37), actual: decoded[1]);
        Assert.Equal(expected: ((byte)200), actual: decoded[2]);
        Assert.Equal(expected: ((byte)91), actual: decoded[15]);
    }
    [Fact]
    public void Bc7RoundTripsUniformAndTwoColorBlocksExactly() {
        Span<byte> block = stackalloc byte[Bc7Codec.BlockBytes];
        Span<byte> decoded = stackalloc byte[64];
        var rgba = new byte[64];
        Span<byte> first = stackalloc byte[4];
        Span<byte> second = stackalloc byte[4];
        var random = new Seeded(seed: 29);

        // Every value of every channel, independently, as a uniform block.
        for (var value = 0; (value < 256); value++) {
            for (var texel = 0; (texel < 16); texel++) {
                rgba[(texel * 4)] = ((byte)value);
                rgba[((texel * 4) + 1)] = ((byte)(255 - value));
                rgba[((texel * 4) + 2)] = ((byte)((value * 7) & 255));
                rgba[((texel * 4) + 3)] = ((byte)((value * 13) & 255));
            }

            Bc7Codec.EncodeBlock(block: block, rgba: rgba);
            Bc7Codec.DecodeBlock(block: block, rgba: decoded);
            Assert.True(condition: decoded.SequenceEqual(other: rgba), userMessage: $"uniform {value}");
        }

        // Two mode-6 endpoints (seven bits and a parity bit each) are representable exactly.
        for (var trial = 0; (trial < 2000); trial++) {
            var p0 = random.Next(bound: 2);
            var p1 = random.Next(bound: 2);

            for (var channel = 0; (channel < 4); channel++) {
                first[channel] = ((byte)((random.Next(bound: 128) << 1) | p0));
                second[channel] = ((byte)((random.Next(bound: 128) << 1) | p1));
            }

            for (var texel = 0; (texel < 16); texel++) {
                (((texel % 3) == 0) ? first : second).CopyTo(destination: rgba.AsSpan(start: (texel * 4)));
            }

            Bc7Codec.EncodeBlock(block: block, rgba: rgba);
            Bc7Codec.DecodeBlock(block: block, rgba: decoded);
            Assert.True(condition: decoded.SequenceEqual(other: rgba), userMessage: $"trial {trial}");
        }
    }
    [Fact]
    public void Bc7DecodesHandBuiltBlocksFieldByField() {
        Span<byte> decoded = stackalloc byte[64];
        var mode6 = new BitWriter();

        // Mode 6: bit 6; endpoints R0 R1 G0 G1 B0 B1 A0 A1 of seven bits; parity bits P0 P1; texel 0's index in three
        // bits, the rest in four.
        mode6.Write(count: 7, value: 0x40);

        foreach (var value in ((int[])[127, 0, 0, 127, 64, 64, 127, 127])) {
            mode6.Write(count: 7, value: value);
        }

        mode6.Write(count: 1, value: 1);
        mode6.Write(count: 1, value: 0);
        mode6.Write(count: 3, value: 0);
        mode6.Write(count: 4, value: 15);
        mode6.Write(count: 4, value: 8);
        Bc7Codec.DecodeBlock(block: mode6.Bytes, rgba: decoded);
        Assert.Equal(expected: [255, 1, 129, 255], actual: decoded[..4].ToArray());
        Assert.Equal(expected: [0, 254, 128, 254], actual: decoded[4..8].ToArray());
        // Weight 34: ((64 - 34) 255 + 34 0 + 32) >> 6 = 120, and so on per channel.
        Assert.Equal(expected: [120, 135, 128, 254], actual: decoded[8..12].ToArray());

        // Mode 5 with rotation 1 (alpha and red swap after decoding): R0 = 127 replicates to 255, alpha 16.
        var mode5 = new BitWriter();

        mode5.Write(count: 6, value: 0x20);
        mode5.Write(count: 2, value: 1);

        foreach (var value in ((int[])[127, 127, 0, 0, 64, 64])) {
            mode5.Write(count: 7, value: value);
        }

        mode5.Write(count: 8, value: 16);
        mode5.Write(count: 8, value: 16);
        Bc7Codec.DecodeBlock(block: mode5.Bytes, rgba: decoded);
        Assert.Equal(expected: [16, 0, 129, 255], actual: decoded[..4].ToArray());

        // A zero first byte is not a mode: transparent black. A partitioned mode is refused.
        Bc7Codec.DecodeBlock(block: new byte[16], rgba: decoded);
        Assert.All(collection: decoded.ToArray(), action: value => Assert.Equal(actual: value, expected: 0));

        var partitioned = new byte[16];

        partitioned[0] = 0x02;
        Assert.Throws<NotSupportedException>(testCode: () => Bc7Codec.DecodeBlock(block: partitioned, rgba: new byte[64]));
    }
    [Fact]
    public void Bc6hRoundTripsUniformAndTenBitTwoEndpointBlocksExactly() {
        Span<byte> block = stackalloc byte[Bc6hCodec.BlockBytes];
        Span<ushort> decoded = stackalloc ushort[48];
        var rgb = new ushort[48];
        Span<ushort> first = stackalloc ushort[3];
        Span<ushort> second = stackalloc ushort[3];
        var random = new Seeded(seed: 43);

        // Every finite non-negative half, as a uniform block.
        for (var value = 0; (value <= Bc6hCodec.MaximumHalf); value++) {
            for (var index = 0; (index < 48); index++) {
                rgb[index] = ((ushort)(((index % 3) == 1) ? (Bc6hCodec.MaximumHalf - value) : value));
            }

            Bc6hCodec.EncodeBlock(block: block, rgb: rgb);
            Bc6hCodec.DecodeBlock(block: block, rgb: decoded);
            Assert.True(condition: decoded.SequenceEqual(other: rgb), userMessage: $"uniform {value}");
        }

        // Two 10-bit mode-11 endpoints finish to halves the format holds exactly.
        for (var trial = 0; (trial < 2000); trial++) {
            for (var channel = 0; (channel < 3); channel++) {
                first[channel] = Finished(q: random.Next(bound: 1024));
                second[channel] = Finished(q: random.Next(bound: 1024));
            }

            for (var texel = 0; (texel < 16); texel++) {
                (((texel % 5) < 2) ? first : second).CopyTo(destination: rgb.AsSpan(start: (texel * 3)));
            }

            Bc6hCodec.EncodeBlock(block: block, rgb: rgb);
            Bc6hCodec.DecodeBlock(block: block, rgb: decoded);
            Assert.True(condition: decoded.SequenceEqual(other: rgb), userMessage: $"trial {trial}");
        }

        // A negative half stores as zero and one past 65504 as 65504.
        Assert.Equal(expected: 0, actual: Bc6hCodec.Clamp(half: BitConverter.HalfToUInt16Bits(value: ((Half)(-2.0)))));
        Assert.Equal(expected: Bc6hCodec.MaximumHalf, actual: Bc6hCodec.Clamp(half: BitConverter.HalfToUInt16Bits(value: Half.PositiveInfinity)));
    }

    // The half a 10-bit endpoint finishes to: unquantized ((q << 16) + 0x8000) >> 10 (0 and 1023 to 0 and 0xFFFF), times
    // 31, shifted right by 6.
    private static ushort Finished(int q) {
        var unquantized = ((q == 0) ? 0 : ((q == 1023) ? 0xFFFF : (((q << 16) + 0x8000) >> 10)));

        return ((ushort)((unquantized * 31) >> 6));
    }

    [Fact]
    public void Bc6hDecodesHandBuiltBlocksFieldByField() {
        Span<ushort> decoded = stackalloc ushort[48];

        // Mode 11: code 00011, then r0 g0 b0 r1 g1 b1 of ten bits, then the indices.
        var mode11 = new BitWriter();

        mode11.Write(count: 5, value: 0x03);

        foreach (var value in ((int[])[0, 512, 1023, 1023, 512, 0])) {
            mode11.Write(count: 10, value: value);
        }

        mode11.Write(count: 3, value: 0);
        mode11.Write(count: 4, value: 15);
        Bc6hCodec.DecodeBlock(block: mode11.Bytes, rgb: decoded);
        Assert.Equal(expected: [0, Finished(q: 512), 0x7BFF], actual: decoded[..3].ToArray());
        Assert.Equal(expected: [0x7BFF, Finished(q: 512), 0], actual: decoded[3..6].ToArray());

        // Mode 14: code 01111; the base's low ten bits, then per channel a 4-bit delta and the base's bits 15 down to 10.
        var mode14 = new BitWriter();
        const int Base = 0xABCD;

        mode14.Write(count: 5, value: 0x0F);

        for (var channel = 0; (channel < 3); channel++) {
            mode14.Write(count: 10, value: Base & 0x3FF);
        }

        for (var channel = 0; (channel < 3); channel++) {
            mode14.Write(count: 4, value: ((channel == 1) ? 0xF : 0x1));

            for (var bit = 15; (bit >= 10); bit--) {
                mode14.Write(count: 1, value: (Base >> bit) & 1);
            }
        }

        mode14.Write(count: 3, value: 0);
        mode14.Write(count: 4, value: 15);
        Bc6hCodec.DecodeBlock(block: mode14.Bytes, rgb: decoded);
        Assert.Equal(expected: ((ushort)((Base * 31) >> 6)), actual: decoded[0]);
        Assert.Equal(expected: ((ushort)(((Base + 1) * 31) >> 6)), actual: decoded[3]);
        Assert.Equal(expected: ((ushort)(((Base - 1) * 31) >> 6)), actual: decoded[4]);

        // A reserved mode decodes to zero; a two-region mode is refused.
        var reserved = new BitWriter();

        reserved.Write(count: 5, value: 0x13);
        Bc6hCodec.DecodeBlock(block: reserved.Bytes, rgb: decoded);
        Assert.All(collection: decoded.ToArray(), action: value => Assert.Equal(actual: value, expected: 0));
        Assert.Throws<NotSupportedException>(testCode: () => Bc6hCodec.DecodeBlock(block: new byte[16], rgb: new ushort[48]));
        Assert.Throws<NotSupportedException>(testCode: () => Bc6hCodec.DecodeBlock(block: [0x02, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0], rgb: new ushort[48]));
    }
    [Fact]
    public void NaturalContentDecodesWithinTheStatedBounds() {
        // BC4 and BC5 per channel, BC7 over RGBA; bounds in 8-bit codes.
        var single = Natural(channels: 1, seed: 3);
        var bc4 = Difference(actual: TextureCompression.Decode(blocks: TextureCompression.Encode(format: TextureFormat.Bc4Unorm, height: Side, texels: single, width: Side), format: TextureFormat.Bc4Unorm, height: Side, width: Side), expected: single);
        var pair = Natural(channels: 2, seed: 5);
        var bc5 = Difference(actual: TextureCompression.Decode(blocks: TextureCompression.Encode(format: TextureFormat.Bc5Unorm, height: Side, texels: pair, width: Side), format: TextureFormat.Bc5Unorm, height: Side, width: Side), expected: pair);
        var color = Natural(channels: 4, seed: 7);
        var bc7 = Difference(actual: TextureCompression.Decode(blocks: TextureCompression.Encode(format: TextureFormat.Bc7Unorm, height: Side, texels: color, width: Side), format: TextureFormat.Bc7Unorm, height: Side, width: Side), expected: color);

        // BC6H: relative error, the decoded value's distance from the source over the source, for every texel above
        // 1/64; separately over the blocks whose every channel spans at most a factor of four, and over all blocks.
        var hdr = NaturalHdr(seed: 11);
        var decoded = TextureCompression.Decode(blocks: TextureCompression.Encode(format: TextureFormat.Bc6hUfloat, height: Side, texels: hdr, width: Side), format: TextureFormat.Bc6hUfloat, height: Side, width: Side);

        double Value(byte[] texels, int x, int y, int channel) =>
            ((double)BitConverter.UInt16BitsToHalf(value: BinaryPrimitives.ReadUInt16LittleEndian(source: texels.AsSpan(start: ((((y * Side) + x) * 8) + (channel * 2))))));
        var (smooth, all) = (0.0, 0.0);

        for (var block = 0; (block < ((Side / 4) * (Side / 4))); block++) {
            var (bx, by) = (((block % (Side / 4)) * 4), ((block / (Side / 4)) * 4));
            var span = 1.0;

            for (var channel = 0; (channel < 3); channel++) {
                var (low, high) = (double.MaxValue, 0.0);

                for (var texel = 0; (texel < 16); texel++) {
                    var value = Value(channel: channel, texels: hdr, x: (bx + (texel & 3)), y: (by + (texel >> 2)));

                    (low, high) = (Math.Min(val1: low, val2: value), Math.Max(val1: high, val2: value));
                }

                span = Math.Max(val1: span, val2: (high / Math.Max(val1: low, val2: (1.0 / 64.0))));
            }

            for (var texel = 0; (texel < 16); texel++) {
                var (x, y) = ((bx + (texel & 3)), (by + (texel >> 2)));

                for (var channel = 0; (channel < 3); channel++) {
                    var source = Value(channel: channel, texels: hdr, x: x, y: y);

                    if (source > (1.0 / 64.0)) {
                        var error = (Math.Abs(value: (Value(channel: channel, texels: decoded, x: x, y: y) - source)) / source);

                        all = Math.Max(val1: all, val2: error);
                        smooth = ((span <= 4.0) ? Math.Max(val1: smooth, val2: error) : smooth);
                    }
                }

                Assert.Equal(expected: ((ushort)0x3C00), actual: BinaryPrimitives.ReadUInt16LittleEndian(source: decoded.AsSpan(start: ((((y * Side) + x) * 8) + 6))));
            }
        }

        var figures = $"BC4: max {bc4.Maximum}, rms {bc4.Rms:F3}; BC5: max {bc5.Maximum}, rms {bc5.Rms:F3}; BC7: max {bc7.Maximum}, rms {bc7.Rms:F3}; BC6H: worst relative {smooth:F4} in smooth blocks, {all:F4} in all";

        // The figures these bounds hold, with margin: BC4 2 and 0.63, BC5 8 and 0.88, BC7 13 and 2.32, BC6H 0.078 and
        // 0.233. BC6H's larger figure is a block across the disc's edge, two colors off one line that a one-region mode
        // cannot hold both of.
        Assert.True(condition: ((bc4.Maximum <= 3) && (bc4.Rms <= 0.8)), userMessage: figures);
        Assert.True(condition: ((bc5.Maximum <= 10) && (bc5.Rms <= 1.0)), userMessage: figures);
        Assert.True(condition: ((bc7.Maximum <= 16) && (bc7.Rms <= 2.6)), userMessage: figures);
        Assert.True(condition: ((smooth <= 0.1) && (all <= 0.3)), userMessage: figures);
    }
    [Fact]
    public void EncodingIsAFunctionOfItsInputPinnedToItsBytes() {
        var single = Natural(channels: 1, seed: 3);
        var pair = Natural(channels: 2, seed: 5);
        var color = Natural(channels: 4, seed: 7);
        var hdr = NaturalHdr(seed: 11);
        var encoded = new[] {
            TextureCompression.Encode(format: TextureFormat.Bc4Unorm, height: Side, texels: single, width: Side),
            TextureCompression.Encode(format: TextureFormat.Bc5Unorm, height: Side, texels: pair, width: Side),
            TextureCompression.Encode(format: TextureFormat.Bc6hUfloat, height: Side, texels: hdr, width: Side),
            TextureCompression.Encode(format: TextureFormat.Bc7Unorm, height: Side, texels: color, width: Side),
        };

        Assert.Equal(expected: encoded[0], actual: TextureCompression.Encode(format: TextureFormat.Bc4Unorm, height: Side, texels: single, width: Side));
        Assert.Equal(expected: encoded[3], actual: TextureCompression.Encode(format: TextureFormat.Bc7Unorm, height: Side, texels: color, width: Side));
        var actual = string.Join(separator: ' ', values: encoded.Select(selector: Sha));

        Assert.True(
            condition: (actual == string.Join(separator: ' ', values: [PinnedBc4, PinnedBc5, PinnedBc6h, PinnedBc7])),
            userMessage: $"the encoders wrote BC4, BC5, BC6H and BC7 blocks hashing to {actual}"
        );
    }

    // The SHA-256 of each encoder's blocks for the fixed images above. An encoder change that moves them re-records them
    // and moves SdfBaker.Version, since every bake's textures are these encoders' bytes.
    private const string PinnedBc4 = "57808ae1feae7b5e01d78d2bc225e447b62dbcebca2f5cbd494a60bb688d813f";
    private const string PinnedBc5 = "d1b9dfff4236b5b78d3bb3b6af70151346eafd5a99f578aea6f5f41e64e9e816";
    private const string PinnedBc6h = "2dd7cc2f50cb071801f8474d4065bfb4c38c222ab97c90b2b8cd8d13b3ce245c";
    private const string PinnedBc7 = "53a5549fe2a35636eb24e4d5110bfbfc59c1a86e2bb3be0e2f45040445a3bdeb";

    [Fact]
    public void APartialEdgeBlockRepeatsTheEdgeAndDecodesOnlyTheLevel() {
        var texels = Natural(channels: 4, seed: 13).AsSpan(length: ((6 * 5) * 4), start: 0).ToArray();
        var blocks = TextureCompression.Encode(format: TextureFormat.Bc7Unorm, height: 5, texels: texels, width: 6);

        Assert.Equal(expected: ((2 * 2) * Bc7Codec.BlockBytes), actual: blocks.Length);
        Assert.Equal(expected: texels.Length, actual: TextureCompression.Decode(blocks: blocks, format: TextureFormat.Bc7Unorm, height: 5, width: 6).Length);
        Assert.Throws<ArgumentException>(testCode: () => TextureCompression.Encode(format: TextureFormat.Bc7Unorm, height: 5, texels: texels.AsSpan(start: 1).ToArray(), width: 6));
        Assert.Throws<ArgumentException>(testCode: () => TextureCompression.Encode(format: TextureFormat.Rgba8Unorm, height: 5, texels: texels, width: 6));
        Assert.Equal(expected: 16L, actual: TextureFormats.LevelBytes(format: TextureFormat.Bc7Unorm, height: 1, width: 1));
        Assert.Equal(expected: 8L, actual: TextureFormats.LevelBytes(format: TextureFormat.Bc4Unorm, height: 2, width: 3));
    }

    // Writes fields least significant bit first, as BC6H and BC7 lay them out, independently of the codecs' own writer.
    private sealed class BitWriter {
        private readonly byte[] m_bytes = new byte[16];

        private int m_position;

        public byte[] Bytes => m_bytes;

        public void Write(int value, int count) {
            for (var bit = 0; (bit < count); bit++) {
                if (((value >> bit) & 1) != 0) {
                    m_bytes[(m_position >> 3)] |= ((byte)(1 << (m_position & 7)));
                }

                m_position++;
            }
        }
    }
}
