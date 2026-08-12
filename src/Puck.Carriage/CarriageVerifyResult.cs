namespace Puck.Carriage;

/// <summary>
/// The outcome of walking a carriage chain (README.md, "Signed carriage"). There is exactly one
/// verify path for everything — a key binding and a claim are both envelopes, verified by the same code —
/// so this is the one result type both produce.
/// </summary>
/// <remarks>
/// <para><b><see cref="Verified"/> is not admission.</b> It answers one question — did the signature, the
/// chain, the window, the audience, and the sequence all hold — and says NOTHING about whether the claim
/// may touch anything. Reach is deny-by-default (<see cref="TrustListEntry.Reach"/>): an entry may admit a
/// claim that reaches no slot at all, and a caller branching on the verdict alone would then let it
/// through. The field is deliberately not called <c>Accepted</c>, because "accepted" is a word that reads
/// as a decision already made.</para>
/// <para>Ask <see cref="Admits"/> instead whenever the question is "may this claim do X". The verifier
/// REPORTS reach and never enforces it: "a trust entry pins an id and says whether that key signs directly
/// or may vouch for others, plus which slots it reaches" (README.md) is an authored scope, and
/// whether a claim <i>counts</i> for a given slot stays the receiving world's policy (invariant 5). No
/// engine consumer exists yet — nothing in this repository calls the verifier from a world — so today
/// this type is the only place the distinction is stated at all.</para>
/// </remarks>
/// <param name="Verified">Whether the claim (or binding) verified and every policy check passed. NOT a decision to admit — see the remarks, and <see cref="Admits"/>.</param>
/// <param name="RefusalReason">Why verification refused, or <see langword="null"/> when <see cref="Verified"/> is <see langword="true"/>.</param>
/// <param name="Reach">
/// The slot names the trust entry that admitted this claim reaches (<see cref="TrustListEntry.Reach"/>),
/// or <see langword="null"/> when <see cref="Verified"/> is <see langword="false"/>. Deny by default: an
/// empty set is a verified claim that reaches no slot at all.
/// </param>
public readonly record struct CarriageVerifyResult(bool Verified, string? RefusalReason, IReadOnlySet<string>? Reach) {
    /// <summary>Builds a verified result carrying the admitting entry's authored slot reach.</summary>
    /// <param name="reach">The slot names the admitting trust entry reaches. Empty means the claim verified but reaches nothing.</param>
    public static CarriageVerifyResult Accept(IReadOnlySet<string> reach) => new(
        Verified: true,
        RefusalReason: null,
        Reach: reach
    );

    /// <summary>Builds a refused result carrying why. A refusal never carries reach — there is no verified claim to scope.</summary>
    /// <param name="reason">A human-readable refusal reason — never used for control flow, only for reporting.</param>
    public static CarriageVerifyResult Refuse(string reason) => new(
        Verified: false,
        RefusalReason: reason,
        Reach: null
    );

    /// <summary>
    /// Whether this claim both verified AND was admitted by an entry whose authored reach covers
    /// <paramref name="slot"/>. This is the question a receiving world actually has, and asking it in one
    /// call is what stops <see cref="Verified"/> being mistaken for the answer.
    /// </summary>
    /// <param name="slot">The slot name the caller wants to act on.</param>
    /// <returns><see langword="false"/> for a refused claim, for a claim whose entry reaches nothing, and for any slot outside that entry's authored set.</returns>
    public bool Admits(string slot) => (Verified && (Reach is not null) && Reach.Contains(item: slot));
}
