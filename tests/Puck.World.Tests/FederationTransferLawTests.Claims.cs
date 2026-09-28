using System.Security.Cryptography;
using Puck.World.Protocol;

namespace Puck.World.Tests;

public sealed partial class FederationTransferLawTests {
    // The instant every claim these laws sign is signed at and every door verifies it at, so a claim's validity window
    // is judged against an instant the law authored, never against how long the law took between signing and verifying.
    private static readonly Func<DateTimeOffset> ClaimNow = static () => AdmissionWireFixture.ClaimInstant;

    /// <summary>A fresh, throwaway <see cref="LocalKeySigningOracle"/> for one test's own SignsDirectly identity,
    /// signing at <see cref="ClaimNow"/>.</summary>
    private static LocalKeySigningOracle LocalOracle(string subject) => new(
        key: ECDsa.Create(curve: ECCurve.NamedCurves.nistP256),
        now: ClaimNow,
        subject: subject,
        validity: TimeSpan.FromMinutes(value: 5)
    );
    /// <summary>A federation door's authenticator, verifying at <see cref="ClaimNow"/>.</summary>
    private static WorldAttestedAuthenticator Authenticator(Func<IReadOnlyList<WorldAdmissionEntry>?> trustEntries, ISigningOracle? oracle = null) => new(
        now: ClaimNow,
        oracle: oracle,
        trustEntries: trustEntries
    );
}
