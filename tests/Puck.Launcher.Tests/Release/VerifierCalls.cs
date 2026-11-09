using Puck.Confinement;
using Puck.Launcher.Release;

namespace Puck.Launcher.Tests.Release;

/// <summary>Verification under a transaction on a fresh, empty install root, for laws about the signature, sequence,
/// canonical-hash and version checks alone, where no release record state is under test.</summary>
internal static class VerifierCalls {
    public static ReleaseVerifyOutcome Verify(this IReleaseVerifier verifier, ReleaseManifest manifest, DateTimeOffset now, string installedVersion, bool advanceSequence) =>
        InFreshRoot(call: transaction => verifier.Verify(advanceSequence: advanceSequence, installedVersion: installedVersion, manifest: manifest, now: now, transaction: transaction));
    public static ReleaseVerifyOutcome ReserveInstall(this IReleaseVerifier verifier, ReleaseManifest manifest, DateTimeOffset verifiedAt, string installedVersion) =>
        InFreshRoot(call: transaction => verifier.ReserveInstall(installedVersion: installedVersion, manifest: manifest, verifiedAt: verifiedAt, transaction: transaction));

    private static ReleaseVerifyOutcome InFreshRoot(Func<InstallTransaction, ReleaseVerifyOutcome> call) {
        var root = Directory.CreateTempSubdirectory(prefix: "puck-verify-");

        try {
            using var transaction = InstallTransaction.TryBegin(installRoot: root.FullName)!;

            return call(arg: transaction);
        } finally {
            try { root.Delete(recursive: true); } catch (IOException) { }
        }
    }
}
