using System.Security.Cryptography;

namespace Puck.Carriage;

/// <summary>Whether a carriage key signs envelopes or agrees a sealing key with another party.</summary>
public enum CarriageKeyRole {
    /// <summary>The key produces or verifies ECDSA signatures over envelope bytes.</summary>
    Signing,
    /// <summary>The key performs ECDH key agreement feeding an AES-GCM key (sealed carriage).</summary>
    Sealing,
}

/// <summary>
/// One algorithm the carriage envelope understands, fully naming curve, signature hash (for signing) or
/// key-agreement/AEAD scheme (for sealing). <see cref="KeyId.Algorithm"/> stores the <see cref="Name"/> of
/// one of these; nothing else may appear there, and the algorithm rule (docs/world-model.md, "Signed
/// carriage") requires the verifier to resolve the actual crypto parameters from this table via the
/// pinned key's algorithm — never from an untrusted envelope field.
/// </summary>
/// <param name="Name">The wire string, e.g. <c>ecdsa-p256-sha256</c>. Curve and hash are both named because a
/// P-256 key can sign under SHA-256 or SHA-384: curve alone does not pin the scheme.</param>
/// <param name="Role">Whether this algorithm signs or seals.</param>
/// <param name="Curve">The EC curve every key under this algorithm name uses.</param>
/// <param name="SignatureHash">The hash algorithm ECDSA signs over. <see langword="null"/> for sealing algorithms.</param>
public readonly record struct CarriageAlgorithmDescriptor(
    string Name,
    CarriageKeyRole Role,
    ECCurve Curve,
    HashAlgorithmName? SignatureHash
);

/// <summary>
/// The closed set of algorithm names a carriage envelope may declare, and the table that resolves one to
/// concrete crypto parameters. Adding a scheme means adding an entry here — the envelope's algorithm field
/// is a lookup key into this table, never an instruction the message gets to invent.
/// </summary>
public static class CarriageAlgorithms {
    /// <summary>P-256 ECDSA, signing over a SHA-256 digest — the default signing algorithm.</summary>
    public const string EcdsaP256Sha256 = "ecdsa-p256-sha256";

    /// <summary>P-256 ECDSA, signing over a SHA-384 digest.</summary>
    public const string EcdsaP256Sha384 = "ecdsa-p256-sha384";

    /// <summary>P-256 ECDH key agreement, HKDF-SHA256 key derivation, AES-256-GCM AEAD — the sealing algorithm.</summary>
    public const string EcdhP256HkdfSha256Aes256Gcm = "ecdh-p256-hkdf-sha256-aes256gcm";

    private static readonly Dictionary<string, CarriageAlgorithmDescriptor> s_descriptors = BuildTable();

    private static Dictionary<string, CarriageAlgorithmDescriptor> BuildTable() {
        var table = new Dictionary<string, CarriageAlgorithmDescriptor>(comparer: StringComparer.Ordinal);

        void Add(string name, CarriageKeyRole role, HashAlgorithmName? hash) {
            table.Add(
                key: name,
                value: new CarriageAlgorithmDescriptor(
                    Name: name,
                    Role: role,
                    Curve: ECCurve.NamedCurves.nistP256,
                    SignatureHash: hash
                )
            );
        }

        Add(name: EcdsaP256Sha256, role: CarriageKeyRole.Signing, hash: HashAlgorithmName.SHA256);
        Add(name: EcdsaP256Sha384, role: CarriageKeyRole.Signing, hash: HashAlgorithmName.SHA384);
        Add(name: EcdhP256HkdfSha256Aes256Gcm, role: CarriageKeyRole.Sealing, hash: null);

        return table;
    }

    /// <summary>Resolves an algorithm name to its concrete parameters.</summary>
    /// <param name="algorithm">A value that must equal one of this class's name constants.</param>
    /// <returns>The resolved descriptor.</returns>
    /// <exception cref="NotSupportedException"><paramref name="algorithm"/> is not a known carriage algorithm.</exception>
    public static CarriageAlgorithmDescriptor Resolve(string algorithm) {
        if (s_descriptors.TryGetValue(key: algorithm, value: out var descriptor)) {
            return descriptor;
        }

        throw new NotSupportedException(message: $"'{algorithm}' is not a carriage algorithm this prototype understands.");
    }

    /// <summary>Determines whether <paramref name="algorithm"/> resolves to a known descriptor without throwing.</summary>
    /// <param name="algorithm">The candidate algorithm name.</param>
    public static bool IsKnown(string algorithm) => s_descriptors.ContainsKey(key: algorithm);
}

/// <summary>
/// Curve identity for imported keys. An algorithm name promises a curve, and an SPKI blob carries its own —
/// so every key imported from bytes has the two compared before it is used, or a name promising P-256 would
/// happily verify against a key on some other curve (the invalid-curve family of attacks). Named curves do
/// not compare by value across platforms — the same curve arrives as an OID on one and a friendly name on
/// another — so identity is decided by an alias set rather than by <see cref="ECCurve"/> equality.
/// </summary>
public static class CarriageCurves {
    private static readonly HashSet<string> s_nistP256Aliases = new(comparer: StringComparer.OrdinalIgnoreCase) {
        "1.2.840.10045.3.1.7",
        "ECDH_P256",
        "ECDSA_P256",
        "nistP256",
        "prime256v1",
        "secp256r1",
    };

    /// <summary>Determines whether <paramref name="key"/>'s curve is the one <paramref name="expected"/> names.</summary>
    /// <param name="key">The curve reported by an imported key's exported parameters.</param>
    /// <param name="expected">The curve the pinned algorithm's descriptor names.</param>
    /// <returns><see langword="true"/> only when both are recognisably the same named curve.</returns>
    public static bool Matches(ECCurve key, ECCurve expected) {
        if (!key.IsNamed || !expected.IsNamed) {
            return false;
        }

        var keyIsP256 = IsNistP256(curve: key);

        return ((keyIsP256 == IsNistP256(curve: expected)) && keyIsP256);
    }

    /// <summary>Determines whether a named curve is P-256 under any of the names the platforms spell it with.</summary>
    /// <param name="curve">The curve to test.</param>
    public static bool IsNistP256(ECCurve curve) =>
        (curve.IsNamed &&
        (
            s_nistP256Aliases.Contains(item: (curve.Oid.Value ?? string.Empty)) ||
            s_nistP256Aliases.Contains(item: (curve.Oid.FriendlyName ?? string.Empty))
        ));
}
