namespace Puck.Attestation;

/// <summary>
/// The outcome of walking an attestation chain (README.md, "Signed attestation"). There is exactly one
/// verify path for everything — a key binding and a claim are both attestations, verified by the same code —
/// so this is the one result type both produce.
/// </summary>
/// <remarks>
/// <para><b><see cref="Verified"/> is not admission.</b> It answers one question — did the signature, the
/// chain, the window, and the audience hold — and says NOTHING about whether the claim
/// may touch anything. Reach is deny-by-default (<see cref="TrustListEntry.Reach"/>): an entry may admit a
/// claim that reaches no slot at all, and a caller branching on the verdict alone would then let it
/// through. The field is deliberately not called <c>Accepted</c>, because "accepted" is a word that reads
/// as a decision already made.</para>
/// <para>Ask <see cref="Admits"/> for an unsequenced claim, or <see cref="TryGetReplayCommit"/> for a
/// sequenced claim, whenever the question is "may this claim do X". The result keeps reach encapsulated
/// and exposes only slot-scoped queries: the trust entry's reach (README.md §7, "Trust entries") is an
/// authored scope, while the receiving world's policy chooses which slot a proposed effect would touch
/// (invariant 5).</para>
/// </remarks>
public sealed class AttestationVerifyResult {
    private AttestationVerifyResult(
        bool verified,
        string? refusalReason,
        IReadOnlySet<string>? reach,
        ReplayCommitRequirement? replayCommit
    ) {
        Verified = verified;
        RefusalReason = refusalReason;
        Reach = reach;
        ReplayCommit = replayCommit;
    }

    /// <summary>The admitting trust entry's authored reach, or <see langword="null"/> after refusal.</summary>
    internal IReadOnlySet<string>? Reach { get; }
    /// <summary>The transaction requirement for a verified sequenced claim, or <see langword="null"/> for an unsequenced claim.</summary>
    internal ReplayCommitRequirement? ReplayCommit { get; }

    /// <summary>Why verification refused, or <see langword="null"/> after successful verification.</summary>
    public string? RefusalReason { get; }
    /// <summary>Whether this verified result is awaiting a receiver-side replay/effect transaction.</summary>
    public bool RequiresReplayCommit => (ReplayCommit is not null);
    /// <summary>Whether cryptographic verification and verifier policy succeeded. This is not an admission decision.</summary>
    public bool Verified { get; }

    /// <summary>Builds a verified result carrying the admitting entry's authored slot reach.</summary>
    /// <param name="reach">The slot names the admitting trust entry reaches. Empty means the claim verified but reaches nothing.</param>
    /// <param name="replayCommit">The replay-mark commit to transact with the claim's effect, or <see langword="null"/> for an unsequenced claim.</param>
    internal static AttestationVerifyResult Accept(IReadOnlySet<string> reach, ReplayCommitRequirement? replayCommit) => new(
        reach: reach,
        refusalReason: null,
        replayCommit: replayCommit,
        verified: true
    );
    /// <summary>Builds a refused result carrying why. A refusal never carries reach — there is no verified claim to scope.</summary>
    /// <param name="reason">A human-readable refusal reason — never used for control flow, only for reporting.</param>
    internal static AttestationVerifyResult Refuse(string reason) => new(
        reach: null,
        refusalReason: reason,
        replayCommit: null,
        verified: false
    );

    /// <summary>
    /// Whether this claim both verified AND was admitted by an entry whose authored reach covers
    /// <paramref name="slot"/>. This is the question a receiving world actually has, and asking it in one
    /// call is what stops <see cref="Verified"/> being mistaken for the answer.
    /// </summary>
    /// <param name="slot">The slot name the caller wants to act on.</param>
    /// <returns><see langword="false"/> for a refused claim, for a sequenced claim whose replay/effect
    /// transaction has not happened, for a claim whose entry reaches nothing, and for any slot outside that
    /// entry's authored set.</returns>
    public bool Admits(string slot) => (
        Verified &&
        (ReplayCommit is null) &&
        (Reach is not null) &&
        Reach.Contains(item: slot)
    );
    /// <summary>
    /// Gets the transaction requirement for a verified sequenced claim whose authored reach covers
    /// <paramref name="slot"/>. Returning a requirement is deliberately not called admission: the receiver
    /// must compare-and-advance it together with the semantic effect in one durable transaction.
    /// </summary>
    /// <param name="slot">The slot the eventual semantic effect would touch.</param>
    /// <param name="requirement">The required replay mutation when this method returns <see langword="true"/>.</param>
    /// <returns><see langword="true"/> only when verification succeeded, reach covers the slot, and a replay commit is required.</returns>
    public bool TryGetReplayCommit(string slot, out ReplayCommitRequirement? requirement) {
        if (
            Verified &&
            (ReplayCommit is not null) &&
            (Reach is not null) &&
            Reach.Contains(item: slot)
        ) {
            requirement = ReplayCommit;

            return true;
        }

        requirement = null;

        return false;
    }
}
