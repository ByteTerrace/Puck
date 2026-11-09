using Puck.Assets;
using Puck.Confinement;

namespace Puck.Launcher.Release;

/// <summary>
/// The one cross-platform <see cref="IUpdateApplier"/>: it verifies every staged file against the manifest, flushes the
/// staged files and (on Linux) the version's directories to stable storage, computes the payload's identity
/// (<see cref="ReleasePayloadIdentity"/>), and hands the install to <see cref="InstallTransaction.CommitApply"/>, which writes
/// the version's install record durably and then commits the release record naming it current. A power loss therefore
/// never leaves the record selecting a version whose files or install record are not on disk.
/// </summary>
public sealed class FileUpdateApplier : IUpdateApplier {
    /// <inheritdoc/>
    public UpdateApplyResult Apply(ReleaseManifest manifest, string rid, InstallTransaction transaction, bool recoverInterruptedApply = false) {
        ArgumentNullException.ThrowIfNull(argument: manifest);
        ArgumentException.ThrowIfNullOrWhiteSpace(argument: rid);
        ArgumentNullException.ThrowIfNull(argument: transaction);

        var errors = ReleaseCanonicalizer.Validate(document: manifest);

        if (errors.Count != 0) {
            return UpdateApplyResult.Refuse(reason: $"manifest fails structural validation: {errors[0]}");
        }
        var payload = manifest.Payloads.FirstOrDefault(predicate: candidate => string.Equals(a: candidate.Rid, b: rid, comparisonType: StringComparison.Ordinal));

        if (payload is null) {
            return UpdateApplyResult.Refuse(reason: $"manifest for version '{manifest.Version}' declares no payload for rid '{rid}'");
        }
        var versionDirectory = Path.Combine(path1: transaction.RootPath, path2: InstallLayout.VersionsDirectoryName, path3: manifest.Version);

        if (!Directory.Exists(path: versionDirectory)) {
            return UpdateApplyResult.Refuse(reason: $"'{versionDirectory}' is not staged — stage before applying");
        }
        var previousVersion = transaction.Policy.Current;
        // A version that is already current may be running: its files are rehashed but never opened for writing.
        var alreadyCurrent = (recoverInterruptedApply && string.Equals(a: previousVersion, b: manifest.Version, comparisonType: StringComparison.Ordinal));

        foreach (var file in payload.Files) {
            var filePath = Path.Combine(path1: versionDirectory, path2: file.Path);

            if (!File.Exists(path: filePath)) {
                return UpdateApplyResult.Refuse(reason: $"staged file '{file.Path}' is missing from '{versionDirectory}' — refused rather than applied");
            }
            var actualHash = (alreadyCurrent ? ContentPin.OfFile(path: filePath) : HashAndFlush(path: filePath)).ToString();

            if (!string.Equals(a: actualHash, b: file.Hash, comparisonType: StringComparison.Ordinal)) {
                return UpdateApplyResult.Refuse(reason: $"staged file '{file.Path}' hash {actualHash} does not match the manifest's {file.Hash} — refused rather than applied");
            }
        }
        if (!alreadyCurrent) {
            using var versions = transaction.Root.Child(name: InstallLayout.VersionsDirectoryName);
            using var staged = versions?.Child(name: manifest.Version);

            staged?.FlushTree();
        }
        var refusal = transaction.CommitApply(files: [.. payload.Files.Select(selector: static file => file.Hash)], payloadPin: ReleasePayloadIdentity.Of(payload: payload,
            stateGeneration: manifest.StateGeneration), precedence: ReleaseVersion.Compare, record: out _, recovering: recoverInterruptedApply, rid: rid,
            stateGeneration: manifest.StateGeneration, version: manifest.Version);

        return ((refusal is not null)
            ? UpdateApplyResult.Refuse(reason: refusal)
            : new UpdateApplyResult(Applied: true, PreviousVersion: (alreadyCurrent ? transaction.RollbackTarget() : previousVersion), RefusalReason: null));
    }

    // The stager wrote the file with ordinary writes; the hash proves its content and the flush makes it durable before the
    // release record can select it.
    private static ContentPin HashAndFlush(string path) {
        using var stream = new FileStream(access: FileAccess.ReadWrite, mode: FileMode.Open, path: path, share: FileShare.Read);
        var pin = ContentPin.Compute(content: stream);

        stream.Flush(flushToDisk: true);
        return pin;
    }
}
