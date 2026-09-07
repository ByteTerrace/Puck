namespace Puck.Maths.Tests;

internal static partial class LawRegistry {
    private static LawCase[] PcgExtendedCases() => [
        // ---- sampling: the extended PCG generator, distance and preimage ----
        Case(
            id: "sampling.pcg-extended-reference-vectors",
            run: () => Laws.Claim(
                claim: Subjects.PcgExtendedReferenceVectors,
                lawId: "sampling.pcg-extended-reference-vectors"
            )
        ),
        Case(
            id: "sampling.pcg-extended-base-equivalence",
            run: () => Laws.Claim(
                claim: Subjects.PcgExtendedBaseEquivalence,
                lawId: "sampling.pcg-extended-base-equivalence"
            )
        ),
        Case(
            id: "sampling.pcg-extended-chosen-outputs",
            run: () => Laws.Claim(
                claim: Subjects.PcgExtendedChosenOutputs,
                lawId: "sampling.pcg-extended-chosen-outputs"
            )
        ),
        Case(
            id: "sampling.pcg-extended-advance-agrees-with-drawing",
            run: () => Laws.Claim(
                claim: Subjects.PcgExtendedAdvanceAgreesWithDrawing,
                lawId: "sampling.pcg-extended-advance-agrees-with-drawing"
            )
        ),
        Case(
            id: "sampling.pcg-extended-delegation-and-refusals",
            run: () => Laws.Claim(
                claim: Subjects.PcgExtendedDelegationAndRefusals,
                lawId: "sampling.pcg-extended-delegation-and-refusals"
            )
        ),
        Case(
            id: "sampling.pcg-extended-create-with-table",
            run: () => Laws.Claim(
                claim: Subjects.PcgExtendedCreateWithTable,
                lawId: "sampling.pcg-extended-create-with-table"
            )
        ),
        Case(
            id: "sampling.pcg-distance-round-trip",
            run: () => Laws.Claim(
                claim: Subjects.PcgDistanceRoundTrip,
                lawId: "sampling.pcg-distance-round-trip"
            )
        ),
        Case(
            id: "sampling.pcg-preimage-and-seeking",
            run: () => Laws.Claim(
                claim: Subjects.PcgPreimageAndSeeking,
                lawId: "sampling.pcg-preimage-and-seeking"
            )
        ),

    ];
}
