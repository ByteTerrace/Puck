using Puck.Confinement;

namespace Puck.Launcher.Release;

/// <summary>The outcome of <see cref="IUpdateApplier.Apply"/>.</summary>
/// <param name="Applied">Whether the version is now current.</param>
/// <param name="PreviousVersion">The version that was current before this apply, the rollback target when a recovering
/// apply found its version already current, or <see langword="null"/> on a first install.</param>
/// <param name="RefusalReason">Why apply refused, when <paramref name="Applied"/> is <see langword="false"/>.</param>
public sealed record UpdateApplyResult(bool Applied, string? PreviousVersion, string? RefusalReason) {
    /// <summary>Builds a terminal refusal. Temporary filesystem unavailability throws, retaining any pending intent.</summary>
    /// <param name="reason">Why apply refused.</param>
    /// <returns>The refusal.</returns>
    public static UpdateApplyResult Refuse(string reason) => new(
        Applied: false,
        PreviousVersion: null,
        RefusalReason: reason
    );
}
/// <summary>
/// Applies an already-staged version: re-verifies its files by hash (never trusts an unverified directory), makes them
/// durable, and installs it as current through <see cref="InstallTransaction.CommitApply"/> — the ONE update model,
/// selection by the release record rather than in-place replacement, so nothing this process has already loaded is ever
/// touched. The new selection is only ever observed by the stub at its NEXT launch.
/// </summary>
public interface IUpdateApplier {
    /// <summary>Applies <paramref name="rid"/>'s staged payload from <paramref name="manifest"/>.</summary>
    /// <param name="manifest">The verified, already-staged manifest.</param>
    /// <param name="rid">The .NET runtime identifier staged under the install root's <c>versions/&lt;version&gt;/</c>.</param>
    /// <param name="transaction">The caller's transaction on the install root the payload was staged under; held across
    /// staging and this call, never ended here.</param>
    /// <param name="recoverInterruptedApply">Permit the already-current version only while recovering a durable,
    /// exact-manifest install reservation. Files and the install record are rechecked.</param>
    /// <returns>Whether the version is current, and what it replaced.</returns>
    UpdateApplyResult Apply(ReleaseManifest manifest, string rid, InstallTransaction transaction, bool recoverInterruptedApply = false);
}
