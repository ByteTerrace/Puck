namespace Puck.Maths.Tests;

internal static partial class LawRegistry {
    private static readonly Domain VectorHelpers = new(
        Key: "vector-componentwise-helpers",
        Block: 512,
        EdgeFraction: 0.4,
        NeighborhoodFraction: 0.3
    );
    private static readonly Domain VectorWithin = new(
        Key: "vector-within",
        Block: 512,
        EdgeFraction: 0.4,
        NeighborhoodFraction: 0.3
    );
    private static readonly Domain VectorCompareLength = new(
        Key: "vector-compare-length",
        Block: 512,
        EdgeFraction: 0.4,
        NeighborhoodFraction: 0.3
    );
    private static readonly Domain QuaternionArc = new(
        Key: "quaternion-arc",
        Block: 512,
        EdgeFraction: 0.4,
        NeighborhoodFraction: 0.3
    );
    private static readonly Domain QuaternionAntiparallel = new(
        Key: "quaternion-antiparallel",
        Block: 512,
        EdgeFraction: 0.4,
        NeighborhoodFraction: 0.3
    );
    private static readonly Domain RigidExpSeries = new(
        Key: "rigid-exp-series",
        Block: 512,
        EdgeFraction: 0.4,
        NeighborhoodFraction: 0.3
    );
    private static readonly Domain RigidLogSeries = new(
        Key: "rigid-log-series",
        Block: 512,
        EdgeFraction: 0.4,
        NeighborhoodFraction: 0.3
    );
    private static readonly Domain TryArithmetic = new(
        Key: "integer-try-arithmetic",
        Block: 512,
        EdgeFraction: 0.4,
        NeighborhoodFraction: 0.3
    );
    private static readonly Domain TryMultiplication = new(
        Key: "integer-try-multiplication",
        Block: 512,
        EdgeFraction: 0.4,
        NeighborhoodFraction: 0.3
    );
    private static readonly Domain WordModular = new(
        Key: "core-word-modular",
        Block: 512,
        EdgeFraction: 0.4,
        NeighborhoodFraction: 0.3
    );

    private static LawCase[] CarrierHelperCases() => [
        SweptCase(
            claim: Subjects.VectorComponentwiseHelpersMatchOracle,
            domain: VectorHelpers,
            id: "vector.componentwise-helpers-vs-oracle",
            width: 4
        ),
        ClaimCase(
            claim: Subjects.VectorUnitAxesAreExact,
            id: "vector.unit-axes-are-exact"
        ),
        SweptCase(
            claim: Subjects.FixedQuaternionSlerpFollowsTheArc,
            domain: QuaternionArc,
            id: "quaternion.slerp-follows-the-arc",
            width: 4
        ),
        SweptCase(
            claim: Subjects.FixedQuaternionFromToNearAntiparallel,
            domain: QuaternionAntiparallel,
            id: "quaternion.from-to-near-antiparallel",
            width: 3
        ),
        SweptCase(
            claim: Subjects.FixedRigidExpMatchesTheSeries,
            domain: RigidExpSeries,
            id: "rigid.exp-matches-the-series",
            width: 3
        ),
        SweptCase(
            claim: Subjects.FixedRigidLogMatchesTheSeries,
            domain: RigidLogSeries,
            id: "rigid.log-matches-the-series",
            width: 4
        ),
        SweptCase(
            claim: Subjects.FixedVectorIsWithinMatchesLength,
            domain: VectorWithin,
            id: "vector.is-within-matches-length",
            width: 3
        ),
        SweptCase(
            claim: Subjects.FixedVectorCompareLengthMatchesTheSquares,
            domain: VectorCompareLength,
            id: "vector.compare-length-matches-the-squares",
            width: 3
        ),
        Case(
            id: "integer.try-add-and-try-narrow-vs-exact",
            run: () => {
                Laws.SweptClaim(
                    claim: TryArithmeticClaims.AddAndNarrowMatchExactArithmetic,
                    domain: TryArithmetic,
                    lawId: "integer.try-add-and-try-narrow-vs-exact",
                    tier: Tier.Default,
                    width: 2
                );
                Laws.Claim(
                    claim: TryArithmeticClaims.SeamsAreExact,
                    lawId: "integer.try-add-and-try-narrow-vs-exact"
                );
            }
        ),
        Case(
            id: "integer.try-multiply-and-try-exponentiate-vs-exact",
            run: () => {
                Laws.SweptClaim(
                    claim: TryArithmeticClaims.MultiplyAndExponentiateMatchExactArithmetic,
                    domain: TryMultiplication,
                    lawId: "integer.try-multiply-and-try-exponentiate-vs-exact",
                    tier: Tier.Default,
                    width: 2
                );
                Laws.Claim(
                    claim: TryArithmeticClaims.MultiplyAndExponentiateSeamsAreExact,
                    lawId: "integer.try-multiply-and-try-exponentiate-vs-exact"
                );
            }
        ),
        Case(
            id: "core.word-modular-arithmetic-vs-big-integer",
            run: () => {
                Laws.SweptClaim(
                    claim: WordModularClaims.WordModularArithmeticMatchesBigInteger,
                    domain: WordModular,
                    lawId: "core.word-modular-arithmetic-vs-big-integer",
                    tier: Tier.Default,
                    width: 2
                );
                Laws.Claim(
                    claim: WordModularClaims.WordModularSeamsAndRefusals,
                    lawId: "core.word-modular-arithmetic-vs-big-integer"
                );
            }
        ),
    ];
}
