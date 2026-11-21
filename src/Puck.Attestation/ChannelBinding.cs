using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Puck.Attestation;

/// <summary>
/// Binds a challenge nonce to the two authenticated transport endpoints that carry it, so a proof signed for one
/// connection cannot be replayed onto another. Every protocol that signs over a transport-bound challenge derives it
/// here, so no second encoding of the transcript exists to drift from the first.
/// </summary>
public static class ChannelBinding {
    /// <summary>Derives the bound challenge: HKDF-SHA256 over the nonce, with an info transcript of the protocol
    /// label, the connecting peer's key fingerprint, the accepting peer's key fingerprint and the caller's context,
    /// each carried with a 32-bit little-endian length so no field can borrow bytes from its neighbour.</summary>
    /// <param name="label">The protocol and version this binding belongs to, so a proof for one protocol never
    /// verifies in another.</param>
    /// <param name="nonce">The challenge the verifier issued, nonempty. The bound challenge has this length.</param>
    /// <param name="clientKeyHash">The key fingerprint of the peer that opened the connection.</param>
    /// <param name="serverKeyHash">The key fingerprint of the peer that accepted it.</param>
    /// <param name="context">Further transcript bytes the protocol needs bound (a direction, a destination, a second
    /// nonce), already unambiguous among themselves; empty when there are none.</param>
    /// <returns>The transcript-bound challenge, the same length as <paramref name="nonce"/>.</returns>
    /// <exception cref="ArgumentException"><paramref name="label"/> or either key fingerprint is null or blank, or
    /// <paramref name="nonce"/> is empty.</exception>
    public static byte[] Bind(string label, ReadOnlySpan<byte> nonce, string clientKeyHash, string serverKeyHash, ReadOnlySpan<byte> context = default) {
        ArgumentException.ThrowIfNullOrWhiteSpace(argument: label);
        ArgumentException.ThrowIfNullOrWhiteSpace(argument: clientKeyHash);
        ArgumentException.ThrowIfNullOrWhiteSpace(argument: serverKeyHash);
        if (nonce.IsEmpty) { throw new ArgumentException(message: "a channel binding needs a nonempty nonce", paramName: nameof(nonce)); }

        var labelBytes = Encoding.UTF8.GetBytes(s: label);
        var clientBytes = Encoding.UTF8.GetBytes(s: clientKeyHash);
        var serverBytes = Encoding.UTF8.GetBytes(s: serverKeyHash);
        var info = new byte[((sizeof(int) * 4) + labelBytes.Length + clientBytes.Length + serverBytes.Length + context.Length)];
        var offset = 0;

        Append(destination: info, offset: ref offset, value: labelBytes);
        Append(destination: info, offset: ref offset, value: clientBytes);
        Append(destination: info, offset: ref offset, value: serverBytes);
        Append(destination: info, offset: ref offset, value: context);
        var bound = new byte[nonce.Length];

        HKDF.DeriveKey(
            hashAlgorithmName: HashAlgorithmName.SHA256,
            ikm: nonce,
            output: bound,
            salt: default,
            info: info
        );
        return bound;
    }

    private static void Append(byte[] destination, ref int offset, ReadOnlySpan<byte> value) {
        BinaryPrimitives.WriteInt32LittleEndian(destination: destination.AsSpan(start: offset), value: value.Length);
        offset += sizeof(int);
        value.CopyTo(destination: destination.AsSpan(start: offset));
        offset += value.Length;
    }
}
