namespace Puck.Maths.Tests;

/// <summary>What one leg of a gate statement stands on. Where legs share code, agreement proves everything EXCEPT the
/// shared part, which is what <see cref="SharedSubstrate"/> exists to say out loud.</summary>
internal enum LegKind {
    /// <summary>Agreement with the presented charged algebra object, where the two sides share no code and no rounding
    /// substrate.</summary>
    PresentedTwin,
    /// <summary>Agreement with a shared-nothing oracle: an independently authored computation — typically in
    /// <see cref="Oracles"/> over <c>BigInteger</c>, or a published or hand-derived reference — sharing no code and no
    /// rounding substrate with the subject.</summary>
    Classical,
    /// <summary>Agreement with a SECOND SHIPPED IMPLEMENTATION in this tree that shares no code and no rounding
    /// substrate with the subject. Classical-grade evidence: the two answers were reached independently. It is spelled
    /// apart from <see cref="Classical"/> because a shipped kernel can be transcribed from the subject and because it
    /// carries an envelope dependency — the leg names where that second implementation is itself pinned.</summary>
    InTreeIndependent,
    /// <summary>Agreement where the two sides share code, a rounding kernel, or the DERIVATION itself. Always
    /// flavored.</summary>
    SharedSubstrate,
    /// <summary>Not an agreement between two computations at all: purity, refusal, an identity element, a certificate
    /// shape, a closure licence, a measured floor. Honest self-standing pinning.</summary>
    Structural,
}
/// <summary>WHICH sharing a <see cref="LegKind.SharedSubstrate"/> leg admits to.</summary>
internal enum ShareFlavor {
    /// <summary>Not a shared-substrate leg.</summary>
    None,
    /// <summary>Both sides round through a house fused rounding kernel — the same member, or two sibling copies of one
    /// shape. The leg NAMES both so a reader can see which.</summary>
    FusedSubstrate,
    /// <summary>Both sides call the same EXACT, non-rounding kernel. Condition (B) is vacuous here; condition (A) is
    /// discharged by the leg's citation, which names where that kernel is independently pinned.</summary>
    SharedExactKernel,
    /// <summary>One side outright delegates to or wraps the other side's code. Carriage-only: agreement proves the
    /// wiring and never the kernel, so the leg cites the delegated-to kernel's own independent evidence.</summary>
    DelegationTwin,
    /// <summary>The reference TRANSCRIBES a rule the subject's own code carries — the same recursion, the same rounding
    /// schedule, the same table — or is built from the subject's own output. No code is shared and the derivation is,
    /// so a shared error cancels: agreement proves faithful carriage and never that the rule is right. Condition (C):
    /// never independent evidence, which is why the leg names the witness that IS.</summary>
    Transcription,
    /// <summary>Both sides live inside the presented world: one kernel read at another entry point, another
    /// presentation, or another material.</summary>
    IntraPresented,
    /// <summary>Both sides are built from a common upstream computation neither owns — a shared input derivation rather
    /// than a shared kernel. Neither side wraps the other and neither is the presented object read twice.</summary>
    SharedUpstream,
}
/// <summary>
/// One leg of one gate statement: what stands against what, what the two share, and where the evidence the leg leans on
/// lives. Legs are materialized from the <c>laws/*.json</c> declarations by <see cref="LegText.ToLeg"/>, and
/// <c>LawLegTests.LawLegsAreDeclared</c> rejects the illegal combinations: a flavorless shared-substrate leg, a
/// flavored classical leg, a delegation twin with nothing cited, a relative canary with no absolute sibling.
/// </summary>
/// <param name="Kind">The leg kind.</param>
/// <param name="Flavor">The sharing flavor; <see cref="ShareFlavor.None"/> for every kind but shared-substrate.</param>
/// <param name="Subject">The computation under test, named by type and member.</param>
/// <param name="Against">What it is compared with; empty for a structural leg.</param>
/// <param name="Shared">What the two sides share; empty for every kind but shared-substrate.</param>
/// <param name="Citation">Where the evidence this leg DEPENDS on lives: the delegated-to kernel's independent pin, the
/// shared exact kernel's own pin, the second implementation's envelope, or a constant's provenance. Empty where the leg
/// depends on nothing beyond itself.</param>
/// <param name="Absolute">For a relative divergence canary, the statement carrying the ABSOLUTE sibling; empty
/// otherwise.</param>
internal readonly record struct Leg(LegKind Kind, ShareFlavor Flavor, string Subject, string Against, string Shared, string Citation, string Absolute) {
    /// <summary>The prefix identifying a documented claim that differs from observed behavior.</summary>
    public const string DocGapMarker = "DOC GAP:";

    /// <summary>Gets what the XML doc claims, for a doc-gap leg; empty for every other leg.</summary>
    public string Documented =>
        (IsDocGap
            ? Citation[DocGapMarker.Length..].Trim()
            : ""
        );
    /// <summary>Gets the sharing flavor as the kebab-cased token diagnostics use; empty when the leg
    /// is not shared-substrate.</summary>
    public string FlavorToken =>
        Flavor switch {
            ShareFlavor.FusedSubstrate => "fused-substrate",
            ShareFlavor.SharedExactKernel => "shared-exact-kernel",
            ShareFlavor.DelegationTwin => "delegation-twin",
            ShareFlavor.Transcription => "transcription",
            ShareFlavor.IntraPresented => "intra-presented",
            ShareFlavor.SharedUpstream => "shared-upstream",
            _ => "",
        };
    /// <summary>Gets whether this leg is an agreement between two computations rather than a self-standing statement.</summary>
    public bool IsAgreement =>
        (Kind != LegKind.Structural);
    /// <summary>Gets whether this leg pins behaviour as observed against the subject's own XML doc.</summary>
    public bool IsDocGap =>
        Citation.StartsWith(
            comparisonType: StringComparison.Ordinal,
            value: DocGapMarker
        );
    /// <summary>Gets whether this leg is INDEPENDENT evidence: the two sides share no code and no rounding substrate.</summary>
    public bool IsIndependent =>
        (Kind is LegKind.Classical or LegKind.PresentedTwin or LegKind.InTreeIndependent);
    /// <summary>Gets whether this leg is a relative divergence canary.</summary>
    public bool IsRelativeCanary =>
        ((Kind == LegKind.Structural) && (Absolute.Length > 0));
    /// <summary>Gets whether this leg is a faithful-carriage check of a transcription.</summary>
    public bool IsTranscription =>
        (Flavor == ShareFlavor.Transcription);
    /// <summary>Gets the leg kind as the kebab-cased token diagnostics use.</summary>
    public string KindToken =>
        Kind switch {
            LegKind.PresentedTwin => "presented-twin",
            LegKind.Classical => "classical",
            LegKind.InTreeIndependent => "in-tree-independent",
            LegKind.SharedSubstrate => "shared-substrate",
            _ => (IsRelativeCanary
            ? "relative-canary"
            : "structural"),
        };
}
/// <summary>The SHAPE a law combinator makes, reported by the combinator itself at entry so the gate can check the
/// declaration against what actually ran rather than against the case's name.</summary>
internal enum LawShape {
    /// <summary>Two computations required to agree, neither presented as the oracle.</summary>
    Twin,
    /// <summary>A twin that ran a THIRD LEG: an independently authored witness both sides were also required to equal,
    /// on the same operand stream. Reported only when the twin combinator was handed a non-null witness, so the
    /// declaration cannot claim an independent leg the run did not exercise.</summary>
    Witnessed,
    /// <summary>A subject against something passed in an oracle-shaped slot.</summary>
    OracleAgreement,
    /// <summary>Two disciplines required to DIFFER.</summary>
    Divergence,
    /// <summary>One computation compared with itself: purity, a round trip, an identity element, an algebraic
    /// symmetry.</summary>
    SelfContained,
    /// <summary>A claim body, which makes whatever statements it makes.</summary>
    Claim,
}
/// <summary>
/// Records which <see cref="LawShape"/>s a running law case actually exercised. Each combinator in <see cref="Laws"/>
/// reports at entry; <see cref="LawTests"/> observes one case's run and hands the set to the leg gate, which asserts
/// immediately in the same test. The state is thread-static and the observation is synchronous, so xUnit parallelism
/// cannot make the assertion vacuous or let two cases see each other's shapes.
/// </summary>
internal static class LawShapes {
    [ThreadStatic]
    private static int Observed;

    /// <summary>Tests whether an observed set contains a shape.</summary>
    /// <param name="observed">The set returned by <see cref="Observe"/>.</param>
    /// <param name="shape">The shape to test for.</param>
    /// <returns><see langword="true"/> when the shape was reported.</returns>
    public static bool Contains(int observed, LawShape shape) =>
        ((observed & (1 << ((int)shape))) != 0);
    /// <summary>Runs one law case and returns the set of shapes it reported.</summary>
    /// <param name="run">The case body.</param>
    /// <returns>The reported shapes as a bit set over <see cref="LawShape"/>.</returns>
    public static int Observe(Action run) {
        Observed = 0;

        run();

        return Observed;
    }
    /// <summary>Records that the running case exercised a combinator of this shape.</summary>
    /// <param name="shape">The combinator's shape.</param>
    public static void Report(LawShape shape) =>
        Observed |= (1 << ((int)shape));
}
