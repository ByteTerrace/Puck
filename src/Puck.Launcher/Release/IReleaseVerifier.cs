namespace Puck.Launcher.Release;

/// <summary>A release authorization or replay-write decision. Only terminal refusals retire durable intents.</summary>
public enum ReleaseDecision {
    /// <summary>The same request cannot succeed without changing its authorization or content.</summary>
    TerminalRefusal,
    /// <summary>Every check passed and any requested durable write completed.</summary>
    Accepted,
    /// <summary>The request may succeed when contention or another temporary condition clears.</summary>
    TransientRefusal,
}
/// <summary>The outcome of verifying a <see cref="ReleaseManifest"/>.</summary>
/// <param name="Decision">Whether verification passed or refused transiently or terminally.</param>
/// <param name="RefusalReason">Why verification refused, or <see langword="null"/> when <see cref="Accepted"/> is <see langword="true"/>.</param>
public sealed record ReleaseVerifyOutcome(ReleaseDecision Decision, string? RefusalReason) {
    /// <summary>Whether every check passed.</summary>
    public bool Accepted => Decision == ReleaseDecision.Accepted;
    /// <summary>Builds an accepted outcome.</summary>
    public static ReleaseVerifyOutcome Accept() => new(
        Decision: ReleaseDecision.Accepted,
        RefusalReason: null
    );
    /// <summary>Builds a refused outcome.</summary>
    /// <param name="reason">A human-readable refusal reason — never used for control flow, only for reporting.</param>
    public static ReleaseVerifyOutcome Refuse(string reason) => new(
        Decision: ReleaseDecision.TerminalRefusal,
        RefusalReason: reason
    );
    /// <summary>Builds a transient refusal that retains a pending install intent.</summary>
    public static ReleaseVerifyOutcome Retry(string reason) => new(Decision: ReleaseDecision.TransientRefusal, RefusalReason: reason);
}
/// <summary>
/// Decides whether a fetched <see cref="ReleaseManifest"/> may be staged: its signature verifies against a pinned
/// trust anchor, its sequence exceeds the durable replay high-water mark, its content hash matches what was
/// actually signed, it names no revocation, it respects <see cref="ReleaseManifest.MinimumSupported"/>, and its
/// version strictly exceeds what is already installed.
/// </summary>
public interface IReleaseVerifier {
    /// <summary>Verifies and durably reserves the exact manifest for an install intent. Unlike ordinary
    /// verification, this can resume its own reservation, but cannot resume another manifest or an older mark.
    /// Call only while holding the install-root writer lease. Persist a new intent only after reservation succeeds.</summary>
    /// <param name="manifest">The signed manifest pinned by the local intent.</param>
    /// <param name="verifiedAt">The instant at which this local intent was originally authorized. Recovery
    /// checks the currently configured trust policy at that instant; expiry does not cancel already-authorized work.</param>
    /// <param name="installedVersion">The running version when the intent was authorized.</param>
    ReleaseVerifyOutcome ReserveInstall(ReleaseManifest manifest, DateTimeOffset verifiedAt, string installedVersion);
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
