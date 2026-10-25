using System.Globalization;
using Puck.Assets;

namespace Puck.Launcher.Release;

/// <summary>
/// The one cross-platform <see cref="IUpdateApplier"/>: every pointer it writes goes through
/// <see cref="AtomicFile"/>, whose temp-then-rename is atomic on both NTFS and POSIX filesystems, so nothing
/// OS-specific is needed once the update model is a pointer swap rather than an in-place executable/DLL replacement.
/// </summary>
public sealed class FileUpdateApplier : IUpdateApplier {
    private readonly Lock m_gate = new();

    /// <inheritdoc/>
    public UpdateApplyResult Apply(ReleaseManifest manifest, string rid, string cacheRoot, bool recoverInterruptedApply = false) {
        lock (m_gate) {
            return ApplyCore(cacheRoot: cacheRoot, manifest: manifest, recoverInterruptedApply: recoverInterruptedApply, rid: rid);
        }
    }

    private static UpdateApplyResult ApplyCore(ReleaseManifest manifest, string rid, string cacheRoot, bool recoverInterruptedApply) {
        ArgumentNullException.ThrowIfNull(argument: manifest);
        ArgumentException.ThrowIfNullOrWhiteSpace(argument: rid);
        ArgumentException.ThrowIfNullOrWhiteSpace(argument: cacheRoot);

        var errors = ReleaseCanonicalizer.Validate(document: manifest);

        if (errors.Count != 0) {
            return UpdateApplyResult.Refuse(reason: $"manifest fails structural validation: {errors[0]}");
        }

        var payload = manifest.Payloads.FirstOrDefault(predicate: candidate => string.Equals(
            a: candidate.Rid,
            b: rid,
            comparisonType: StringComparison.Ordinal
        ));

        if (payload is null) {
            return UpdateApplyResult.Refuse(reason: $"manifest for version '{manifest.Version}' declares no payload for rid '{rid}'");
        }

        var root = Path.GetFullPath(path: cacheRoot);
        var versionDirectory = Path.Combine(
            path1: root,
            path2: "versions",
            path3: manifest.Version
        );

        if (!Directory.Exists(path: versionDirectory)) {
            return UpdateApplyResult.Refuse(reason: $"'{versionDirectory}' is not staged — stage before applying");
        }

        foreach (var file in payload.Files) {
            var filePath = Path.Combine(
                path1: versionDirectory,
                path2: file.Path
            );

            if (!File.Exists(path: filePath)) {
                return UpdateApplyResult.Refuse(reason: $"staged file '{file.Path}' is missing from '{versionDirectory}' — refused rather than applied");
            }

            var actualHash = ContentPin.OfFile(path: filePath).ToString();

            if (!string.Equals(
                a: actualHash,
                b: file.Hash,
                comparisonType: StringComparison.Ordinal
            )) {
                return UpdateApplyResult.Refuse(reason: $"staged file '{file.Path}' hash {actualHash} does not match the manifest's {file.Hash} — refused rather than applied");
            }
        }

        var currentPath = Path.Combine(
            path1: root,
            path2: "current"
        );
        var previousVersion = (File.Exists(path: currentPath)
            ? File.ReadAllText(path: currentPath).Trim()
            : null
        );

        if (previousVersion is { Length: > 0 }) {
            var lastGoodPath = Path.Combine(path1: root, path2: "last-good");
            var lastGoodVersion = (File.Exists(path: lastGoodPath) ? File.ReadAllText(path: lastGoodPath).Trim() : null);

            if (recoverInterruptedApply && (previousVersion == manifest.Version)) {
                var generationPath = Path.Combine(path1: versionDirectory, path2: "state-generation");

                if (!File.Exists(path: generationPath) || (File.ReadAllText(path: generationPath) != manifest.StateGeneration.ToString(provider: CultureInfo.InvariantCulture))) {
                    return UpdateApplyResult.Refuse(reason: "the current install's state generation does not match its pending intent");
                }
                return new UpdateApplyResult(Applied: true, PreviousVersion: lastGoodVersion, RefusalReason: null);
            }
            // InstalledVersion belongs to the running process. A previous apply may already have advanced this
            // pointer without restarting it, so the on-disk version is the final downgrade gate.
            if (!ReleaseVersion.IsStrictlyGreaterThan(left: manifest.Version, right: previousVersion)) {
                return UpdateApplyResult.Refuse(reason: $"version '{manifest.Version}' does not exceed the current install '{previousVersion}'");
            }
            if (string.Equals(a: manifest.Version, b: previousVersion, comparisonType: StringComparison.OrdinalIgnoreCase) ||
                (!string.IsNullOrEmpty(value: lastGoodVersion) && string.Equals(a: manifest.Version, b: lastGoodVersion, comparisonType: StringComparison.OrdinalIgnoreCase))) {
                return UpdateApplyResult.Refuse(reason: $"version '{manifest.Version}' collides case-insensitively with an active install directory");
            }

            // The stub leaves this marker until the selected process reports healthy. A newer apply must
            // preserve the last proven version while the previous install has not completed startup.
            if (!File.Exists(path: Path.Combine(path1: root, path2: "state", path3: "health.json"))) {
                AtomicFile.WriteAllText(
                path: Path.Combine(
                    path1: root,
                    path2: "last-good"
                ),
                contents: previousVersion
                );
            }
        }

        AtomicFile.WriteAllText(
            path: Path.Combine(path1: versionDirectory, path2: "state-generation"),
            contents: manifest.StateGeneration.ToString(provider: CultureInfo.InvariantCulture)
        );

        // Publish pending health first: two updates before a stub launch must not promote an unstarted version.
        HealthGateFile.MarkPending(cacheRoot: root, version: manifest.Version);
        AtomicFile.WriteAllText(
            path: currentPath,
            contents: manifest.Version
        );

        return new UpdateApplyResult(
            Applied: true,
            PreviousVersion: previousVersion,
            RefusalReason: null
        );
    }
}
