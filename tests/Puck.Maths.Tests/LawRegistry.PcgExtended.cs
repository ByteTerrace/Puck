namespace Puck.Maths.Tests;

internal static partial class LawRegistry {
    private static LawCase[] PcgExtendedCases() => [
        // ---- sampling: the extended PCG generator, distance and preimage ----
        ClaimCase(
            claim: Subjects.PcgExtendedReferenceVectors,
            id: "sampling.pcg-extended-reference-vectors"
        ),
        ClaimCase(
            claim: Subjects.PcgExtendedBaseEquivalence,
            id: "sampling.pcg-extended-base-equivalence"
        ),
        ClaimCase(
            claim: Subjects.PcgExtendedChosenOutputs,
            id: "sampling.pcg-extended-chosen-outputs"
        ),
        ClaimCase(
            claim: Subjects.PcgExtendedAdvanceAgreesWithDrawing,
            id: "sampling.pcg-extended-advance-agrees-with-drawing"
        ),
        ClaimCase(
            claim: Subjects.PcgExtendedDelegationAndRefusals,
            id: "sampling.pcg-extended-delegation-and-refusals"
        ),
        ClaimCase(
            claim: Subjects.PcgExtendedCreateWithTable,
            id: "sampling.pcg-extended-create-with-table"
        ),
        ClaimCase(
            claim: Subjects.PcgDistanceRoundTrip,
            id: "sampling.pcg-distance-round-trip"
        ),
        ClaimCase(
            claim: Subjects.PcgPreimageAndSeeking,
            id: "sampling.pcg-preimage-and-seeking"
        ),

    ];
}
