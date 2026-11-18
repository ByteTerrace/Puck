using System.Buffers.Binary;
using System.Security.Cryptography;

namespace Puck.State;

/// <summary>Deterministic private stream samples. The authority provisions the key before simulation; simulation
/// consumes no system entropy. Cursor addressing preserves constant-time resume.</summary>
public sealed class PrivateDrawStream {
    private readonly Func<ulong, ulong, long, uint> m_sample;
    internal PrivateDrawAuthority? Owner { get; }

    internal PrivateDrawStream(Func<ulong, ulong, long, uint> sample, long installation, PrivateDrawAuthority? owner) {
        m_sample = sample;
        Installation = installation;
        Owner = owner;
    }

    internal static PrivateDrawStream FromKey(ClosedBitset256 key) =>
        new((seed, stream, cursor) => Word(key, seed, stream, cursor), 0, null);

    /// <summary>The installation ordinal in the authority continuation.</summary>
    public long Installation { get; }

    /// <summary>Returns the word at this address.</summary>
    public uint Sample(ulong seed, ulong stream, long cursor) => m_sample(seed, stream, cursor);

    /// <summary>Computes one word from an installed key.</summary>
    internal static uint Word(ClosedBitset256 secret, ulong seed, ulong stream, long cursor) {
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
