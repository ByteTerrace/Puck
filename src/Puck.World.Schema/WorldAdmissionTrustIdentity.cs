using Puck.Attestation;

namespace Puck.World.Protocol;

/// <summary>The policy identity that authenticated an admission, retained across reauthorization and replay.</summary>
/// <param name="Mode">The proof mode; a different mode cannot inherit this admission.</param>
/// <param name="Domain">The trusted entry's domain.</param>
/// <param name="Subject">The pinned subject, or null for a vouching root.</param>
/// <param name="Algorithm">The pinned algorithm, or null for keyless admission.</param>
/// <param name="KeyHash">The complete pinned key hash, or null for keyless admission.</param>
public sealed record WorldAdmissionTrustIdentity(WorldAdmissionTrustMode Mode, string Domain, string? Subject, string? Algorithm, string? KeyHash) {
    internal static WorldAdmissionTrustIdentity FromEntry(WorldAdmissionEntry entry) => new(
        entry.Mode, entry.Domain, entry.Subject, entry.Algorithm,
        ((entry.PublicKey is { } key) ? KeyId.ComputeKeyHash(subjectPublicKeyInfo: Convert.FromBase64String(s: key)) : null));
    internal static WorldAdmissionTrustIdentity FromVerified(TrustListEntry entry) => new(
        ((entry.Mode == AttestationTrustMode.Vouches) ? WorldAdmissionTrustMode.Vouches : WorldAdmissionTrustMode.SignsDirectly),
        entry.PinnedId.Domain, entry.PinnedId.Subject, entry.PinnedId.Algorithm, entry.PinnedId.KeyHash);
}
