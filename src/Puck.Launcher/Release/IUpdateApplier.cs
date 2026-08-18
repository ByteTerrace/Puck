namespace Puck.Launcher.Release;

/// <summary>The outcome of <see cref="IUpdateApplier.Apply"/>.</summary>
/// <param name="Applied">Whether the pointer swap happened.</param>
/// <param name="PreviousVersion">The version <c>current</c> named before this apply, or <see langword="null"/> on a
/// first install. Recorded as the new <c>last-good</c> whenever it is not <see langword="null"/>.</param>
/// <param name="RefusalReason">Why apply refused, when <paramref name="Applied"/> is <see langword="false"/>.</param>
public sealed record UpdateApplyResult(bool Applied, string? PreviousVersion, string? RefusalReason) {
    /// <summary>Builds a refused result.</summary>
    public static UpdateApplyResult Refuse(string reason) => new(Applied: false, PreviousVersion: null, RefusalReason: reason);
}
/// <summary>
/// Applies an already-staged version: re-verifies its files by hash (never trusts an unverified directory), writes
/// its <c>state-generation</c>, and atomically swaps the install's <c>current</c> pointer — the ONE update model,
/// selection-by-pointer rather than in-place replacement, so nothing this process has already loaded is ever
/// touched. The swap is only ever observed by the stub at its NEXT launch.
/// </summary>
public interface IUpdateApplier {
    /// <summary>Applies <paramref name="rid"/>'s staged payload from <paramref name="manifest"/>.</summary>
    /// <param name="manifest">The verified, already-staged manifest.</param>
    /// <param name="rid">The .NET runtime identifier staged under <c>cacheRoot/versions/&lt;version&gt;/</c>.</param>
    /// <param name="cacheRoot">The install root — the same directory <see cref="IUpdateStager"/> staged under and
    /// the stub reads <c>current</c>/<c>last-good</c>/<c>versions/</c> from.</param>
    UpdateApplyResult Apply(ReleaseManifest manifest, string rid, string cacheRoot);
}
