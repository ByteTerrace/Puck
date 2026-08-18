using Puck.Assets;

namespace Puck.Launcher.Release;

/// <summary>The <see cref="IUpdateStager"/> every <c>AddSelfUpdate</c> registration wires: <see cref="IReleaseSource"/>
/// for transport, a <see cref="ContentAddressedStore"/> rooted at <c>&lt;cacheRoot&gt;/objects/</c> for the
/// download/verify/dedup cache, and <c>&lt;cacheRoot&gt;/versions/&lt;version&gt;/</c> as the staged directory a
/// future <c>Puck.Platform.IUpdateApplier</c> points the stub at.</summary>
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

        var payload = manifest.Payloads.FirstOrDefault(predicate: candidate => string.Equals(a: candidate.Rid, b: rid, comparisonType: StringComparison.Ordinal));

        if (payload is null) {
            return UpdateStageResult.Refuse(reason: $"manifest for version '{manifest.Version}' declares no payload for rid '{rid}'");
        }

        var downloaded = 0;
        var reused = 0;

        foreach (var file in payload.Files) {
            if (m_cache.Contains(hash: file.Hash)) {
                reused++;

                continue;
            }

            using var buffer = new MemoryStream();
            var found = await m_source.TryGetFileAsync(hash: file.Hash, destination: buffer, cancellationToken: cancellationToken).ConfigureAwait(continueOnCapturedContext: false);

            if (!found) {
                return UpdateStageResult.Refuse(reason: $"file '{file.Path}' (hash {file.Hash}) could not be fetched");
            }

            var bytes = buffer.ToArray();
            var actualHash = $"sha256/{ContentAddressedStore.ComputeHash(content: bytes)}";

            if (!string.Equals(a: actualHash, b: file.Hash, comparisonType: StringComparison.Ordinal)) {
                return UpdateStageResult.Refuse(reason: $"file '{file.Path}' fetched with hash {actualHash}, expected {file.Hash} — refused rather than staged");
            }

            _ = m_cache.Put(content: bytes);
            downloaded++;
        }

        var versionDirectory = Path.Combine(path1: m_cacheRoot, path2: "versions", path3: manifest.Version);

        foreach (var file in payload.Files) {
            if (!m_cache.TryGet(hash: file.Hash, content: out var bytes)) {
                return UpdateStageResult.Refuse(reason: $"file '{file.Path}' (hash {file.Hash}) is missing from the cache after staging — refused rather than writing a partial install");
            }

            var actualHash = $"sha256/{ContentAddressedStore.ComputeHash(content: bytes)}";

            if (!string.Equals(a: actualHash, b: file.Hash, comparisonType: StringComparison.Ordinal)) {
                return UpdateStageResult.Refuse(reason: $"file '{file.Path}' re-verified with hash {actualHash} at staging time, expected {file.Hash} — refused");
            }

            var destinationPath = Path.Combine(path1: versionDirectory, path2: file.Path.Replace(newChar: Path.DirectorySeparatorChar, oldChar: '/'));

            _ = Directory.CreateDirectory(path: Path.GetDirectoryName(path: destinationPath)!);
            await File.WriteAllBytesAsync(bytes: bytes, cancellationToken: cancellationToken, path: destinationPath).ConfigureAwait(continueOnCapturedContext: false);
        }

        return new UpdateStageResult(FilesDownloaded: downloaded, FilesReused: reused, RefusalReason: null, Staged: true, StagedPath: versionDirectory);
    }
}
