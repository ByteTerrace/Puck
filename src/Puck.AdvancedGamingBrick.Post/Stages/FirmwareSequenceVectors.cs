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

        Put(
            bytes: [0xBE, 11, 0x81, 0xB2],
            image: jump,
            offset: 0
        );
        Pointer(
            image: jump,
            offset: 4,
            target: 32
        );
        Put(
            bytes: [0xBE, 99, 0xB1],
            image: jump,
            offset: 8
        ); // Unreachable volume catches fallthrough.
        Put(
            bytes: [0xBE, 27, 0x82, 0xBE, 45, 0x81, 0xB1],
            image: jump,
            offset: 32
        );
        yield return new(
            Name: "GOTO",
            Program: jump,
            Points: [new(
                    11,
                    0,
                    0,
                    0,
                    3
                ), new(
                    27,
                    1,
                    0,
                    0,
                    35
                ), new(
                    27,
                    0,
                    0,
                    0,
                    35
                ), new(
                    45,
                    0,
                    0,
                    0,
                    38
                ), new(
                    Active: false,
                    Depth: 0,
                    Offset: 39,
                    Repeat: 0,
                    Volume: 45,
                    Wait: 0
                )]
        );

        foreach (var depth in new[] { 1, 3 }) {
            var pattern = new byte[128];

            pattern[0] = 0xB3;
            Pointer(
                image: pattern,
                offset: 1,
                target: 32
            );
            Put(
                bytes: [0xBE, 45, 0x81, 0xB1],
                image: pattern,
                offset: 5
            );
            for (var level = 1; (level <= depth); ++level) {
                var start = (level * 32);

                if (level < depth) {
                    pattern[start] = 0xB3;
                    Pointer(
                        image: pattern,
                        offset: (start + 1),
                        target: (start + 32)
                    );
                    pattern[(start + 5)] = 0xB4;
                } else {
                    Put(
                        bytes: [0xBE, 27, 0x81, 0xB4],
                        image: pattern,
                        offset: start
                    );
                }
            }
            yield return new(
                Name: $"pattern-depth-{depth}",
                Program: pattern,
                Points: [new(
                        27,
                        0,
                        ((byte)depth),
                        0,
                        ((depth * 32) + 3)
                    ), new(
                        45,
                        0,
                        0,
                        0,
                        8
                    ), new(
                        Active: false,
                        Depth: 0,
                        Offset: 9,
                        Repeat: 0,
                        Volume: 45,
                        Wait: 0
                    )]
            );
        }

        foreach (var count in new byte[] { 0, 1, 3 }) {
            var repeat = new byte[32];

            Put(
                bytes: [0xBE, 11, 0x81, 0xBE, 27, 0x81, 0xB5, count],
                image: repeat,
                offset: 0
            );
            Pointer(
                image: repeat,
                offset: 8,
                target: 3
            );
            Put(
                bytes: [0xBE, 45, 0x81, 0xB1],
                image: repeat,
                offset: 12
            );
            var points = new List<Point> { new(
                11,
                0,
                0,
                0,
                3
            ), new(
                27,
                0,
                0,
                0,
                6
            ) };

            for (byte iteration = 1; (iteration < ((count == 0)
                ? 6
                : count)); ++iteration) {
                // Retail black-box control: an unbounded repeat never advances the finite-repeat counter.
                points.Add(item: new(
                    27,
                    0,
                    0,
                    ((count == 0)
                    ? (byte)0
                    : iteration),
                    6
                ));
            }
            if (count != 0) {
                points.Add(item: new(
                45,
                0,
                0,
                0,
                15
            )); points.Add(item: new(
                Active: false,
                Depth: 0,
                Offset: 16,
                Repeat: 0,
                Volume: 45,
                Wait: 0
            ));
            }
            yield return new(
                Name: $"repeat-{count}",
                Program: repeat,
                Points: points.ToArray()
            );
        }
        yield return new(
            Name: "pattern-end-without-call",
            Program: [0xB4, 0xBE, 33, 0x81, 0xB1],
            Points: [new(
                    33,
                    0,
                    0,
                    0,
                    4
                ), new(
                    Active: false,
                    Depth: 0,
                    Offset: 5,
                    Repeat: 0,
                    Volume: 33,
                    Wait: 0
                )]
        );
    }
    internal static byte[] Note(byte command, byte extra = 0) => ((extra == 0)
        ? [0xBD, 0, 0xBE, 127, 0xBF, 64, command, 60, 127, 0x88, 0xB1]
        : [0xBD, 0, 0xBE, 127, 0xBF, 64, command, 60, 127, extra, 0x88, 0xB1]
    );

    private static void Pointer(byte[] image, int offset, int target) => BinaryPrimitives.WriteUInt32LittleEndian(
        destination: image.AsSpan(start: offset),
        value: (FirmwareSequenceFixture.Program + ((uint)target))
    );
    private static void Put(byte[] image, int offset, ReadOnlySpan<byte> bytes) => bytes.CopyTo(destination: image.AsSpan(start: offset));
}
