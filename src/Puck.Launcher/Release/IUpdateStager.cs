namespace Puck.Launcher.Release;

/// <summary>The outcome of staging one RID's payload.</summary>
/// <param name="Staged">Whether every file landed under <paramref name="StagedPath"/> and verified.</param>
/// <param name="StagedPath">The directory the payload was staged under, when <paramref name="Staged"/> is <see langword="true"/>.</param>
/// <param name="FilesDownloaded">How many files were actually fetched through the <see cref="IReleaseSource"/>.</param>
/// <param name="FilesReused">How many files were already present in the content-addressed cache and never re-fetched.</param>
/// <param name="RefusalReason">Why staging failed, when <paramref name="Staged"/> is <see langword="false"/>.</param>
public sealed record UpdateStageResult(bool Staged, string? StagedPath, int FilesDownloaded, int FilesReused, string? RefusalReason) {
    /// <summary>Builds a refused result.</summary>
    public static UpdateStageResult Refuse(string reason) => new(FilesDownloaded: 0, FilesReused: 0, RefusalReason: reason, Staged: false, StagedPath: null);
}
/// <summary>
/// Stages one RID's verified payload under <c>&lt;cacheRoot&gt;/versions/&lt;version&gt;/</c>: every file's content
/// hash is checked against a local <see cref="Puck.Assets.ContentAddressedStore"/> cache first (an already-cached
/// hash is never re-fetched, regardless of which earlier version last downloaded it), a cache miss is fetched
/// through the source and its hash re-verified before entering the cache, and every file — cached or freshly
/// fetched — is re-verified once more as it is written into the staged directory. This is distinct from
/// <c>Puck.Platform.IUpdateApplier</c>: staging never touches which version the stub currently runs.
/// </summary>
public interface IUpdateStager {
    /// <summary>Stages <paramref name="rid"/>'s payload from <paramref name="manifest"/>.</summary>
    /// <param name="manifest">The verified manifest to stage a payload from.</param>
    /// <param name="rid">The .NET runtime identifier to stage.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    Task<UpdateStageResult> StageAsync(ReleaseManifest manifest, string rid, CancellationToken cancellationToken);
}
