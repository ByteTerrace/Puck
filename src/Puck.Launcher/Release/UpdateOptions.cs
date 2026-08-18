namespace Puck.Launcher.Release;

/// <summary>
/// The OPERATIONAL configuration <c>AddSelfUpdate</c> takes as a parameter: channel, release-source endpoint, cache
/// root, check interval, keep-N-versions, and the build-pinned <see cref="TrustAnchor"/> — never parsed from a
/// document itself. Each composition root sources the operational fields from its own durable document (an
/// <c>update</c> section's operational fields, for a game client) and supplies <see cref="TrustAnchor"/> as a
/// composition-root constant, never a synced document field.
/// </summary>
/// <param name="App">The application id this install runs (e.g. <c>puck.world</c>).</param>
/// <param name="Channel">The release channel to track.</param>
/// <param name="CacheRoot">The on-disk root staged versions and update state live under.</param>
/// <param name="TrustAnchor">The release channel's root trust anchor, compiled in at build time.</param>
/// <param name="InstalledVersion">The version this running binary reports itself as, for <c>update.check</c>'s
/// version-monotonicity comparison. Before <c>Puck.Platform.IUpdateApplier</c> lands, this is the composition
/// root's own compiled-in version; once staging is real, the composition root reads it from local install state
/// instead (the version the stub most recently launched).</param>
/// <param name="CheckInterval">How often an automatic check runs. <see langword="null"/> or non-positive disables automatic checking; <c>update.check</c> still works manually.</param>
/// <param name="KeepVersions">The number of most-recent staged versions to retain beyond the current one.</param>
/// <param name="InstallId">The 16-byte, hex-encoded rollout-bucketing identity. When <see langword="null"/>, one is minted via <see cref="System.Security.Cryptography.RandomNumberGenerator"/> and persisted at <c>&lt;cacheRoot&gt;/install-id</c> on first use.</param>
/// <param name="ReplayAcceptanceHorizon">The verifier-wide finite replay horizon <c>H</c> — bounds how stale a
/// manifest the verifier considers signature-valid, and defines the sequence-mark epoch length. <see langword="null"/>
/// selects <see cref="AddSelfUpdateDefaults.ReplayAcceptanceHorizon"/>.</param>
public sealed record UpdateOptions(
    string App,
    string Channel,
    string CacheRoot,
    ReleaseTrustAnchor TrustAnchor,
    string InstalledVersion,
    TimeSpan? CheckInterval = null,
    int KeepVersions = 2,
    string? InstallId = null,
    TimeSpan? ReplayAcceptanceHorizon = null
);
/// <summary>Default values <c>AddSelfUpdate</c> falls back to when <see cref="UpdateOptions"/> leaves a field unauthored.</summary>
public static class AddSelfUpdateDefaults {
    /// <summary>The default verifier-wide replay horizon <c>H</c> when <see cref="UpdateOptions.ReplayAcceptanceHorizon"/> is unset.</summary>
    public static readonly TimeSpan ReplayAcceptanceHorizon = TimeSpan.FromDays(days: 30);
}
