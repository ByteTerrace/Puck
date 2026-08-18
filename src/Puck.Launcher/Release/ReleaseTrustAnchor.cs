using Puck.Attestation;

namespace Puck.Launcher.Release;

/// <summary>
/// A release channel's root trust anchor, authored the same domain/algorithm/SPKI-bytes shape this repository's own
/// admission-door trust rows already establish — never a fingerprint alone (a fingerprint pins nothing an offline
/// verifier could resolve back into a key). A composition root compiles this in as a constant; it is never a field
/// of any synced document — a trust anchor a user's own storage container could rewrite is not a trust anchor.
/// </summary>
/// <param name="Domain">The root key's own SHA-256 fingerprint (lowercase hex).</param>
/// <param name="Algorithm">The pinned key's signing algorithm, from <see cref="AttestationAlgorithms"/>.</param>
/// <param name="PublicKeySubjectPublicKeyInfoBase64">The root key's actual SPKI bytes, base64-encoded.</param>
public sealed record ReleaseTrustAnchor(string Domain, string Algorithm, string PublicKeySubjectPublicKeyInfoBase64) {
    /// <summary>The <see cref="Domain"/> a placeholder anchor authors — never a real key's fingerprint, so
    /// <see cref="IsPlaceholder"/> can recognize it by name before anything tries to import
    /// <see cref="PublicKeySubjectPublicKeyInfoBase64"/> as a key at all.</summary>
    public const string PlaceholderDomain = "placeholder-release-trust-anchor";

    /// <summary>A build-time placeholder pin: the exact shape a composition root compiles in before it has a real
    /// release-signing chain to pin. <see cref="LauncherServiceRegistration.AddSelfUpdate"/> resolves a verifier
    /// that refuses every manifest outright while <see cref="UpdateOptions.TrustAnchor"/> equals this — never one
    /// that tries to import <see cref="PublicKeySubjectPublicKeyInfoBase64"/> (here, empty) as a signing key.</summary>
    public static ReleaseTrustAnchor Placeholder { get; } = new(
        Algorithm: AttestationAlgorithms.EcdsaP256Sha256,
        Domain: PlaceholderDomain,
        PublicKeySubjectPublicKeyInfoBase64: string.Empty
    );
    /// <summary>Gets a value indicating whether this anchor is the build-time <see cref="Placeholder"/> rather than
    /// an authored real key.</summary>
    public bool IsPlaceholder => string.Equals(a: Domain, b: PlaceholderDomain, comparisonType: StringComparison.Ordinal);

    /// <summary>Builds the <see cref="TrustListEntry"/> this anchor authorizes for release-manifest verification —
    /// always <see cref="AttestationTrustMode.Vouches"/>, since a release root vouches for a rotatable issuing key
    /// rather than signing manifests itself.</summary>
    /// <param name="reach">The slot names a manifest claim admitted under this anchor may reach.</param>
    /// <param name="maximumAge">The verifier-authored maximum claim age, or <see langword="null"/> for no verifier-side ceiling beyond the signed window.</param>
    /// <returns>A <see cref="TrustListEntry"/> ready for <see cref="TrustList"/> construction.</returns>
    public TrustListEntry ToTrustListEntry(IReadOnlySet<string> reach, TimeSpan? maximumAge) => new(
        MaximumAge: maximumAge,
        Mode: AttestationTrustMode.Vouches,
        PinnedId: new KeyId {
            Algorithm = Algorithm,
            Domain = Domain,
            KeyHash = Domain,
            Subject = null,
        },
        PublicKeySubjectPublicKeyInfo: Convert.FromBase64String(s: PublicKeySubjectPublicKeyInfoBase64),
        Reach: reach
    );
}
