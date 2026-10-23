namespace Puck.Maths.Tests;

internal static partial class LawRegistry {
    private static readonly Domain DegreeConversion = new(
        Key: "degree-conversion",
        Block: 512,
        EdgeFraction: 0.3,
        NeighborhoodFraction: 0.3
    );
    private static readonly Domain AngleWrap = new(
        Key: "angle-wrap",
        Block: 512,
        EdgeFraction: 0.3,
        NeighborhoodFraction: 0.3
    );
    private static readonly Domain PlaneDoors = new(
        Key: "plane-doors-and-clamp",
        Block: 512,
        EdgeFraction: 0.4,
        NeighborhoodFraction: 0.3
    );
    private static readonly Domain VectorRescale = new(
        Key: "vector-rescale",
        Block: 512,
        EdgeFraction: 0.4,
        NeighborhoodFraction: 0.3
    );
    private static readonly Domain VectorWithinExact = new(
        Key: "vector-within-exact",
        Block: 512,
        EdgeFraction: 0.4,
        NeighborhoodFraction: 0.3
    );
    private static readonly Domain VectorCone = new(
        Key: "vector-cone",
        Block: 512,
        EdgeFraction: 0.3,
        NeighborhoodFraction: 0.3
    );
    private static readonly Domain VectorReject = new(
        Key: "vector-reject",
        Block: 512,
        EdgeFraction: 0.3,
        NeighborhoodFraction: 0.3
    );
    private static readonly Domain VectorTryArithmetic = new(
        Key: "vector-try-arithmetic",
        Block: 512,
        EdgeFraction: 0.5,
        NeighborhoodFraction: 0.3
    );
    private static readonly Domain VectorYaw = new(
        Key: "vector-yaw",
        Block: 512,
        EdgeFraction: 0.3,
        NeighborhoodFraction: 0.3
    );
    private static readonly Domain YawPitchRoll = new(
        Key: "yaw-pitch-roll",
        Block: 512,
        EdgeFraction: 0.25,
        NeighborhoodFraction: 0.3
    );
    private static readonly Domain SaturateMultiply = new(
        Key: "saturate-multiply",
        Block: 512,
        EdgeFraction: 0.4,
        NeighborhoodFraction: 0.3
    );
    private static readonly Domain SecondsOfTicks = new(
        Key: "seconds-of-ticks",
        Block: 512,
        EdgeFraction: 0.4,
        NeighborhoodFraction: 0.3
    );
    private static readonly Domain SignedQuotients = new(
        Key: "signed-quotients",
        Block: 512,
        EdgeFraction: 0.4,
        NeighborhoodFraction: 0.3
    );
    private static readonly Domain DoubleSeams = new(
        Key: "double-seams",
        Block: 512,
        EdgeFraction: 0.4,
        NeighborhoodFraction: 0.3
    );
    private static readonly Domain FollowerLanes = new(
        Key: "follower-lanes",
        Block: 512,
        EdgeFraction: 0.4,
        NeighborhoodFraction: 0.3
    );
    private static readonly Domain SaturatingIntegers = new(
        Key: "saturating-integers",
        Block: 512,
        EdgeFraction: 0.5,
        NeighborhoodFraction: 0.3
    );
    private static readonly Domain ZigZag = new(
        Key: "zigzag",
        Block: 512,
        EdgeFraction: 0.4,
        NeighborhoodFraction: 0.3
    );
    private static readonly Domain BitMix64 = new(
        Key: "bit-mix-64",
        Block: 512,
        EdgeFraction: 0.3,
        NeighborhoodFraction: 0.3
    );
    private static readonly Domain ScatterPoint = new(
        Key: "scatter-point",
        Block: 512,
        EdgeFraction: 0.3,
        NeighborhoodFraction: 0.3
    );
    private static readonly Domain RationalRate = new(
        Key: "rational-rate",
        Block: 512,
        EdgeFraction: 0.3,
        NeighborhoodFraction: 0.3
    );
    private static readonly Domain RationalDoors = new(
        Key: "rational-doors",
        Block: 512,
        EdgeFraction: 0.4,
        NeighborhoodFraction: 0.3
    );

    private static LawCase[] SharedKernelCases() => [
        ClaimCase(
            claim: SharedKernelClaims.PiConstantsVsMachin,
            id: "scalar.pi-constants-vs-machin"
        ),
        SweptCase(
            claim: (left, right) => SharedKernelClaims.FromDegreesVsExactRational(
                deep: false,
                left: left,
                right: right
            ),
            domain: DegreeConversion,
            id: "scalar.from-degrees-vs-exact-rational",
            width: 1
        ),
        SweptCase(
            claim: (left, right) => SharedKernelClaims.FromDegreesVsExactRational(
                deep: true,
                left: left,
                right: right
            ),
            domain: DegreeConversion,
            id: "deep.scalar-from-degrees-vs-exact-rational",
            width: 1
        ),
        ClaimCase(
            claim: SharedKernelClaims.FromDegreesDoubleLadder,
            id: "scalar.from-degrees-double-ladder"
        ),
        SweptCase(
            claim: (left, right) => SharedKernelClaims.WrapAngleResidueAndRange(
                deep: false,
                left: left,
                right: right
            ),
            domain: AngleWrap,
            id: "scalar.wrap-angle-residue-and-range",
            width: 1
        ),
        SweptCase(
            claim: (left, right) => SharedKernelClaims.WrapAngleResidueAndRange(
                deep: true,
                left: left,
                right: right
            ),
            domain: AngleWrap,
            id: "deep.scalar-wrap-angle-residue-and-range",
            width: 1
        ),
        SweptCase(
            claim: SharedKernelClaims.PlaneDoorsAndClamp,
            domain: PlaneDoors,
            id: "vector.plane-doors-and-clamp",
            width: 2
        ),
        SweptCase(
            claim: (left, right) => SharedKernelClaims.WithLengthTowardZero(
                deep: false,
                left: left,
                right: right
            ),
            domain: VectorRescale,
            id: "vector.with-length-toward-zero",
            width: 3
        ),
        SweptCase(
            claim: (left, right) => SharedKernelClaims.WithLengthTowardZero(
                deep: true,
                left: left,
                right: right
            ),
            domain: VectorRescale,
            id: "deep.vector-with-length-toward-zero",
            width: 3
        ),
        SweptCase(
            claim: (left, right) => SharedKernelClaims.IsWithinExactSquares(
                deep: false,
                left: left,
                right: right
            ),
            domain: VectorWithinExact,
            id: "vector.is-within-exact-squares",
            width: 3
        ),
        SweptCase(
            claim: (left, right) => SharedKernelClaims.IsWithinExactSquares(
                deep: true,
                left: left,
                right: right
            ),
            domain: VectorWithinExact,
            id: "deep.vector-is-within-exact-squares",
            width: 3
        ),
        SweptCase(
            claim: (left, right) => SharedKernelClaims.IsWithinConeVsExactRational(
                deep: false,
                left: left,
                right: right
            ),
            domain: VectorCone,
            id: "vector.is-within-cone-vs-exact-rational",
            width: 3
        ),
        SweptCase(
            claim: (left, right) => SharedKernelClaims.IsWithinConeVsExactRational(
                deep: true,
                left: left,
                right: right
            ),
            domain: VectorCone,
            id: "deep.vector-is-within-cone-vs-exact-rational",
            width: 3
        ),
        ClaimCase(
            claim: SharedKernelClaims.IsWithinConeLadder,
            id: "vector.is-within-cone-ladder"
        ),
        SweptCase(
            claim: SharedKernelClaims.RejectFromAndNormalized,
            domain: VectorReject,
            id: "vector.reject-from-and-normalized",
            width: 3
        ),
        SweptCase(
            claim: SharedKernelClaims.TryAddSubtractRefuseOutsideCarrier,
            domain: VectorTryArithmetic,
            id: "vector.try-add-subtract-refuse-outside-carrier",
            width: 3
        ),
        SweptCase(
            claim: SharedKernelClaims.YawOfFacingAndAttitude,
            domain: VectorYaw,
            id: "vector.yaw-of-facing-and-attitude",
            width: 3
        ),
        SweptCase(
            claim: (left, right) => SharedKernelClaims.FromYawPitchRollProductAndReadback(
                deep: false,
                left: left,
                right: right
            ),
            domain: YawPitchRoll,
            id: "quaternion.from-yaw-pitch-roll-product-and-readback",
            width: 3
        ),
        SweptCase(
            claim: (left, right) => SharedKernelClaims.FromYawPitchRollProductAndReadback(
                deep: true,
                left: left,
                right: right
            ),
            domain: YawPitchRoll,
            id: "deep.quaternion-from-yaw-pitch-roll-product-and-readback",
            width: 3
        ),
        Case(
            id: "core.fixed-saturate-multiply-vs-oracle",
            run: () => Laws.ScalarBinaryMatchesOracle(
                domain: SaturateMultiply,
                lawId: "core.fixed-saturate-multiply-vs-oracle",
                oracle: SharedKernelClaims.SaturateMultiplyOracle,
                subject: SharedKernelClaims.SaturateMultiplyRaw,
                tier: Tier.Default
            )
        ),
        ClaimCase(
            claim: () => SharedKernelClaims.DecimalDurationsAndRatePeriods(deep: false),
            id: "core.decimal-durations-and-rate-periods"
        ),
        ClaimCase(
            claim: () => SharedKernelClaims.DecimalDurationsAndRatePeriods(deep: true),
            id: "deep.core-decimal-durations-and-rate-periods"
        ),
        SweptCase(
            claim: (left, right) => SharedKernelClaims.SecondsOfVsOracle(
                deep: false,
                left: left,
                right: right
            ),
            domain: SecondsOfTicks,
            id: "core.seconds-of-ticks-vs-oracle",
            width: 1
        ),
        SweptCase(
            claim: (left, right) => SharedKernelClaims.SecondsOfVsOracle(
                deep: true,
                left: left,
                right: right
            ),
            domain: SecondsOfTicks,
            id: "deep.core-seconds-of-ticks-vs-oracle",
            width: 1
        ),
        SweptCase(
            claim: (left, right) => SharedKernelClaims.SignedQuotientsVsOracle(
                deep: false,
                left: left,
                right: right
            ),
            domain: SignedQuotients,
            id: "directed-rounding.signed-quotients-vs-oracle",
            width: 1
        ),
        SweptCase(
            claim: (left, right) => SharedKernelClaims.SignedQuotientsVsOracle(
                deep: true,
                left: left,
                right: right
            ),
            domain: SignedQuotients,
            id: "deep.directed-rounding-signed-quotients-vs-oracle",
            width: 1
        ),
        SweptCase(
            claim: SharedKernelClaims.DoubleEnclosureSeams,
            domain: DoubleSeams,
            id: "directed-rounding.double-enclosure-seams",
            width: 1
        ),
        SweptCase(
            claim: SharedKernelClaims.FollowerLaneReplacement,
            domain: FollowerLanes,
            id: "dynamics.follower-lane-replacement",
            width: 3
        ),
        SweptCase(
            claim: SharedKernelClaims.SaturatingArithmeticVsExact,
            domain: SaturatingIntegers,
            id: "integer.saturating-arithmetic-vs-exact",
            width: 1
        ),
        SweptCase(
            claim: SharedKernelClaims.ZigZagBijection,
            domain: ZigZag,
            id: "integer.zigzag-bijection",
            width: 1
        ),
        SweptCase(
            claim: (left, right) => SharedKernelClaims.BitMix64FinalizerAndInverse(
                deep: false,
                left: left,
                right: right
            ),
            domain: BitMix64,
            id: "sampling.bit-mix64-finalizer-and-inverse",
            width: 1
        ),
        SweptCase(
            claim: (left, right) => SharedKernelClaims.BitMix64FinalizerAndInverse(
                deep: true,
                left: left,
                right: right
            ),
            domain: BitMix64,
            id: "deep.sampling-bit-mix64-finalizer-and-inverse",
            width: 1
        ),
        ClaimCase(
            claim: () => SharedKernelClaims.SampleOnceMatchesAliasTable(deep: false),
            id: "sampling.sample-once-matches-alias-table"
        ),
        ClaimCase(
            claim: () => SharedKernelClaims.SampleOnceMatchesAliasTable(deep: true),
            id: "deep.sampling-sample-once-matches-alias-table"
        ),
        SweptCase(
            claim: (left, right) => SharedKernelClaims.ScatterPointAndHashConstants(
                deep: false,
                left: left,
                right: right
            ),
            domain: ScatterPoint,
            id: "sampling.scatter-point-and-hash-constants",
            width: 3
        ),
        SweptCase(
            claim: (left, right) => SharedKernelClaims.ScatterPointAndHashConstants(
                deep: true,
                left: left,
                right: right
            ),
            domain: ScatterPoint,
            id: "deep.sampling-scatter-point-and-hash-constants",
            width: 3
        ),
        ClaimCase(
            claim: SharedKernelClaims.R2AlphasAreTheRoundedIncrements,
            id: "sampling.r2-alphas-are-the-rounded-increments"
        ),
        SweptCase(
            claim: SharedKernelClaims.RationalRateAccumulatorVsLedger,
            domain: RationalRate,
            id: "rate.rational-accumulator-vs-ledger",
            width: 2
        ),
        SweptCase(
            claim: SharedKernelClaims.RationalDoorsExact,
            domain: RationalDoors,
            id: "core.rational-doors-exact",
            width: 1
        ),
    ];
}
