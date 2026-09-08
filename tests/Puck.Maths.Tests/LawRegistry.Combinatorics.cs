namespace Puck.Maths.Tests;

internal static partial class LawRegistry {
    private static LawCase[] CombinatoricsCases() => [
        Case(id: "deep.combinatorics-poker", run: () => Laws.Claim(lawId: "deep.combinatorics-poker", claim: Subjects.CombinatoricsPoker)),
        Case(id: "integer.combinatorics-counts", run: () => Laws.Claim(lawId: "integer.combinatorics-counts", claim: Subjects.CombinatoricsCounts)),
        Case(id: "integer.combinatorics-order", run: () => Laws.Claim(lawId: "integer.combinatorics-order", claim: Subjects.CombinatoricsOrder)),
        Case(id: "integer.combinatorics-refusals", run: () => Laws.Claim(lawId: "integer.combinatorics-refusals", claim: Subjects.CombinatoricsRefusals)),
    ];
}
