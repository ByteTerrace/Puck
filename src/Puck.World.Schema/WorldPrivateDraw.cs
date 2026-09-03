using System.Buffers.Binary;
using System.Security.Cryptography;

namespace Puck.World;

/// <summary>Deterministic private stream samples. The authority provisions the key before simulation;
/// simulation consumes no system entropy. Cursor addressing preserves constant-time resume.</summary>
internal static class WorldPrivateDraw {
    internal static uint Sample(ClosedBitset256 secret, ulong seed, ulong stream, long cursor) {
        Span<byte> key = stackalloc byte[32];
        BinaryPrimitives.WriteUInt64LittleEndian(key, secret.Word0);
        BinaryPrimitives.WriteUInt64LittleEndian(key[8..], secret.Word1);
        BinaryPrimitives.WriteUInt64LittleEndian(key[16..], secret.Word2);
        BinaryPrimitives.WriteUInt64LittleEndian(key[24..], secret.Word3);
        Span<byte> message = stackalloc byte[32];
        BinaryPrimitives.WriteUInt64LittleEndian(message, 0x3156455441564950UL);
        BinaryPrimitives.WriteUInt64LittleEndian(message[8..], seed);
        BinaryPrimitives.WriteUInt64LittleEndian(message[16..], stream);
        BinaryPrimitives.WriteInt64LittleEndian(message[24..], cursor);
        Span<byte> output = stackalloc byte[32];
        HMACSHA256.HashData(key, message, output);
        return BinaryPrimitives.ReadUInt32LittleEndian(output);
    }
}
