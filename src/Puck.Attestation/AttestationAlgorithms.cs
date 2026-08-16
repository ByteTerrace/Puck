using System.Security.Cryptography;

namespace Puck.Attestation;

/// <summary>Whether an attestation key signs attestations or agrees a sealing key with another party.</summary>
public enum AttestationKeyRole {
    /// <summary>The key produces or verifies ECDSA signatures over attestation bytes.</summary>
    Signing,
    /// <summary>The key performs ECDH key agreement feeding an AES-GCM key (sealed attestation).</summary>
    Sealing,
}
/// <summary>
/// One algorithm the attestation understands, fully naming curve, signature hash (for signing) or
/// key-agreement/AEAD scheme (for sealing). <see cref="KeyId.Algorithm"/> stores the <see cref="Name"/> of
/// one of these; nothing else may appear there, and the algorithm rule (README.md, "Signed
/// attestation") requires the verifier to resolve the actual crypto parameters from this table via the
/// pinned key's algorithm — never from an untrusted attestation field.
/// </summary>
/// <param name="Name">The wire string, e.g. <c>ecdsa-p256-sha256</c>. Curve and hash are both named because a
/// P-256 key can sign under more than one digest: curve alone does not pin the scheme.</param>
/// <param name="Role">Whether this algorithm signs or seals.</param>
/// <param name="Curve">The EC curve every key under this algorithm name uses.</param>
/// <param name="SignatureHash">The hash algorithm ECDSA signs over. <see langword="null"/> for sealing algorithms.</param>
public readonly record struct AttestationAlgorithmDescriptor(
    string Name,
    AttestationKeyRole Role,
    ECCurve Curve,
    HashAlgorithmName? SignatureHash
);
/// <summary>
/// The closed set of algorithm names an attestation may declare, and the table that resolves one to
/// concrete crypto parameters. Adding a scheme means adding an entry here — the attestation's algorithm field
/// is a lookup key into this table, never an instruction the message gets to invent.
/// </summary>
public static class AttestationAlgorithms {
    /// <summary>P-256 ECDH key agreement, HKDF-SHA256 key derivation, AES-256-GCM AEAD — the sealing algorithm.</summary>
    public const string EcdhP256HkdfSha256Aes256Gcm = "ecdh-p256-hkdf-sha256-aes256gcm";
    /// <summary>P-256 ECDSA, signing over a SHA-256 digest — the mandatory signing algorithm.</summary>
    public const string EcdsaP256Sha256 = "ecdsa-p256-sha256";

    private static readonly Dictionary<string, AttestationAlgorithmDescriptor> Descriptors = BuildTable();

    private static Dictionary<string, AttestationAlgorithmDescriptor> BuildTable() {
        var table = new Dictionary<string, AttestationAlgorithmDescriptor>(comparer: StringComparer.Ordinal);

        void Add(string name, AttestationKeyRole role, HashAlgorithmName? hash) {
            table.Add(
                key: name,
                value: new AttestationAlgorithmDescriptor(
                    Name: name,
                    Role: role,
                    Curve: ECCurve.NamedCurves.nistP256,
                    SignatureHash: hash
                )
            );
        }

        Add(
            name: EcdsaP256Sha256,
            role: AttestationKeyRole.Signing,
            hash: HashAlgorithmName.SHA256
        );
        Add(
            hash: null,
            name: EcdhP256HkdfSha256Aes256Gcm,
            role: AttestationKeyRole.Sealing
        );

        return table;
    }

    /// <summary>Determines whether <paramref name="algorithm"/> resolves to a known descriptor without throwing.</summary>
    /// <param name="algorithm">The candidate algorithm name.</param>
    public static bool IsKnown(string algorithm) => Descriptors.ContainsKey(key: algorithm);
    /// <summary>Resolves an algorithm name to its concrete parameters.</summary>
    /// <param name="algorithm">A value that must equal one of this class's name constants.</param>
    /// <returns>The resolved descriptor.</returns>
    /// <exception cref="NotSupportedException"><paramref name="algorithm"/> is not a known attestation algorithm.</exception>
    public static AttestationAlgorithmDescriptor Resolve(string algorithm) {
        if (Descriptors.TryGetValue(
            key: algorithm,
            value: out var descriptor
        )) {
            return descriptor;
        }

        throw new NotSupportedException(message: $"'{algorithm}' is not an attestation algorithm.");
    }
}
/// <summary>
/// Curve identity for imported keys. An algorithm name promises a curve, and an SPKI blob carries its own —
/// so every key imported from bytes has the two compared before it is used, or a name promising P-256 would
/// happily verify against a key on some other curve (the invalid-curve family of attacks). Named curves do
/// not compare by value across platforms — the same curve arrives as an OID on one and a friendly name on
/// another — so identity is decided by a per-curve alias set rather than by <see cref="ECCurve"/> equality.
/// A curve named by a <see cref="AttestationAlgorithmDescriptor"/> must have an alias set here, or every key
/// under that algorithm is refused.
/// </summary>
public static class AttestationCurves {
    private static readonly HashSet<string> NistP256Aliases = new(comparer: StringComparer.OrdinalIgnoreCase) {
        "1.2.840.10045.3.1.7",
        "ECDH_P256",
        "ECDSA_P256",
        "nistP256",
        "prime256v1",
        "secp256r1",
    };
    private static readonly HashSet<string>[] AliasSets = [NistP256Aliases];

    private static bool Contains(HashSet<string> set, ECCurve curve) =>
        (set.Contains(item: (curve.Oid.Value ?? string.Empty)) ||
        set.Contains(item: (curve.Oid.FriendlyName ?? string.Empty)));
    private static HashSet<string>? FindAliasSet(ECCurve curve) {
        foreach (var set in AliasSets) {
            if (Contains(
                curve: curve,
                set: set
            )) {
                return set;
            }
        }

        return null;
    }

    /// <summary>Determines whether a named curve is P-256 under any of the names the platforms spell it with.</summary>
    /// <param name="curve">The curve to test.</param>
    public static bool IsNistP256(ECCurve curve) =>
        (curve.IsNamed && Contains(
            curve: curve,
            set: NistP256Aliases
        ));
    /// <summary>Determines whether <paramref name="key"/>'s curve is the one <paramref name="expected"/> names.</summary>
    /// <param name="key">The curve reported by an imported key's exported parameters.</param>
    /// <param name="expected">The curve the pinned algorithm's descriptor names.</param>
    /// <returns><see langword="true"/> only when both are recognisably the same named curve.</returns>
    public static bool Matches(ECCurve key, ECCurve expected) {
        if (
            !key.IsNamed ||
            !expected.IsNamed
        ) {
            return false;
        }

        var aliases = FindAliasSet(curve: expected);

        return (
            (aliases is not null) &&
            Contains(
            curve: key,
            set: aliases
        )
        );
    }
}
