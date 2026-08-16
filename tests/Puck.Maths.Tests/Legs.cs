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
/// lives. The factories are the only constructors used, so illegal combinations — a flavorless shared-substrate leg, a
/// flavored classical leg, a delegation twin with nothing cited, a relative canary with no absolute sibling — cannot be
/// spelled.
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
    /// <summary>The prefix a doc-divergence leg opens its citation with. It is the ONE spelling the ledger's doc-gap
    /// register keys on, so it lives here — beside the factory that writes it — rather than in the renderer that reads
    /// it: a second spelling would empty the register silently, which is the exact failure this register exists to
    /// prevent.</summary>
    public const string DocGapMarker = "DOC GAP:";

    /// <summary>Agreement with a shared-nothing oracle.</summary>
    /// <param name="subject">The computation under test.</param>
    /// <param name="against">The independently authored reference.</param>
    /// <returns>The leg.</returns>
    public static Leg Classical(string subject, string against) =>
        new(Absolute: "", Against: against, Citation: "", Flavor: ShareFlavor.None, Kind: LegKind.Classical, Shared: "", Subject: subject);
    /// <summary>Agreement with a PUBLISHED or independently hand-derived constant table. Classical by provenance: the
    /// table was authored outside this tree's arithmetic. A constant captured from the subject's own output is a
    /// regression pin — <see cref="Structural"/> — and never this.</summary>
    /// <param name="subject">The computation under test.</param>
    /// <param name="table">The declared reference table.</param>
    /// <param name="provenance">Where the table's values come from; the parameter is required because provenance is
    /// what makes the leg classical rather than structural.</param>
    /// <returns>The leg.</returns>
    /// <exception cref="ArgumentException"><paramref name="provenance"/> is empty or white space. The kind this leg
    /// carries is indistinguishable from a plain classical one once built, so the demand is made HERE, where the
    /// spelling is still visible, rather than in a downstream declaration check that could no longer tell them apart.</exception>
    public static Leg PublishedConstant(string subject, string table, string provenance) {
        ArgumentException.ThrowIfNullOrWhiteSpace(argument: provenance);

        return new(Absolute: "", Against: table, Citation: provenance, Flavor: ShareFlavor.None, Kind: LegKind.Classical, Shared: "", Subject: subject);
    }
    /// <summary>Agreement with the presented charged algebra object.</summary>
    /// <param name="subject">The computation under test.</param>
    /// <param name="against">The presented-object side.</param>
    /// <returns>The leg.</returns>
    public static Leg PresentedTwin(string subject, string against) =>
        new(Absolute: "", Against: against, Citation: "", Flavor: ShareFlavor.None, Kind: LegKind.PresentedTwin, Shared: "", Subject: subject);
    /// <summary>Agreement with a second shipped implementation in this tree that shares no code and no rounding
    /// substrate with the subject.</summary>
    /// <param name="subject">The computation under test.</param>
    /// <param name="against">The second in-tree implementation.</param>
    /// <param name="envelope">The kernels that second implementation itself rests on, and where THEY are independently
    /// pinned; required, because the leg's trust is exactly that envelope's.</param>
    /// <returns>The leg.</returns>
    public static Leg InTreeIndependent(string subject, string against, string envelope) =>
        new(Absolute: "", Against: against, Citation: envelope, Flavor: ShareFlavor.None, Kind: LegKind.InTreeIndependent, Shared: "", Subject: subject);
    /// <summary>Agreement where both sides round through a house fused rounding kernel.</summary>
    /// <param name="subject">The computation under test.</param>
    /// <param name="against">The other side.</param>
    /// <param name="shared">The rounding kernel(s) both sides reach, named so a reader can see identity from
    /// siblinghood.</param>
    /// <returns>The leg.</returns>
    public static Leg FusedSubstrate(string subject, string against, string shared) =>
        new(Absolute: "", Against: against, Citation: "", Flavor: ShareFlavor.FusedSubstrate, Kind: LegKind.SharedSubstrate, Shared: shared, Subject: subject);
    /// <summary>Agreement where both sides call the same EXACT, non-rounding kernel.</summary>
    /// <param name="subject">The computation under test.</param>
    /// <param name="against">The other side.</param>
    /// <param name="shared">The exact kernel both sides reach.</param>
    /// <param name="envelope">Where that kernel is independently pinned; required, because nothing else in this leg
    /// pins it.</param>
    /// <returns>The leg.</returns>
    public static Leg SharedExactKernel(string subject, string against, string shared, string envelope) =>
        new(Absolute: "", Against: against, Citation: envelope, Flavor: ShareFlavor.SharedExactKernel, Kind: LegKind.SharedSubstrate, Shared: shared, Subject: subject);
    /// <summary>A CARRIAGE-ONLY agreement: one side delegates to or wraps the other's code, so agreement proves the
    /// wiring and never the kernel.</summary>
    /// <param name="subject">The computation under test.</param>
    /// <param name="against">The side it delegates to.</param>
    /// <param name="shared">The delegated-to body.</param>
    /// <param name="envelope">The delegated-to kernel's own independent evidence, or the classical pin it is OWED;
    /// required, because carriage proves nothing without it.</param>
    /// <returns>The leg.</returns>
    public static Leg DelegationTwin(string subject, string against, string shared, string envelope) =>
        new(Absolute: "", Against: against, Citation: envelope, Flavor: ShareFlavor.DelegationTwin, Kind: LegKind.SharedSubstrate, Shared: shared, Subject: subject);
    /// <summary>A FAITHFUL-CARRIAGE agreement: the reference transcribes a rule the subject's own code carries, or is
    /// built from the subject's own output, so agreement proves the carriage and never the rule.</summary>
    /// <param name="subject">The computation under test.</param>
    /// <param name="against">The transcribed reference.</param>
    /// <param name="transcribes">WHAT is transcribed, named on both sides so a reader can check the claim.</param>
    /// <param name="witness">The independent witness standing beside it, named; required, because a transcription that
    /// names none is a statement with no evidence under it. Where none stands, the leg says so in those words and names
    /// the owed item.</param>
    /// <returns>The leg.</returns>
    public static Leg FaithfulCarriage(string subject, string against, string transcribes, string witness) =>
        new(Absolute: "", Against: against, Citation: witness, Flavor: ShareFlavor.Transcription, Kind: LegKind.SharedSubstrate, Shared: transcribes, Subject: subject);
    /// <summary>Agreement where both sides live inside the presented world.</summary>
    /// <param name="subject">The computation under test.</param>
    /// <param name="against">The other side.</param>
    /// <param name="shared">The presented kernel, presentation or material both sides reach.</param>
    /// <returns>The leg.</returns>
    public static Leg IntraPresented(string subject, string against, string shared) =>
        new(Absolute: "", Against: against, Citation: "", Flavor: ShareFlavor.IntraPresented, Kind: LegKind.SharedSubstrate, Shared: shared, Subject: subject);
    /// <summary>Agreement where both sides are built from a common upstream computation neither owns.</summary>
    /// <param name="subject">The computation under test.</param>
    /// <param name="against">The other side.</param>
    /// <param name="shared">The upstream computation both sides consume.</param>
    /// <returns>The leg.</returns>
    public static Leg SharedUpstream(string subject, string against, string shared) =>
        new(Absolute: "", Against: against, Citation: "", Flavor: ShareFlavor.SharedUpstream, Kind: LegKind.SharedSubstrate, Shared: shared, Subject: subject);
    /// <summary>A self-standing statement: no second computation is involved.</summary>
    /// <param name="statement">What is pinned.</param>
    /// <returns>The leg.</returns>
    public static Leg Structural(string statement) =>
        new(Absolute: "", Against: "", Citation: "", Flavor: ShareFlavor.None, Kind: LegKind.Structural, Shared: "", Subject: statement);
    /// <summary>A self-standing statement pinning behaviour AS OBSERVED where the subject's own XML doc says something
    /// else. Structural — nothing is compared with a second computation — but spelled apart from
    /// <see cref="Structural"/> so the divergence lands in the ledger's doc-gap register by construction. Rule 4 forbids
    /// preserving a wrong ANSWER; what makes pinning-as-observed legitimate is that the divergence is carried forward
    /// where a reader can find it, and this factory is that carriage.</summary>
    /// <param name="statement">What the run pins, in the same voice as a structural leg.</param>
    /// <param name="documented">What the XML doc claims instead, quoted closely enough that a reader can check both
    /// sides; required, because a divergence that never says what the doc claims is a rumour.</param>
    /// <returns>The leg.</returns>
    /// <exception cref="ArgumentException"><paramref name="documented"/> is empty or white space. The demand is made
    /// HERE, where the spelling is still visible, rather than downstream where the leg is indistinguishable from a plain
    /// structural one.</exception>
    public static Leg PinnedAsObserved(string statement, string documented) {
        ArgumentException.ThrowIfNullOrWhiteSpace(argument: documented);

        return new(Absolute: "", Against: "", Citation: ((DocGapMarker + " ") + documented), Flavor: ShareFlavor.None, Kind: LegKind.Structural, Shared: "", Subject: statement);
    }
    /// <summary>A RELATIVE statement — two disciplines required to DIFFER — with the absolute sibling named beside it. A
    /// divergence canary cannot fail on a second rounding, so the absolute leg is part of the declaration. Classified
    /// structural: "these two must disagree somewhere" is not a twin.</summary>
    /// <param name="subject">The first discipline.</param>
    /// <param name="against">The second discipline, which it must differ from.</param>
    /// <param name="absolute">The statement carrying the absolute sibling, opening with its reference token: a law id
    /// or <c>owed:&lt;text&gt;</c>.</param>
    /// <returns>The leg.</returns>
    public static Leg RelativeCanary(string subject, string against, string absolute) =>
        new(Absolute: absolute, Against: against, Citation: "", Flavor: ShareFlavor.None, Kind: LegKind.Structural, Shared: "", Subject: subject);

    /// <summary>Gets whether this leg is an agreement between two computations rather than a self-standing statement.</summary>
    public bool IsAgreement =>
        (Kind != LegKind.Structural);
    /// <summary>Gets whether this leg is INDEPENDENT evidence: the two sides share no code and no rounding substrate.</summary>
    public bool IsIndependent =>
        (Kind is LegKind.Classical or LegKind.PresentedTwin or LegKind.InTreeIndependent);
    /// <summary>Gets whether this leg is a faithful-carriage check of a transcription.</summary>
    public bool IsTranscription =>
        (Flavor == ShareFlavor.Transcription);
    /// <summary>Gets whether this leg pins behaviour as observed against the subject's own XML doc.</summary>
    public bool IsDocGap =>
        Citation.StartsWith(comparisonType: StringComparison.Ordinal, value: DocGapMarker);
    /// <summary>Gets what the XML doc claims, for a doc-gap leg; empty for every other leg.</summary>
    public string Documented =>
        (IsDocGap ? Citation[DocGapMarker.Length..].Trim() : "");
    /// <summary>Gets whether this leg is a relative divergence canary.</summary>
    public bool IsRelativeCanary =>
        ((Kind == LegKind.Structural) && (Absolute.Length > 0));
    /// <summary>Gets the leg kind as the kebab-cased token the tool labels and the ledger use.</summary>
    public string KindToken =>
        Kind switch {
            LegKind.PresentedTwin => "presented-twin",
            LegKind.Classical => "classical",
            LegKind.InTreeIndependent => "in-tree-independent",
            LegKind.SharedSubstrate => "shared-substrate",
            _ => (IsRelativeCanary ? "relative-canary" : "structural"),
        };
    /// <summary>Gets the sharing flavor as the kebab-cased token the tool labels and the ledger use; empty when the leg
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
    private static int t_observed;

    /// <summary>Records that the running case exercised a combinator of this shape.</summary>
    /// <param name="shape">The combinator's shape.</param>
    public static void Report(LawShape shape) =>
        t_observed |= (1 << ((int)shape));
    /// <summary>Runs one law case and returns the set of shapes it reported.</summary>
    /// <param name="run">The case body.</param>
    /// <returns>The reported shapes as a bit set over <see cref="LawShape"/>.</returns>
    public static int Observe(Action run) {
        t_observed = 0;

        run();

        return t_observed;
    }
    /// <summary>Tests whether an observed set contains a shape.</summary>
    /// <param name="observed">The set returned by <see cref="Observe"/>.</param>
    /// <param name="shape">The shape to test for.</param>
    /// <returns><see langword="true"/> when the shape was reported.</returns>
    public static bool Contains(int observed, LawShape shape) =>
        ((observed & (1 << ((int)shape))) != 0);
}
