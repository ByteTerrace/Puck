using System.Buffers.Binary;
using System.Security.Cryptography;

namespace Puck.State;

/// <summary>Reads a raw word for a generator authorized to consume a site.</summary>
public delegate uint PrivateDrawSample(ulong seed, ulong stream, long cursor);

/// <summary>Deterministic private stream samples. The authority provisions the key before simulation; simulation
/// consumes no system entropy. Cursor addressing preserves constant-time resume.</summary>
public sealed class PrivateDrawStream {
    private readonly PrivateDrawSample m_sample;
    internal PrivateDrawAuthority Owner { get; }

    internal PrivateDrawStream(PrivateDrawSample sample, long installation, PrivateDrawAuthority owner) {
        Installation = installation;
        m_sample = sample;
        Owner = owner;
    }

    /// <summary>Returns a sampler under a caller-owned key that no authority issued: the words a hypothetical
    /// evaluation (a search candidate) draws in place of a private site's own, which read no authority word and move
    /// no authority's read count or timeline.</summary>
    /// <param name="key">The sampler's key.</param>
    /// <returns>The sampler.</returns>
    public static PrivateDrawSample Imagined(ClosedBitset256 key) => (seed, stream, cursor) => Word(key, seed, stream, cursor);

    /// <summary>The nonsecret address used by recorded draws.</summary>
    public long Installation { get; }

    /// <summary>Opens this site's word source for its issuing authority. Public previews need no credential.</summary>
    public PrivateDrawSample? Open(PrivateDrawAuthority? authority) =>
        ReferenceEquals(Owner, authority) || (authority is null && Owner.PreviewWords) ? m_sample : null;

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
