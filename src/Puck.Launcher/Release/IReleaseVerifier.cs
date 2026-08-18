namespace Puck.Launcher.Release;

/// <summary>The outcome of verifying a <see cref="ReleaseManifest"/>.</summary>
/// <param name="Accepted">Whether every check passed.</param>
/// <param name="RefusalReason">Why verification refused, or <see langword="null"/> when <paramref name="Accepted"/> is <see langword="true"/>.</param>
public sealed record ReleaseVerifyOutcome(bool Accepted, string? RefusalReason) {
    /// <summary>Builds an accepted outcome.</summary>
    public static ReleaseVerifyOutcome Accept() => new(Accepted: true, RefusalReason: null);
    /// <summary>Builds a refused outcome.</summary>
    /// <param name="reason">A human-readable refusal reason — never used for control flow, only for reporting.</param>
    public static ReleaseVerifyOutcome Refuse(string reason) => new(Accepted: false, RefusalReason: reason);
}
/// <summary>
/// Decides whether a fetched <see cref="ReleaseManifest"/> may be staged: its signature verifies against a pinned
/// trust anchor, its sequence exceeds the durable replay high-water mark, its content hash matches what was
/// actually signed, it names no revocation, it respects <see cref="ReleaseManifest.MinimumSupported"/>, and its
/// version strictly exceeds what is already installed.
/// </summary>
public interface IReleaseVerifier {
    /// <summary>Verifies <paramref name="manifest"/>. The replay-refusal check (does this sequence already fall at
    /// or below the stored mark) always runs regardless of <paramref name="advanceSequence"/> — a manifest is never
    /// accepted twice under the same sequence merely because an earlier call declined to commit it. Only the
    /// durable WRITE that raises the mark is conditional, so a read-only inspection
    /// (<c>UpdateService.CheckAsync</c>) can be repeated without permanently consuming the one sequence number a
    /// later <c>update.apply</c> still needs to commit.</summary>
    /// <param name="manifest">The parsed manifest, signature included.</param>
    /// <param name="now">The verification instant, captured once at the call's own boundary.</param>
    /// <param name="installedVersion">The currently installed version.</param>
    /// <param name="advanceSequence">Whether an otherwise-accepted manifest's sequence should be durably committed
    /// as the new high-water mark. <see langword="false"/> for a read-only check; <see langword="true"/> for the
    /// one verification an apply actually acts on.</param>
    ReleaseVerifyOutcome Verify(ReleaseManifest manifest, DateTimeOffset now, string installedVersion, bool advanceSequence);
}
