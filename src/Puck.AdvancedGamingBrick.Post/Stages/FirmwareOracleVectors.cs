using System.Buffers.Binary;

namespace Puck.AdvancedGamingBrick.Post;

/// <summary>Original, valid-shape inputs described by the public GBA BIOS contracts; expected results come only from the local oracle.</summary>
internal static class FirmwareOracleVectors {
    // Field layouts and accepted widths: https://www.akkit.org/info/gbatek.htm#biosfunctions
    private static readonly (short X, short Y)[] Scales = [(1, 1), (256, 256), (257, -257), (0, short.MaxValue),
        (short.MinValue, 1), (short.MaxValue, short.MinValue), (-1, 1), (0x1234, -0x2345)];

    /// <summary>Sweeps every effective angle with signed scales, translated origins, overlapping and odd byte strides,
    /// and ignored fractional angle bits.</summary>
    /// <param name="comparison">The independent native machines and result collector.</param>
    /// <param name="thumb">Whether the caller invokes SWI in Thumb state.</param>
    public static void CheckAffine(FirmwareOracleComparison comparison, bool thumb) {
        for (var phase = 0; phase < 256; ++phase) {
            var background = new byte[Scales.Length * 20];
            var objects = new byte[Scales.Length * 8];
            for (var index = 0; index < Scales.Length; ++index) {
                var bg = background.AsSpan(start: index * 20, length: 20);
                BinaryPrimitives.WriteInt32LittleEndian(destination: bg, value: 0x123456 + index * 17);
                BinaryPrimitives.WriteInt32LittleEndian(destination: bg[4..], value: -0x234567 - index * 31);
                BinaryPrimitives.WriteInt16LittleEndian(destination: bg[8..], value: (short)(137 - index * 19));
                BinaryPrimitives.WriteInt16LittleEndian(destination: bg[10..], value: (short)(-49 + index * 13));
                var obj = objects.AsSpan(start: index * 8, length: 8);
                BinaryPrimitives.WriteInt16LittleEndian(destination: obj, value: Scales[index].X);
                BinaryPrimitives.WriteInt16LittleEndian(destination: obj[2..], value: Scales[index].Y);
                BinaryPrimitives.WriteUInt16LittleEndian(destination: obj[4..], value: (ushort)((phase << 8) | ((phase + index * 37) & 255)));
                obj[..6].CopyTo(destination: bg[12..]);
            }
            comparison.Memory(number: 0x0E, thumb: thumb, source: background, outputLength: Scales.Length * 16,
                name: $"BgAffine phase={phase:X2}, eight signed scale/origin records", r2: (uint)Scales.Length);
            // The offset is a raw byte stride, not a BG/OBJ selector. Zero repeatedly overwrites one halfword;
            // odd strides exercise the native halfword-store alignment rules, with canaries around the full extent.
            foreach (var stride in new uint[] { 0, 1, 2, 3, 8 }) {
                comparison.Memory(number: 0x0F, thumb: thumb, source: objects, outputLength: Math.Max(val1: 2, val2: Scales.Length * 4 * (int)stride),
                    name: $"ObjAffine phase={phase:X2}, eight signed scale records, stride={stride}", r2: (uint)Scales.Length, r3: stride);
            }
        }
    }

    /// <summary>Checks bias direction at, below and above the normal level, while retaining output-resolution bits.</summary>
    /// <param name="comparison">The independent native machines and result collector.</param>
    /// <param name="thumb">Whether the caller invokes SWI in Thumb state.</param>
    public static void CheckSoundBias(FirmwareOracleComparison comparison, bool thumb) {
        foreach (var initial in new ushort[] { 0, 0x100, 0x200, 0x3FE, 0xC000, 0xC100, 0xC200, 0xC3FE }) {
            foreach (var flag in new uint[] { 0, 1, uint.MaxValue }) {
                comparison.SoundBias(thumb: thumb, initial: initial, flag: flag);
            }
        }
    }

    /// <summary>Exercises aligned copies/fills, packed bit widths, and original valid compressed streams in writable RAM and VRAM.</summary>
    /// <param name="comparison">The independent native machines and result collector.</param>
    /// <param name="thumb">Whether the caller invokes SWI in Thumb state.</param>
    public static void CheckMemory(FirmwareOracleComparison comparison, bool thumb) {
        var data = new byte[256];
        for (var index = 0; index < data.Length; ++index) {
            data[index] = (byte)((index * 73) ^ (index >> 1));
        }
        foreach (var count in new uint[] { 1, 2, 7, 8, 9, 31, 64 }) {
            foreach (var word in new[] { false, true }) {
                foreach (var fill in new[] { false, true }) {
                    comparison.Memory(number: 11, thumb: thumb, source: data, outputLength: (int)count * (word ? 4 : 2),
                        name: $"CpuSet count={count}, word={word}, fill={fill}", r2: count | (word ? 1u << 26 : 0) | (fill ? 1u << 24 : 0));
                }
            }
        }
        foreach (var count in new uint[] { 8, 16, 64 }) {
            foreach (var fill in new[] { false, true }) {
                comparison.Memory(number: 12, thumb: thumb, source: data, outputLength: (int)count * 4,
                    name: $"CpuFastSet count={count}, fill={fill}", r2: count | (fill ? 1u << 24 : 0));
            }
        }
        foreach (var sourceWidth in new byte[] { 1, 2, 4, 8 }) {
            foreach (var destinationWidth in new byte[] { 1, 2, 4, 8, 16, 32 }) {
                if (destinationWidth < sourceWidth) { continue; }
                foreach (var offset in destinationWidth > sourceWidth ? new uint[] { 0, 1, 0x80000001 } : [0u]) {
                    byte[] info = [16, 0, sourceWidth, destinationWidth, 0, 0, 0, 0];
                    BinaryPrimitives.WriteUInt32LittleEndian(destination: info.AsSpan(start: 4), value: offset);
                    comparison.Memory(number: 16, thumb: thumb, source: data[..16], outputLength: 16 * destinationWidth / sourceWidth,
                        name: $"BitUnpack {sourceWidth}-to-{destinationWidth}, offset={offset:X8}", r2: FirmwareSwiProbe.Info, info: info);
                }
            }
        }
        foreach (var vram in new[] { false, true }) {
            comparison.Memory(number: (byte)(vram ? 0x12 : 0x11), thumb: thumb,
                source: Pad(source: [0x10, 64, 0, 0, 0x3C, 0xA5, 0x5A, 0xF0, 1, 0xF0, 1, 0xF0, 1, 0x50, 1]),
                outputLength: 64, name: "LZ77 overlapping distance-two runs", vram: vram);
            comparison.Memory(number: (byte)(vram ? 0x12 : 0x11), thumb: thumb, source: LiteralLz(data: data.AsSpan(start: 0, length: 72)),
                outputLength: 72, name: "LZ77 multi-flag literals", vram: vram);
            comparison.Memory(number: (byte)(vram ? 0x15 : 0x14), thumb: thumb,
                source: Pad(source: [0x30, 72, 0, 0, 2, 0x10, 0x20, 0x30, 0xBD, 0xA7, 4, 1, 2, 3, 4, 5]),
                outputLength: 72, name: "RL literals and a 64-byte run", vram: vram);
            var differential = new byte[68];
            differential[0] = 0x81;
            differential[1] = 64;
            data.AsSpan(start: 0, length: 64).CopyTo(destination: differential.AsSpan(start: 4));
            comparison.Memory(number: (byte)(vram ? 0x17 : 0x16), thumb: thumb, source: differential,
                outputLength: 64, name: "Diff8 modulo-byte carry", vram: vram);
            differential[0] = 0x82;
            comparison.Memory(number: 0x18, thumb: thumb, source: differential,
                outputLength: 64, name: "Diff16 modulo-halfword carry", vram: vram);
        }
        comparison.Memory(number: 0x13, thumb: thumb, source: [0x28, 32, 0, 0, 1, 0xC0, 0xA5, 0x5A, 0x78, 0x56, 0x34, 0x12],
            outputLength: 32, name: "Huffman byte symbols and a full 32-bit bitstream");
        comparison.Memory(number: 0x13, thumb: thumb, source: [0x24, 16, 0, 0, 1, 0xC0, 1, 15, 0x78, 0x56, 0x34, 0x12],
            outputLength: 16, name: "Huffman nibble symbols and word packing");
        comparison.Memory(number: 0x13, thumb: thumb, source: [0x28, 8, 0, 0, 3, 0x80, 0x41, 0xC0, 0x42, 0x43, 0, 0, 0, 0, 0xD0, 0x5A],
            outputLength: 8, name: "Huffman unequal-depth three-symbol tree");
    }

    private static byte[] LiteralLz(ReadOnlySpan<byte> data) {
        var stream = new List<byte> { 0x10, (byte)data.Length, 0, 0 };
        for (var index = 0; index < data.Length; ++index) {
            if (index % 8 == 0) { stream.Add(item: 0); }
            stream.Add(item: data[index]);
        }
        return Pad(source: stream.ToArray());
    }

    private static byte[] Pad(byte[] source) {
        var aligned = new byte[(source.Length + 3) & ~3];
        source.CopyTo(array: aligned, index: 0);
        return aligned;
    }
}
