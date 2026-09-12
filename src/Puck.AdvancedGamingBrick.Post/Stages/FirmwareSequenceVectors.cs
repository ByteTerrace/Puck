using System.Buffers.Binary;

namespace Puck.AdvancedGamingBrick.Post;

/// <summary>Small authored command streams with explicit musical control-flow expectations.</summary>
internal static class FirmwareSequenceVectors {
    // Public command semantics: https://raw.githubusercontent.com/ipatix/m4a2s/master/sappy%20(by%20Bregalad).txt
    // The older description disagrees about nested patterns; the optional retail run supplies independent
    // evidence for the bounded three-entry stack rather than treating that prose as a retail implementation.
    internal readonly record struct Point(byte Volume, byte Wait, byte Depth, byte Repeat, int Offset, bool Active = true);
    internal readonly record struct Vector(string Name, byte[] Program, Point[] Points);

    internal static IEnumerable<Vector> ControlFlow() {
        var jump = new byte[64];
        Put(image: jump, offset: 0, bytes: [0xBE, 11, 0x81, 0xB2]);
        Pointer(image: jump, offset: 4, target: 32);
        Put(image: jump, offset: 8, bytes: [0xBE, 99, 0xB1]); // Unreachable volume catches fallthrough.
        Put(image: jump, offset: 32, bytes: [0xBE, 27, 0x82, 0xBE, 45, 0x81, 0xB1]);
        yield return new(Name: "GOTO", Program: jump, Points: [new(11, 0, 0, 0, 3), new(27, 1, 0, 0, 35), new(27, 0, 0, 0, 35), new(45, 0, 0, 0, 38), new(45, 0, 0, 0, 39, false)]);

        foreach (var depth in new[] { 1, 3 }) {
            var pattern = new byte[128];
            pattern[0] = 0xB3;
            Pointer(image: pattern, offset: 1, target: 32);
            Put(image: pattern, offset: 5, bytes: [0xBE, 45, 0x81, 0xB1]);
            for (var level = 1; level <= depth; ++level) {
                var start = level * 32;
                if (level < depth) {
                    pattern[start] = 0xB3;
                    Pointer(image: pattern, offset: start + 1, target: start + 32);
                    pattern[start + 5] = 0xB4;
                } else {
                    Put(image: pattern, offset: start, bytes: [0xBE, 27, 0x81, 0xB4]);
                }
            }
            yield return new(Name: $"pattern-depth-{depth}", Program: pattern, Points: [new(27, 0, (byte)depth, 0, depth * 32 + 3), new(45, 0, 0, 0, 8), new(45, 0, 0, 0, 9, false)]);
        }

        foreach (var count in new byte[] { 0, 1, 3 }) {
            var repeat = new byte[32];
            Put(image: repeat, offset: 0, bytes: [0xBE, 11, 0x81, 0xBE, 27, 0x81, 0xB5, count]);
            Pointer(image: repeat, offset: 8, target: 3);
            Put(image: repeat, offset: 12, bytes: [0xBE, 45, 0x81, 0xB1]);
            var points = new List<Point> { new(11, 0, 0, 0, 3), new(27, 0, 0, 0, 6) };
            for (byte iteration = 1; iteration < (count == 0 ? 6 : count); ++iteration) {
                // Retail black-box control: an unbounded repeat never advances the finite-repeat counter.
                points.Add(item: new(27, 0, 0, count == 0 ? (byte)0 : iteration, 6));
            }
            if (count != 0) { points.Add(item: new(45, 0, 0, 0, 15)); points.Add(item: new(45, 0, 0, 0, 16, false)); }
            yield return new(Name: $"repeat-{count}", Program: repeat, Points: points.ToArray());
        }
        yield return new(Name: "pattern-end-without-call", Program: [0xB4, 0xBE, 33, 0x81, 0xB1], Points: [new(33, 0, 0, 0, 4), new(33, 0, 0, 0, 5, false)]);
    }

    internal static byte[] Note(byte command, byte extra = 0) => extra == 0
        ? [0xBD, 0, 0xBE, 127, 0xBF, 64, command, 60, 127, 0x88, 0xB1]
        : [0xBD, 0, 0xBE, 127, 0xBF, 64, command, 60, 127, extra, 0x88, 0xB1];

    private static void Put(byte[] image, int offset, ReadOnlySpan<byte> bytes) => bytes.CopyTo(destination: image.AsSpan(start: offset));
    private static void Pointer(byte[] image, int offset, int target) => BinaryPrimitives.WriteUInt32LittleEndian(
        destination: image.AsSpan(start: offset), value: FirmwareSequenceFixture.Program + (uint)target);
}
