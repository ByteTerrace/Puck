using System.Buffers.Binary;
using System.Security.Cryptography;

namespace Puck.State;

/// <summary>Deterministic private stream samples. The authority provisions the key before simulation; simulation
/// consumes no system entropy. Cursor addressing preserves constant-time resume.</summary>
internal static class PrivateDraw {
    internal static uint Sample(ClosedBitset256 secret, ulong seed, ulong stream, long cursor) {
        Span<byte> key = stackalloc byte[32];

        BinaryPrimitives.WriteUInt64LittleEndian(
            destination: key,
            value: secret.Word0
        );
        BinaryPrimitives.WriteUInt64LittleEndian(
            destination: key[8..],
            value: secret.Word1
        );
        BinaryPrimitives.WriteUInt64LittleEndian(
            destination: key[16..],
            value: secret.Word2
        );
        BinaryPrimitives.WriteUInt64LittleEndian(
            destination: key[24..],
            value: secret.Word3
        );
        Span<byte> message = stackalloc byte[32];

        BinaryPrimitives.WriteUInt64LittleEndian(
            destination: message,
            value: 0x3156455441564950UL
        );
        BinaryPrimitives.WriteUInt64LittleEndian(
            destination: message[8..],
            value: seed
        );
        BinaryPrimitives.WriteUInt64LittleEndian(
            destination: message[16..],
            value: stream
        );
        BinaryPrimitives.WriteInt64LittleEndian(
            destination: message[24..],
            value: cursor
        );
        Span<byte> output = stackalloc byte[32];

        HMACSHA256.HashData(
            destination: output,
            key: key,
            source: message
        );
        return BinaryPrimitives.ReadUInt32LittleEndian(source: output);
    }
}
