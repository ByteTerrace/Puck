namespace Puck.Maths.Tests;

internal static partial class LawRegistry {
    private static LawCase[] CombinatoricsCases() => [
        Case(
            id: "deep.combinatorics-poker",
            run: () => Laws.Claim(
                claim: Subjects.CombinatoricsPoker,
                lawId: "deep.combinatorics-poker"
            )
        ),
        Case(
            id: "integer.combinatorics-counts",
            run: () => Laws.Claim(
                claim: Subjects.CombinatoricsCounts,
                lawId: "integer.combinatorics-counts"
            )
        ),
        Case(
            id: "integer.combinatorics-order",
            run: () => Laws.Claim(
                claim: Subjects.CombinatoricsOrder,
                lawId: "integer.combinatorics-order"
            )
        ),
        Case(
            id: "integer.combinatorics-refusals",
            run: () => Laws.Claim(
                claim: Subjects.CombinatoricsRefusals,
                lawId: "integer.combinatorics-refusals"
            )
        ),
    ];
}
