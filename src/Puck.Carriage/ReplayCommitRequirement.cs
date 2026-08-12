namespace Puck.Carriage;

/// <summary>
/// The replay-state mutation required before a verified sequenced claim may take effect. Verification is
/// pure: it returns this value but never reads or writes the receiver's store. The receiver must compare
/// and advance <see cref="Sequence"/> atomically with the claim's semantic effect.
/// </summary>
/// <param name="Domain">The issuing domain.</param>
/// <param name="Subject">The claim subject.</param>
/// <param name="EpochStartUnixSeconds">The signed-window replay epoch.</param>
/// <param name="RetainThroughUnixSeconds">The inclusive minimum retention deadline.</param>
/// <param name="Sequence">The proposed high-water sequence.</param>
public sealed record ReplayCommitRequirement(
    string Domain,
    string Subject,
    long EpochStartUnixSeconds,
    long RetainThroughUnixSeconds,
    ulong Sequence
);
