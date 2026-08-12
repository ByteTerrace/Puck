using System.Security.Cryptography;

namespace Puck.Carriage;

/// <summary>
/// A key's self-certifying identity: <c>(domain, subject, algorithm, key-hash)</c>
/// (README.md, "Signed carriage"). <see cref="Domain"/> is never a name — it IS the fingerprint
/// of the root key at the top of this id's chain, constant across every key that chain vouches for, so a
/// verifier needs no registry to resolve it and it cannot be squatted (taking one requires the root's
/// private half). <see cref="KeyHash"/> is always derived from actual key bytes via
/// <see cref="ComputeKeyHash(ReadOnlySpan{byte})"/>, never accepted as an independent claim — that is what
/// makes an id self-certifying: anyone holding the referenced public key can recompute the hash and catch
/// a lie.
/// </summary>
/// <remarks>
/// A root is the base case: no domain above it and no subject, only its own fingerprint, so
/// <see cref="IsRoot"/> is exactly <c>Domain == KeyHash &amp;&amp; Subject is null</c> — no separate flag
/// is stored because the shape already proves it. An issuing key (binding #1's target) shares its domain's
/// root fingerprint but carries no subject, since it is not a platform user; a subject key carries the
/// platform user id that owns it.
/// </remarks>
public sealed record KeyId {
    /// <summary>The root key's SHA-256 fingerprint (lowercase hex) for this id's whole chain — never a name.</summary>
    public required string Domain { get; init; }

    /// <summary>The platform user id this key belongs to, or <see langword="null"/> for a root or issuing key.</summary>
    public string? Subject { get; init; }

    /// <summary>One of <see cref="CarriageAlgorithms"/>'s names — curve and hash/scheme both, so the pin fully determines the crypto.</summary>
    public required string Algorithm { get; init; }

    /// <summary>SHA-256 (lowercase hex) of this key's <c>SubjectPublicKeyInfo</c> encoding.</summary>
    public required string KeyHash { get; init; }

    /// <summary>Whether this id is the base case of the chain: self-referential domain and no subject.</summary>
    public bool IsRoot => (string.Equals(
        a: Domain,
        b: KeyHash,
        comparisonType: StringComparison.Ordinal
    ) && (Subject is null));

    /// <summary>Computes the lowercase-hex SHA-256 fingerprint of a <c>SubjectPublicKeyInfo</c> encoding.</summary>
    /// <param name="subjectPublicKeyInfo">The DER-encoded SPKI bytes exported from an EC key.</param>
    /// <returns>A 64-character lowercase-hex string.</returns>
    public static string ComputeKeyHash(ReadOnlySpan<byte> subjectPublicKeyInfo) =>
        Convert.ToHexStringLower(bytes: SHA256.HashData(source: subjectPublicKeyInfo));

    /// <summary>Builds a root id: domain is self-referential (equal to this key's own hash), subject is absent.</summary>
    /// <param name="subjectPublicKeyInfo">The root key's SPKI bytes.</param>
    /// <param name="algorithm">The root key's signing algorithm.</param>
    public static KeyId ForRoot(ReadOnlySpan<byte> subjectPublicKeyInfo, string algorithm) {
        var hash = ComputeKeyHash(subjectPublicKeyInfo: subjectPublicKeyInfo);

        return new KeyId {
            Algorithm = algorithm,
            Domain = hash,
            KeyHash = hash,
            Subject = null,
        };
    }

    /// <summary>Builds an issuing key's id: shares its root's domain, carries no subject.</summary>
    /// <param name="domain">The root fingerprint of the chain this issuing key belongs to.</param>
    /// <param name="subjectPublicKeyInfo">The issuing key's SPKI bytes.</param>
    /// <param name="algorithm">The issuing key's signing algorithm.</param>
    public static KeyId ForIssuing(string domain, ReadOnlySpan<byte> subjectPublicKeyInfo, string algorithm) =>
        new() {
            Algorithm = algorithm,
            Domain = domain,
            KeyHash = ComputeKeyHash(subjectPublicKeyInfo: subjectPublicKeyInfo),
            Subject = null,
        };

    /// <summary>Builds a subject (per-user) key's id.</summary>
    /// <param name="domain">The root fingerprint of the chain this subject key belongs to.</param>
    /// <param name="subject">The platform user id this key belongs to. Must not be null or whitespace.</param>
    /// <param name="subjectPublicKeyInfo">The subject key's SPKI bytes.</param>
    /// <param name="algorithm">The subject key's algorithm (signing or sealing).</param>
    public static KeyId ForSubject(string domain, string subject, ReadOnlySpan<byte> subjectPublicKeyInfo, string algorithm) {
        ArgumentException.ThrowIfNullOrWhiteSpace(argument: subject);

        return new KeyId {
            Algorithm = algorithm,
            Domain = domain,
            KeyHash = ComputeKeyHash(subjectPublicKeyInfo: subjectPublicKeyInfo),
            Subject = subject,
        };
    }
}
