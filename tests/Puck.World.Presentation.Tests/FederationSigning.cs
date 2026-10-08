using System.Security.Cryptography;
using Puck.Attestation;
using Puck.World.Protocol;

namespace Puck.World.Presentation.Tests;

/// <summary>Local signing identities for federation laws: a throwaway key per subject, the admission row a peer
/// authors to trust it, and an authenticator that proves and verifies at one authored instant.</summary>
internal static class FederationSigning {
    /// <summary>The instant every claim these laws sign is signed at and every door verifies it at, so a claim's
    /// validity window is judged against an instant the law authored, never against how long the law took between
    /// signing and verifying.</summary>
    public static readonly Func<DateTimeOffset> ClaimNow = static () => AdmissionWireFixture.ClaimInstant;

    /// <summary>A federation door's authenticator, verifying at <see cref="ClaimNow"/>.</summary>
    /// <param name="trustEntries">The admission rows this door trusts.</param>
    /// <param name="oracle">The identity this side proves with, or <see langword="null"/> for a verify-only door.</param>
    /// <returns>The authenticator.</returns>
    public static WorldAttestedAuthenticator Authenticator(Func<IReadOnlyList<WorldAdmissionEntry>?> trustEntries, ISigningOracle? oracle = null) => new(
        now: ClaimNow,
        oracle: oracle,
        trustEntries: trustEntries
    );
    /// <summary>A fresh, throwaway <see cref="LocalKeySigningOracle"/> for one test's own SignsDirectly identity,
    /// signing at <see cref="ClaimNow"/>.</summary>
    /// <param name="subject">The authority the oracle signs as.</param>
    /// <returns>The oracle, which the caller disposes.</returns>
    public static LocalKeySigningOracle LocalOracle(string subject) => new(
        key: ECDsa.Create(curve: ECCurve.NamedCurves.nistP256),
        now: ClaimNow,
        subject: subject,
        validity: TimeSpan.FromMinutes(value: 5)
    );
    /// <summary>The SignsDirectly admission row a peer must author to trust <paramref name="oracle"/>'s own key.</summary>
    /// <param name="oracle">The identity to trust.</param>
    /// <returns>The admission row.</returns>
    public static WorldAdmissionEntry TrustEntryFor(LocalKeySigningOracle oracle) => new(
        Domain: oracle.Domain,
        Subject: oracle.Subject,
        Mode: WorldAdmissionTrustMode.SignsDirectly,
        Algorithm: AttestationAlgorithms.EcdsaP256Sha256,
        PublicKey: Convert.ToBase64String(inArray: oracle.PublicKeySubjectPublicKeyInfo),
        Grants: []
    );
}
