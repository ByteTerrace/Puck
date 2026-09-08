using System.Security.Cryptography;
using System.Text;

namespace Puck.Azure.Functions.Utilities;

internal static class AzureUtilities
{
    private static ReadOnlySpan<byte> ArmNamespaceBytes => [
        0x11, 0xFB, 0x06, 0xFB, 0x71, 0x2D, 0x4D, 0xDD,
        0x98, 0xC7, 0xE7, 0x1B, 0xBD, 0x58, 0x88, 0x30,
    ];

    private static Guid HashToGuid(ReadOnlySpan<byte> source) {
        var destination = (stackalloc byte[20]);

        SHA1.HashData(
            destination: destination,
            source: source
        );

        destination[6] = ((byte)((destination[6] & 0x0F) | (5 << 4)));
        destination[8] = ((byte)((destination[8] & 0x3F) | 0x80));

        return new Guid(
            b: destination[..16],
            bigEndian: true
        );
    }

    public static Guid Guid(ReadOnlySpan<char> value) {
        var scratchLength = (16 + Encoding.UTF8.GetByteCount(chars: value));
        var scratchSpan = (
            (512 >= scratchLength)
            ? stackalloc byte[scratchLength]
            : new byte[scratchLength]
        );

        ArmNamespaceBytes.CopyTo(destination: scratchSpan);
        Encoding.UTF8.GetBytes(
            bytes: scratchSpan[16..],
            chars: value
        );

        return HashToGuid(source: scratchSpan);
    }
    public static Guid Guid(params string[] values) {
        var scratchLength = 16;

        foreach (var value in values) {
            scratchLength += Encoding.UTF8.GetByteCount(s: value);
        }

        scratchLength += Math.Max(val1: 0, val2: (values.Length - 1));

        var scratchSpan = (
            (512 >= scratchLength)
            ? stackalloc byte[scratchLength]
            : new byte[scratchLength]
        );

        ArmNamespaceBytes.CopyTo(destination: scratchSpan);

        var scratchOffset = 16;

        if (0 < values.Length) {
            scratchOffset += Encoding.UTF8.GetBytes(
                bytes: scratchSpan[scratchOffset..],
                chars: values[0]
            );

            var valuesOffset = 1;

            while (values.Length > valuesOffset) {
                scratchSpan[scratchOffset++] = ((byte)'-');
                scratchOffset += Encoding.UTF8.GetBytes(
                    bytes: scratchSpan[scratchOffset..],
                    chars: values[valuesOffset++]
                );
            }
        }

        return HashToGuid(source: scratchSpan);
    }
}

