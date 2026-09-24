namespace Puck.Maths.Tests;

internal static partial class LawRegistry {
    private static LawCase[] CombinatoricsCases() => [
        ClaimCase(
            claim: Subjects.CombinatoricsPoker,
            id: "deep.combinatorics-poker"
        ),
        ClaimCase(
            claim: Subjects.CombinatoricsCounts,
            id: "integer.combinatorics-counts"
        ),
        ClaimCase(
            claim: Subjects.CombinatoricsOrder,
            id: "integer.combinatorics-order"
        ),
        ClaimCase(
            claim: Subjects.CombinatoricsRefusals,
            id: "integer.combinatorics-refusals"
        ),
        SweptCase(
            claim: Subjects.LexicographicOrderMatchesOracle,
            domain: Scalar,
            id: "integer.lexicographic-order-vs-oracle",
            width: 2
        ),
    ];
}
