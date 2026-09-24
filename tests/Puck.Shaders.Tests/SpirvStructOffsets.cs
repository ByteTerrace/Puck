using System.Buffers.Binary;
using System.Text;

namespace Puck.Shaders.Tests;

/// <summary>Reads the member offsets DXC decorated a SPIR-V struct with, found by its debug name or as the pointee of the
/// module's push-constant variable.</summary>
internal static class SpirvStructOffsets {
    private const int DecorationOffset = 35;
    private const int OpMemberDecorate = 72;
    private const int OpName = 5;
    private const int OpTypePointer = 32;
    private const int OpVariable = 59;
    private const uint StorageClassPushConstant = 9;

    /// <summary>Returns the offsets of the struct named <paramref name="structName"/>, in member order.</summary>
    public static IReadOnlyList<uint> OfNamed(ReadOnlySpan<byte> module, string structName) {
        var words = Words(module: module);
        uint? structId = null;

        Walk(
            visit: (opcode, operands) => {
                if (
                    (opcode == OpName) &&
                    (operands.Length >= 2) &&
                    string.Equals(
                        a: LiteralString(words: operands[1..]),
                        b: structName,
                        comparisonType: StringComparison.Ordinal
                    )
                ) {
                    structId = operands[0];
                }
            },
            words: words
        );
        Assert.NotNull(value: structId);

        return Offsets(
            structId: structId.Value,
            words: words
        );
    }
    /// <summary>Returns the offsets of the push-constant block's members, in member order, or <see langword="null"/> when
    /// the module declares no push-constant variable.</summary>
    public static IReadOnlyList<uint>? OfPushConstants(ReadOnlySpan<byte> module) {
        var words = Words(module: module);
        var pointees = new Dictionary<uint, uint>();
        uint? pointerType = null;

        Walk(
            visit: (opcode, operands) => {
                if (
                    (opcode == OpTypePointer) &&
                    (operands.Length >= 3)
                ) {
                    pointees[operands[0]] = operands[2];
                } else if (
                    (opcode == OpVariable) &&
                    (operands.Length >= 3) &&
                    (operands[2] == StorageClassPushConstant)
                ) {
                    pointerType = operands[0];
                }
            },
            words: words
        );

        return ((pointerType is { } pointer)
            ? Offsets(
                structId: pointees[pointer],
                words: words
            )
            : null);
    }

    private static uint[] Words(ReadOnlySpan<byte> module) {
        var words = new uint[(module.Length / 4)];

        for (var index = 0; (index < words.Length); index++) {
            words[index] = BinaryPrimitives.ReadUInt32LittleEndian(source: module.Slice(
                length: 4,
                start: (index * 4)
            ));
        }

        return words;
    }
    private static void Walk(uint[] words, Action<int, uint[]> visit) {
        for (var cursor = 5; (cursor < words.Length);) {
            var wordCount = ((int)(words[cursor] >> 16));

            visit(
                arg1: ((int)(words[cursor] & 0xFFFF)),
                arg2: words[(cursor + 1)..(cursor + wordCount)]
            );
            cursor += wordCount;
        }
    }
    private static List<uint> Offsets(uint[] words, uint structId) {
        var offsets = new SortedDictionary<uint, uint>();

        Walk(
            visit: (opcode, operands) => {
                if (
                    (opcode == OpMemberDecorate) &&
                    (operands.Length >= 4) &&
                    (operands[0] == structId) &&
                    (operands[2] == DecorationOffset)
                ) {
                    offsets[operands[1]] = operands[3];
                }
            },
            words: words
        );

        return offsets.Values.ToList();
    }
    private static string LiteralString(uint[] words) {
        var builder = new StringBuilder();

        foreach (var word in words) {
            for (var shift = 0; (shift < 32); shift += 8) {
                var value = ((byte)(word >> shift));

                if (value == 0) {
                    return builder.ToString();
                }

                builder.Append(value: ((char)value));
            }
        }

        return builder.ToString();
    }
}
