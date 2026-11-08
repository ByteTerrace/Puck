using Puck.Assets;

namespace Puck.Launcher.Release;

/// <summary>The <see cref="IUpdateStager"/> every <c>AddSelfUpdate</c> registration wires: <see cref="IReleaseSource"/>
/// for transport, a <see cref="ContentAddressedStore"/> rooted at <c>&lt;cacheRoot&gt;/objects/</c> for the
/// download/verify/dedup cache, and <c>&lt;cacheRoot&gt;/versions/&lt;version&gt;/</c> as the staged directory a
/// <see cref="IUpdateApplier"/> points the stub at.</summary>
/// <param name="source">The transport to fetch missing files through.</param>
/// <param name="cache">The content-addressed object cache.</param>
/// <param name="cacheRoot">The root directory staged versions live under.</param>
public sealed class ContentAddressedUpdateStager(IReleaseSource source, ContentAddressedStore cache, string cacheRoot) : IUpdateStager {
    private readonly ContentAddressedStore m_cache = cache;
    private readonly string m_cacheRoot = Path.GetFullPath(path: cacheRoot);
    private readonly IReleaseSource m_source = source;

    /// <inheritdoc/>
    public async Task<UpdateStageResult> StageAsync(ReleaseManifest manifest, string rid, CancellationToken cancellationToken) {
        ArgumentNullException.ThrowIfNull(argument: manifest);
        ArgumentException.ThrowIfNullOrWhiteSpace(argument: rid);

        var errors = ReleaseCanonicalizer.Validate(document: manifest);

        if (errors.Count != 0) {
            return UpdateStageResult.Refuse(reason: $"manifest fails structural validation: {errors[0]}");
        }

        var payload = manifest.Payloads.FirstOrDefault(predicate: candidate => string.Equals(
            a: candidate.Rid,
            b: rid,
            comparisonType: StringComparison.Ordinal
        ));

        if (payload is null) {
            return UpdateStageResult.Refuse(reason: $"manifest for version '{manifest.Version}' declares no payload for rid '{rid}'");
        }

        var downloaded = 0;
        var reused = 0;

        foreach (var file in payload.Files) {
            if (!ContentPin.TryParse(
                pin: out var pin,
                text: file.Hash
            )) {
                return UpdateStageResult.Refuse(reason: $"file '{file.Path}' names '{file.Hash}', which is not a well-formed content pin");
            }
            if (m_cache.Contains(pin: pin)) {
                reused++;

                continue;
            }

            using var buffer = new MemoryStream();
            var found = await m_source.TryGetFileAsync(
                cancellationToken: cancellationToken,
                destination: buffer,
                length: file.Size,
                pin: pin
            ).ConfigureAwait(continueOnCapturedContext: false);

            if (!found) {
                return UpdateStageResult.Refuse(reason: $"file '{file.Path}' (hash {file.Hash}) could not be fetched");
            }

            var bytes = buffer.ToArray();
            var actualHash = ContentPin.Compute(content: bytes);

            if ((bytes.LongLength != file.Size) || (actualHash != pin)) {
                return UpdateStageResult.Refuse(reason: $"file '{file.Path}' fetched with hash {actualHash}, expected {file.Hash} — refused rather than staged");
            }

            _ = m_cache.Put(content: bytes);
            downloaded++;
        }

        var versionDirectory = Path.Combine(
            path1: m_cacheRoot,
            path2: "versions",
            path3: manifest.Version
        );

        // A previous attempt can stop after writing files but before UpdateService persists an install intent.
        // A later signed manifest for this version may omit some of those files. Its payload is the complete
        // file set, so rebuild the inactive directory instead of inheriting undeclared executable content.
        // The caller holds the install lease and has excluded the current version before entering staging. The removal is
        // a prune's: refused while a process runs from the directory, and never through a link.
        try {
            _ = InstallPruner.RemoveVersion(installRoot: m_cacheRoot, version: manifest.Version);
        } catch (IOException error) {
            return UpdateStageResult.Refuse(reason: $"the earlier staging of '{manifest.Version}' cannot be replaced: {error.Message}");
        }

        foreach (var file in payload.Files) {
            if (!ContentPin.TryParse(
                pin: out var pin,
                text: file.Hash
            )) {
                return UpdateStageResult.Refuse(reason: $"file '{file.Path}' names '{file.Hash}', which is not a well-formed content pin");
            }

            // The store verifies an object as it reads it, so an object that is present yet unreadable is corrupt: it is
            // deleted, so the next attempt finds the pin missing and downloads it again rather than refusing forever.
            if (!m_cache.TryGet(
                content: out var bytes,
                pin: pin
            )) {
                if (m_cache.Contains(pin: pin)) {
                    File.Delete(path: ContentAddressedStore.ObjectPath(root: m_cache.Root, pin: pin));

                    return UpdateStageResult.Refuse(reason: $"file '{file.Path}' (hash {file.Hash}) was corrupt in the cache and has been deleted — refused, and the next attempt downloads it again");
                }

                return UpdateStageResult.Refuse(reason: $"file '{file.Path}' (hash {file.Hash}) is missing from the cache after staging — refused rather than writing a partial install");
            }

            if (bytes.LongLength != file.Size) {
                return UpdateStageResult.Refuse(reason: $"file '{file.Path}' holds {bytes.LongLength} bytes at staging time, but the manifest declares {file.Size} — refused");
            }

            var destinationPath = Path.Combine(
                path1: versionDirectory,
                path2: file.Path
            );

            _ = Directory.CreateDirectory(path: Path.GetDirectoryName(path: destinationPath)!);
            await File.WriteAllBytesAsync(
                bytes: bytes,
                cancellationToken: cancellationToken,
                path: destinationPath
            ).ConfigureAwait(continueOnCapturedContext: false);
        }

        return new UpdateStageResult(
            FilesDownloaded: downloaded,
            FilesReused: reused,
            RefusalReason: null,
            Staged: true,
            StagedPath: versionDirectory
        );
    }
}
