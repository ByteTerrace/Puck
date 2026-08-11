using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Puck.World.Server;

/// <summary>Process-scoped federation authentication. Operators provision the same 256-bit secret to authorities
/// that are allowed to federate; absence is deny-by-default. A fresh server challenge and the claimed source
/// authority are both covered, so a captured proof cannot be replayed or rebound to another authority namespace.</summary>
public sealed class WorldFederationSecurity {
    public const int SecretBytes = 32;
    public const int ChallengeBytes = 32;
    public const int ProofBytes = 32;

    private readonly byte[]? m_secret;

    public WorldFederationSecurity(byte[]? secret) {
        if ((secret is not null) && (secret.Length != SecretBytes)) {
            throw new ArgumentException(message: $"federation secret must contain exactly {SecretBytes} bytes", paramName: nameof(secret));
        }

        m_secret = secret?.ToArray();
    }

    public bool IsConfigured => (m_secret is not null);

    public byte[] NewChallenge() {
        var challenge = new byte[ChallengeBytes];
        RandomNumberGenerator.Fill(data: challenge);
        return challenge;
    }

    public byte[] Prove(string sourceAuthority, ReadOnlySpan<byte> challenge) {
        if (m_secret is null) {
            throw new InvalidOperationException("federation authentication is not configured");
        }

        return Compute(secret: m_secret, sourceAuthority: sourceAuthority, challenge: challenge);
    }

    public bool Verify(string sourceAuthority, ReadOnlySpan<byte> challenge, ReadOnlySpan<byte> proof) {
        if ((m_secret is null) || (proof.Length != ProofBytes)) {
            return false;
        }

        var expected = Compute(secret: m_secret, sourceAuthority: sourceAuthority, challenge: challenge);

        return CryptographicOperations.FixedTimeEquals(left: expected, right: proof);
    }

    private static byte[] Compute(byte[] secret, string sourceAuthority, ReadOnlySpan<byte> challenge) {
        ArgumentException.ThrowIfNullOrWhiteSpace(argument: sourceAuthority);

        var authority = Encoding.UTF8.GetBytes(s: sourceAuthority);
        var message = new byte[sizeof(int) + authority.Length + challenge.Length];
        BinaryPrimitives.WriteInt32LittleEndian(destination: message, value: authority.Length);
        authority.CopyTo(array: message, index: sizeof(int));
        challenge.CopyTo(destination: message.AsSpan(start: (sizeof(int) + authority.Length)));

        return HMACSHA256.HashData(key: secret, source: message);
    }
}
