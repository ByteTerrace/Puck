using System.Buffers;
using System.Buffers.Binary;
using System.Numerics;
using System.Text;

namespace Puck.Assets;

/// <summary>Writes the canonical binary primitives Puck's binary artifacts share: minimal little-endian base-128
/// unsigned integers, sign-and-magnitude integers of any size, length-prefixed UTF-8 text, and fixed little-endian
/// 64-bit words. Every value has exactly one spelling, and <see cref="CanonicalBinaryReader"/> reads that spelling back
/// and refuses any other, so equal values always encode to equal bytes and a content hash over an artifact means one
/// thing.</summary>
public static class CanonicalBinaryWriterExtensions {
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true
    );

    /// <summary>Writes an integer as a sign byte (0 zero, 1 positive, 2 negative), a variable-width byte count, and the
    /// big-endian magnitude without leading zero bytes.</summary>
    /// <param name="writer">The destination.</param>
    /// <param name="value">The integer to write.</param>
    public static void WriteBigInteger(this IBufferWriter<byte> writer, BigInteger value) {
        if (value.IsZero) {
            writer.WriteByte(value: 0);
            writer.WriteVarUInt(value: 0);
            return;
        }

        writer.WriteByte(value: ((value.Sign > 0)
            ? ((byte)1)
            : ((byte)2)));
        var magnitude = BigInteger.Abs(value: value);
        var byteCount = magnitude.GetByteCount(isUnsigned: true);

        writer.WriteVarUInt(value: checked((uint)byteCount));
        var destination = writer.GetSpan(sizeHint: byteCount)[..byteCount];

        if (
            !magnitude.TryWriteBytes(
            bytesWritten: out var written,
            destination: destination,
            isBigEndian: true,
            isUnsigned: true
        ) ||
            (written != byteCount)
        ) {
            throw new InvalidOperationException(message: "BigInteger did not write its canonical magnitude");
        }

        writer.Advance(count: byteCount);
    }
    /// <summary>Writes one byte.</summary>
    /// <param name="writer">The destination.</param>
    /// <param name="value">The byte to write.</param>
    public static void WriteByte(this IBufferWriter<byte> writer, byte value) {
        var destination = writer.GetSpan(sizeHint: 1);

        destination[0] = value;
        writer.Advance(count: 1);
    }
    /// <summary>Writes bytes as they stand, with no length prefix.</summary>
    /// <param name="writer">The destination.</param>
    /// <param name="value">The bytes to write.</param>
    public static void WriteBytes(this IBufferWriter<byte> writer, ReadOnlySpan<byte> value) {
        var destination = writer.GetSpan(sizeHint: value.Length);

        value.CopyTo(destination: destination);
        writer.Advance(count: value.Length);
    }
    /// <summary>Writes text as a variable-width UTF-8 byte count followed by the UTF-8 bytes.</summary>
    /// <param name="writer">The destination.</param>
    /// <param name="value">The text to write.</param>
    /// <exception cref="ArgumentException"><paramref name="value"/> holds an unpaired surrogate, which has no UTF-8
    /// spelling.</exception>
    public static void WriteText(this IBufferWriter<byte> writer, string value) {
        ArgumentNullException.ThrowIfNull(argument: value);

        byte[] bytes;

        try {
            bytes = StrictUtf8.GetBytes(s: value);
        } catch (EncoderFallbackException exception) {
            throw new ArgumentException(
                innerException: exception,
                message: "the text holds an unpaired surrogate and has no UTF-8 spelling",
                paramName: nameof(value)
            );
        }

        writer.WriteVarUInt(value: checked((uint)bytes.Length));
        writer.WriteBytes(value: bytes);
    }
    /// <summary>Writes a 64-bit word as eight little-endian bytes.</summary>
    /// <param name="writer">The destination.</param>
    /// <param name="value">The word to write.</param>
    public static void WriteUInt64(this IBufferWriter<byte> writer, ulong value) {
        BinaryPrimitives.WriteUInt64LittleEndian(
            destination: writer.GetSpan(sizeHint: sizeof(ulong)),
            value: value
        );
        writer.Advance(count: sizeof(ulong));
    }
    /// <summary>Writes an unsigned integer in the minimal little-endian base-128 form: seven bits a byte, the high bit
    /// set on every byte but the last.</summary>
    /// <param name="writer">The destination.</param>
    /// <param name="value">The integer to write.</param>
    public static void WriteVarUInt(this IBufferWriter<byte> writer, uint value) {
        do {
            var current = ((byte)(value & 0x7f));

            value >>= 7;
            if (value != 0) { current |= 0x80; }
            writer.WriteByte(value: current);
        } while (value != 0);
    }
    /// <summary>Writes <paramref name="count"/> zero bytes.</summary>
    /// <param name="writer">The destination.</param>
    /// <param name="count">The number of zero bytes.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="count"/> is negative.</exception>
    public static void WriteZeros(this IBufferWriter<byte> writer, int count) {
        ArgumentOutOfRangeException.ThrowIfNegative(value: count);

        var destination = writer.GetSpan(sizeHint: count)[..count];

        destination.Clear();
        writer.Advance(count: count);
    }
}
