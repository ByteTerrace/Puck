namespace Puck.Maths.Tests;

internal static partial class LawRegistry {
    private static LawCase[] SmokeCases() => [
        // ---- Smoke: the folded originals, tiny domains, under two seconds ----
        Case(
            id: "smoke.complex-twin-quad",
            run: () => Laws.TwinBinary(
                lawId: "smoke.complex-twin-quad",
                domain: SmokeDomain,
                tier: Tier.Smoke,
                first: Subjects.ComplexMultiply,
                second: Subjects.AlgebraMultiply(
                    pRaw: 0L,
                    qRaw: ComplexQ
                ),
                witness: Subjects.MultiplyOracle(
                    pRaw: 0L,
                    qRaw: ComplexQ
                )
            )
        ),
        Case(
            id: "smoke.fixed-mul-ties-to-even",
            run: () => Laws.ScalarBinaryMatchesOracle(
                domain: SmokeDomain,
                lawId: "smoke.fixed-mul-ties-to-even",
                oracle: Subjects.FixedMultiplyOracle,
                subject: Subjects.FixedMultiply,
                tier: Tier.Smoke
            )
        ),
        // The carrier's other rounding kernel, and the hottest one the multiply's mirror does not already cover.
        Case(
            id: "smoke.fixed-divide-ties-to-even",
            run: () => Laws.ScalarBinaryMatchesOracle(
                domain: SmokeDomain,
                lawId: "smoke.fixed-divide-ties-to-even",
                oracle: Subjects.FixedDivideOracle,
                subject: Subjects.FixedDivide,
                tier: Tier.Smoke
            )
        ),
        Case(
            id: "smoke.closed-unit-mul-ties-to-even",
            run: () => Laws.ScalarBinaryMatchesOracle(
                domain: SmokeDomain,
                lawId: "smoke.closed-unit-mul-ties-to-even",
                oracle: Subjects.ClosedUnitMultiplyOracle,
                subject: Subjects.ClosedUnitMultiply,
                tier: Tier.Smoke
            )
        ),
        // The family's hottest kernel: UnitFraction32 is what the sampling tier RETURNS — Pcg32XshRr.NextUnitFraction32,
        // LowDiscrepancy.R1/R2 and CertifiedLowDiscrepancy.Point all produce it — so blending two sampled fractions is the
        // operation a consumer reaches on the hot path, and it is the one carrying a full-width 32×32→64 product.
        Case(
            id: "smoke.unit-fraction32-mul-ties-to-even",
            run: () => Laws.ScalarBinaryMatchesOracle(
                domain: SmokeDomain,
                lawId: "smoke.unit-fraction32-mul-ties-to-even",
                oracle: UnitFractionClaims<UnitFraction32Width, UnitFraction32>.MultiplyOracle,
                subject: UnitFractionClaims<UnitFraction32Width, UnitFraction32>.Multiply,
                tier: Tier.Smoke
            )
        ),
        // The vector family's hottest kernels. FixedVector2.Dot and Wedge are the two scalar fused products every
        // consumer of the family reaches — the rotation seam in FixedComplex.Rotate/FromTo, every projection and every
        // winding test — and the BinaryElemOp shape gives Smoke the budget-bounded edge battery over BOTH operands for
        // essentially no time. FixedVector3.Cross is the same two-term fused shape three times over and is mirrored at
        // Deep instead.
        Case(
            id: "smoke.vector-fused-products-one-rounding",
            run: () => Laws.BinaryMatchesOracle(
                domain: SmokeDomain,
                lawId: "smoke.vector-fused-products-one-rounding",
                oracle: Subjects.PlaneProductsOracle,
                subject: Subjects.PlaneProducts,
                tier: Tier.Smoke
            )
        ),
        // The unsigned carrier's hottest kernel, and the same choice the two scalar smoke rows above make.
        Case(
            id: "smoke.unsigned-scalar-mul-ties-to-even",
            run: () => Laws.ScalarBinaryMatchesOracle(
                domain: SmokeDomain,
                lawId: "smoke.unsigned-scalar-mul-ties-to-even",
                oracle: Subjects.UnsignedFixedMultiplyOracle,
                subject: Subjects.UnsignedFixedMultiply,
                tier: Tier.Smoke
            )
        ),
        // The family's hot path: every rotation compose, every dual-quaternion product inside FixedRigidTransform, every
        // Slerp and every FromTo runs through the Hamilton product, and the planar side already has smoke.complex-twin-quad.
        Case(
            id: "smoke.quaternion-mul-vs-oracle",
            run: () => Laws.VectorMatchesOracle(
                domain: SmokeDomain,
                lawId: "smoke.quaternion-mul-vs-oracle",
                oracle: Subjects.QuaternionMultiplyOracle,
                subject: Subjects.QuaternionMultiplyLanes,
                tier: Tier.Smoke,
                width: 4
            )
        ),
        // The kinematics family's hottest kernel and the widest fused accumulator it drives: every scene-graph compose,
        // every ComposeNormalized and both dual products inside ScLerp run through it, and each of its eight lanes
        // accumulates eight leaf Q32 products before a single rounding. FixedPosition.Delta is the runner-up — one call
        // per rendered entity — but its whole statement is exact integer arithmetic and gains far more from Deep's
        // exhaustive edge sweep than from a second fast mirror, so it takes a Deep mirror instead.
        Case(
            id: "smoke.rigid-compose-vs-oracle",
            run: () => Laws.VectorMatchesOracle(
                domain: SmokeDomain,
                lawId: "smoke.rigid-compose-vs-oracle",
                oracle: Subjects.RigidComposeOracle,
                subject: Subjects.RigidComposeLanes,
                tier: Tier.Smoke,
                width: 8
            )
        ),
        Case(
            id: "smoke.mobius-integer-exact",
            run: () => Laws.MobiusMatchesOracle(
                lawId: "smoke.mobius-integer-exact",
                domain: SmokeDomain,
                tier: Tier.Smoke,
                subject: Subjects.AlgebraMobius(
                    pRaw: OneRaw,
                    qRaw: OneRaw
                ),
                oracleNumerator: Subjects.MobiusNumeratorOracle(
                    pRaw: OneRaw,
                    qRaw: OneRaw
                )
            )
        ),
        Case(
            id: "smoke.presented-complex-twin",
            run: () => Laws.VectorTwin(
                lawId: "smoke.presented-complex-twin",
                domain: SmokeDomain,
                tier: Tier.Smoke,
                width: 2,
                first: Subjects.PresentedQuadraticMultiply(
                    pRaw: 0L,
                    qRaw: ComplexQ
                ),
                second: Subjects.ComplexMultiplyLanes,
                witness: Subjects.QuadraticMultiplyLanesOracle(
                    pRaw: 0L,
                    qRaw: ComplexQ
                )
            )
        ),
        Case(
            id: "smoke.presented-boolean-star",
            run: () => Laws.VectorMatchesOracle(
                lawId: "smoke.presented-boolean-star",
                domain: SmokeDomain,
                tier: Tier.Smoke,
                width: (Subjects.GraphOrder * Subjects.GraphOrder),
                subject: Subjects.PresentedBooleanStar(),
                oracle: Subjects.BooleanStarOracle
            )
        ),
        // The refusal is the law: an unguarded star over a cyclic counting quiver must RETURN its obstruction, never
        // throw and never invent a certificate, while the exact finite truncation stays available beside it.
        ClaimCase(
            claim: Subjects.UnguardedStarRefuses,
            id: "smoke.presented-star-unguarded-refuses"
        ),
        // The zero-allocation overload is the SAME loop writing caller buffers, so it is a twin rather than a variant.
        Case(
            id: "smoke.presented-multiply-into-twin",
            run: () => Laws.VectorTwin(
                lawId: "smoke.presented-multiply-into-twin",
                domain: SmokeDomain,
                tier: Tier.Smoke,
                width: 8,
                first: Subjects.PresentedCliffordMultiplyInto(
                    degenerateCount: 0,
                    negativeCount: 0,
                    positiveCount: 3
                ),
                second: Subjects.PresentedCliffordMultiply(
                    degenerateCount: 0,
                    negativeCount: 0,
                    positiveCount: 3
                ),
                witness: null
            )
        ),
        // Ledger row 15 in its smallest form: the residual at the identity twist IS the chain rule the dual number lifts.
        Case(
            id: "smoke.presented-jet-residual-twin",
            run: () => Laws.TwinBinary(
                lawId: "smoke.presented-jet-residual-twin",
                domain: SmokeDomain,
                tier: Tier.Smoke,
                first: Subjects.PresentedJetResidual(),
                second: Subjects.DualChainRuleLift,
                witness: Subjects.JetResidualOracle
            )
        ),
        // Co-arity greater than one at its smallest: the six planar diagrams of width two, their whole composition
        // table against the arc-tracing oracle, and the sum of the identity diagrams fixing every one of them.
        ClaimCase(
            claim: Subjects.TangleComposesAtSmokeWidth,
            id: "smoke.presented-tangle-composes"
        ),
        // Ledger row 19's transfer, named: the morphism out of the free monoid on one letter per partial quotient,
        // carrying each to that quotient's digit element, reaches the element the transfer's own fold reaches and the
        // entries its module run reads out.
        ClaimCase(
            claim: Subjects.FunctorTwinsTransfer,
            id: "smoke.presented-functor-twin"
        ),
        // The second product at its smallest: the seven words of two letters at a window of two, their whole table
        // against the interleaving enumeration, and the one-letter quasi-shuffle whose collisions are already there.
        ClaimCase(
            claim: Subjects.ShuffleComposesAtSmokeWindow,
            id: "smoke.presented-shuffle-twin"
        ),

    ];
    private static LawCase[] RootCoreCases() => [
        // ---- Root Core/Sampling public-surface laws ----
        ClaimCase(
            claim: CoreSurfaceClaims.BitMixConstantsInvertSurface,
            id: "sampling.bit-mix-constants-invert"
        ),
        ClaimCase(
            claim: CoreSurfaceClaims.BitMixIsAPermutationSurface,
            id: "sampling.bit-mix-is-a-permutation"
        ),
        ClaimCase(
            claim: CoreSurfaceClaims.CyclicRotationStructureSurface,
            id: "scalar.cyclic-rotation-closes-its-loop"
        ),
        ClaimCase(
            claim: CoreSurfaceClaims.BigIntegerIsPrimeSurface,
            id: "core.big-integer-is-prime-vs-oracle"
        ),
        ClaimCase(
            claim: CoreSurfaceClaims.BigIntegerPrimeFactorsSurface,
            id: "core.big-integer-prime-factors-vs-word-kernel"
        ),

        // The full-carrier scale the case above only samples, plus the Jacobi statement it cannot make. The Jacobi law
        // sits at Deep rather than Exhaustive on purpose: it is a two-second grid of statements, not a carrier-wide
        // sweep, so parking it behind an opt-in tier would cost it its everyday coverage. Its oracle is the
        // factor-and-Euler DEFINITION, with no reciprocity step anywhere in it, so at composite moduli it cannot pick
        // the same wrong value as the subject by running the library's own sibling descent on both sides.
        // The two the factorization surface could not make before: it took the gate's word at the one value the twelve
        // bases decide wrongly, and its depth was the operand's multiplicity rather than the heap.
        ClaimCase(
            claim: CoreSurfaceClaims.WitnessSetBoundaryFactorsExactly,
            id: "core.witness-set-boundary-factors-exactly"
        ),
        ClaimCase(
            claim: CoreSurfaceClaims.PrimeCountingIsDenseAgainstASieve,
            id: "core.prime-counting-is-dense-against-a-sieve"
        ),
        ClaimCase(
            claim: CoreSurfaceClaims.DeepMultiplicityFactorsWithoutStackGrowth,
            id: "core.deep-multiplicity-factors-without-stack-growth"
        ),

        ClaimCase(
            claim: PrimalityScaleClaims.JacobiSymbolSurface,
            id: "core.jacobi-symbol-cross-carrier"
        ),

        // Descriptors and elements that compared unequal to themselves, and an exponential that answered outside the
        // subdomain it documents instead of refusing. All three were silent: array identity masquerading as value
        // equality, and a closed form read off the scalar lane of a square that was not scalar.
        ClaimCase(
            claim: GeometricAlgebraClaims.CliffordDescriptorIdentitySurface,
            id: "algebra.clifford-descriptor-identity-is-the-signature"
        ),
        ClaimCase(
            claim: GeometricAlgebraClaims.CliffordExponentialDomainSurface,
            id: "algebra.clifford-exponential-scalar-square-domain"
        ),
        ClaimCase(
            claim: GeometricAlgebraClaims.MonogenicIdentitySurface,
            id: "algebra.monogenic-identity-is-tail-and-coordinates"
        ),

        // Carriers that admitted values they advertise they do not hold, a letter mask that broadened a split predicate
        // into a false positive, and colour indices that were never checked at all.
        ClaimCase(
            claim: OracleClaims.RationalMaterialAdmitsOnlyRationals,
            id: "presented.rational-material-admits-only-rationals"
        ),
        ClaimCase(
            claim: OracleClaims.CountingMaterialAdmitsOnlyNaturals,
            id: "presented.counting-material-admits-only-naturals"
        ),
        ClaimCase(
            claim: OracleClaims.LetterMaskRefusesASplitBlock,
            id: "presented.letter-mask-refuses-a-split-block"
        ),
        ClaimCase(
            claim: OracleClaims.GeneratorColoursAreBoundedIndices,
            id: "presented.generator-colours-are-bounded-indices"
        ),
        ClaimCase(
            claim: PrimalityScaleClaims.FactorizationFullWidthSurface,
            id: "core.factorization-full-width-sweep"
        ),
        ClaimCase(
            claim: CoreSurfaceClaims.BigIntegerSquareRootSurface,
            id: "core.big-integer-square-root-vs-unsigned"
        ),
        ClaimCase(
            claim: CoreSurfaceClaims.BigIntegerToDoubleSurface,
            id: "core.big-integer-to-double-vs-exact-neighbours"
        ),
        ClaimCase(
            claim: CoreSurfaceClaims.BigIntegerModularInverseSurface,
            id: "core.big-integer-modular-inverse-vs-hensel"
        ),
        ClaimCase(
            claim: CoreSurfaceClaims.BigIntegerModularSquareRootSurface,
            id: "core.big-integer-modular-square-root-vs-prime-field"
        ),
        ClaimCase(
            claim: CoreSurfaceClaims.BinaryIntegerSurface,
            id: "core.binary-integer-contracts"
        ),
        Case(
            id: "core.discrete-measure-exact-and-compiled",
            run: () => {
                Laws.Claim(
                    claim: CoreSurfaceClaims.DiscreteMeasureSurface,
                    lawId: "core.discrete-measure-exact-and-compiled"
                );
                Laws.Claim(
                    claim: CoreSurfaceClaims.CompiledRadicalTransport,
                    lawId: "core.discrete-measure-exact-and-compiled"
                );
            }
        ),
        ClaimCase(
            claim: CoreSurfaceClaims.NumberTheorySurface,
            id: "core.number-theory-contracts"
        ),
        ClaimCase(
            claim: CoreSurfaceClaims.RealQuadraticFieldSurface,
            id: "core.real-quadratic-field-descriptor"
        ),
        ClaimCase(
            claim: CoreSurfaceClaims.RealQuadraticSurface,
            id: "core.real-quadratic-field-and-conversion"
        ),
        ClaimCase(
            claim: CoreSurfaceClaims.PrimeExtensionsSurface,
            id: "core.prime-extensions-vs-trial-division"
        ),
        ClaimCase(
            claim: CoreSurfaceClaims.UnsignedIntegerSurface,
            id: "core.unsigned-integer-contracts"
        ),
        ClaimCase(
            claim: CoreSurfaceClaims.Fnv1aSurface,
            id: "core.fnv1a-published-vector"
        ),
        ClaimCase(
            claim: CoreSurfaceClaims.MonotonicPartitionerSurface,
            id: "core.monotonic-partitioner-fast-invariants"
        ),

    ];
    private static LawCase[] FixedQ4816Cases() => [
        // ---- FixedQ4816 carrier: rounding vs oracle (ties to even), add, determinism ----
        Case(
            id: "scalar.mul-vs-oracle",
            run: () => Laws.ScalarBinaryMatchesOracle(
                domain: Scalar,
                lawId: "scalar.mul-vs-oracle",
                oracle: Subjects.FixedMultiplyOracle,
                subject: Subjects.FixedMultiply,
                tier: Tier.Default
            )
        ),
        Case(
            id: "scalar.add-vs-oracle",
            run: () => Laws.ScalarBinaryMatchesOracle(
                domain: Scalar,
                lawId: "scalar.add-vs-oracle",
                oracle: Subjects.FixedAddOracle,
                subject: Subjects.FixedAdd,
                tier: Tier.Default
            )
        ),
        Case(
            id: "scalar.mul-purity",
            run: () => Laws.PureScalarBinary(
                domain: Scalar,
                lawId: "scalar.mul-purity",
                op: Subjects.FixedMultiply,
                tier: Tier.Default
            )
        ),
        ClaimCase(
            claim: Subjects.FixedGridAndConstruction,
            id: "scalar.grid-and-construction"
        ),
        // The OUTWARD double seam. It was waived as presentation-only for the whole of this suite's life; the waiver's
        // premise does not survive its own siblings, because unit-fraction16/32.double-projection-exact pin the same
        // conversion exactly and unsigned-scalar.double-seam pins the unsigned twin of THIS one — a Q48.16 narrowing with
        // genuine precision loss — against a hand ladder. Inexact is not unspecified: the map is a total function of the
        // raw and every value it takes is decidable in integers.
        SweptCase(
            claim: Subjects.FixedDoubleProjectionExact,
            domain: Scalar,
            id: "scalar.double-projection-vs-oracle",
            width: 1
        ),
        SweptCase(
            claim: Subjects.FixedAdditiveOpsExact,
            domain: Scalar,
            id: "scalar.additive-ops-vs-oracle",
            width: 1
        ),
        SweptCase(
            claim: Subjects.FixedCheckedOpsRefuse,
            domain: Scalar,
            id: "scalar.checked-ops-refuse",
            width: 1
        ),
        Case(
            id: "scalar.divide-vs-oracle",
            run: () => Laws.ScalarBinaryMatchesOracle(
                domain: ScalarDivision,
                lawId: "scalar.divide-vs-oracle",
                oracle: Subjects.FixedDivideOracle,
                subject: Subjects.FixedDivide,
                tier: Tier.Default
            )
        ),
        SweptCase(
            claim: Subjects.FixedModulusExact,
            domain: Scalar,
            id: "scalar.modulus-vs-oracle",
            width: 1
        ),
        SweptCase(
            claim: Subjects.FixedOrderExact,
            domain: Scalar,
            id: "scalar.order-vs-oracle",
            width: 2
        ),
        SweptCase(
            claim: Subjects.FixedMagnitudeSelectionExact,
            domain: Scalar,
            id: "scalar.magnitude-selection-vs-oracle",
            width: 1
        ),
        SweptCase(
            claim: Subjects.FixedIntegralPartsExact,
            domain: Scalar,
            id: "scalar.integral-parts-vs-oracle",
            width: 1
        ),
        SweptCase(
            claim: Subjects.FixedPredicatesClassify,
            domain: Scalar,
            id: "scalar.predicates-classify",
            width: 1
        ),
        SweptCase(
            claim: Subjects.FixedLerpEndpointsAndOracle,
            domain: Scalar,
            id: "scalar.lerp-endpoints-and-oracle",
            width: 2
        ),
        SweptCase(
            claim: Subjects.FixedTextRoundTrip,
            domain: ScalarText,
            id: "scalar.text-round-trip",
            width: 1
        ),
        ClaimCase(
            claim: Subjects.FixedTextLadderAndRefusals,
            id: "scalar.text-ladder-and-refusals"
        ),
        ClaimCase(
            claim: Subjects.StyledExponentCompensation,
            id: "scalar.styled-exponent-compensation"
        ),
        ClaimCase(
            claim: Subjects.GenericConversionModes,
            id: "scalar.generic-conversion-modes"
        ),
        ClaimCase(
            claim: Subjects.CultureTokenAmbiguityRefused,
            id: "scalar.culture-token-ambiguity-refused"
        ),
        SweptCase(
            claim: Subjects.FixedSqrtExact,
            domain: ScalarTranscendental,
            id: "scalar.sqrt-exact",
            width: 1
        ),
        SweptCase(
            claim: Subjects.FixedLog2WithinEnvelope,
            domain: ScalarTranscendental,
            id: "scalar.log2-vs-series",
            width: 1
        ),
        SweptCase(
            claim: Subjects.FixedExp2WithinEnvelope,
            domain: ScalarTranscendental,
            id: "scalar.exp2-vs-series",
            width: 1
        ),
        ClaimCase(
            claim: Subjects.TrigonometryTwiddles,
            id: "scalar.trigonometry-twiddles"
        ),
        ClaimCase(
            claim: Subjects.TrigonometryConstants,
            id: "scalar.trigonometry-constants"
        ),
        ClaimCase(
            claim: Subjects.TrigonometrySeams,
            id: "scalar.trigonometry-seams"
        ),
        SweptCase(
            claim: Subjects.TrigonometryTurns,
            domain: ScalarTranscendental,
            id: "scalar.trigonometry-turns",
            width: 1
        ),
        SweptCase(
            claim: Subjects.TrigonometryTurns,
            domain: ScalarTranscendental,
            id: "scalar.trigonometry-turns-deep",
            width: 1
        ),
        SweptCase(
            claim: Subjects.FixedSinCosWithinEnvelope,
            domain: ScalarTranscendental,
            id: "scalar.sincos-vs-series",
            width: 1
        ),
        SweptCase(
            claim: Subjects.FixedAtan2WithinEnvelope,
            domain: ScalarTranscendental,
            id: "scalar.atan2-vs-series",
            width: 1
        ),
        ClaimCase(
            claim: Subjects.FixedPowExactLattice,
            id: "scalar.pow-exact-lattice"
        ),
        SweptCase(
            claim: Subjects.FixedPowWithinEnvelope,
            domain: ScalarTranscendental,
            id: "scalar.pow-envelope",
            width: 1
        ),

    ];
    private static LawCase[] FixedQ1648Cases() => [
        // ---- FixedQ1648 (Q16.48): a range-for-resolution scalar leaning toward resolution. Non-transcendental
        // sibling of the scalar family above, retargeted at forty-eight fraction bits and a sixteen-bit integer
        // range; its distinguishing law is the FixedQ4816 peer conversion. ----
        Case(
            id: "q1648.mul-vs-oracle",
            run: () => Laws.ScalarBinaryMatchesOracle(
                domain: Q1648Scalar,
                lawId: "q1648.mul-vs-oracle",
                oracle: Subjects.Q1648MultiplyOracle,
                subject: Subjects.Q1648Multiply,
                tier: Tier.Default
            )
        ),
        Case(
            id: "q1648.add-vs-oracle",
            run: () => Laws.ScalarBinaryMatchesOracle(
                domain: Q1648Scalar,
                lawId: "q1648.add-vs-oracle",
                oracle: Subjects.Q1648AddOracle,
                subject: Subjects.Q1648Add,
                tier: Tier.Default
            )
        ),
        Case(
            id: "q1648.mul-purity",
            run: () => Laws.PureScalarBinary(
                domain: Q1648Scalar,
                lawId: "q1648.mul-purity",
                op: Subjects.Q1648Multiply,
                tier: Tier.Default
            )
        ),
        ClaimCase(
            claim: Subjects.Q1648GridAndConstruction,
            id: "q1648.grid-and-construction"
        ),
        SweptCase(
            claim: Subjects.Q1648AdditiveOpsExact,
            domain: Q1648Scalar,
            id: "q1648.additive-ops-vs-oracle",
            width: 1
        ),
        SweptCase(
            claim: Subjects.Q1648CheckedOpsRefuse,
            domain: Q1648Scalar,
            id: "q1648.checked-ops-refuse",
            width: 1
        ),
        Case(
            id: "q1648.divide-vs-oracle",
            run: () => Laws.ScalarBinaryMatchesOracle(
                domain: Q1648ScalarDivision,
                lawId: "q1648.divide-vs-oracle",
                oracle: Subjects.Q1648DivideOracle,
                subject: Subjects.Q1648Divide,
                tier: Tier.Default
            )
        ),
        SweptCase(
            claim: Subjects.Q1648ModulusExact,
            domain: Q1648Scalar,
            id: "q1648.modulus-vs-oracle",
            width: 1
        ),
        SweptCase(
            claim: Subjects.Q1648OrderExact,
            domain: Q1648Scalar,
            id: "q1648.order-vs-oracle",
            width: 2
        ),
        SweptCase(
            claim: Subjects.Q1648MagnitudeSelectionExact,
            domain: Q1648Scalar,
            id: "q1648.magnitude-selection-vs-oracle",
            width: 1
        ),
        SweptCase(
            claim: Subjects.Q1648IntegralPartsExact,
            domain: Q1648Scalar,
            id: "q1648.integral-parts-vs-oracle",
            width: 1
        ),
        SweptCase(
            claim: Subjects.Q1648PredicatesClassify,
            domain: Q1648Scalar,
            id: "q1648.predicates-classify",
            width: 1
        ),
        SweptCase(
            claim: Subjects.Q1648LerpEndpointsAndOracle,
            domain: Q1648Scalar,
            id: "q1648.lerp-endpoints-and-oracle",
            width: 2
        ),
        SweptCase(
            claim: Subjects.Q1648TextRoundTrip,
            domain: Q1648Scalar,
            id: "q1648.text-round-trip",
            width: 1
        ),
        ClaimCase(
            claim: Subjects.Q1648TextRefusals,
            id: "q1648.text-refusals"
        ),
        ClaimCase(
            claim: Subjects.Q1648StyledParseIsGenuine,
            id: "q1648.styled-parse-is-genuine"
        ),
        ClaimCase(
            claim: Subjects.Q1648TextParseTies,
            id: "q1648.text-parse-ties"
        ),
        ClaimCase(
            claim: Subjects.Q1648DecimalConversionModes,
            id: "q1648.decimal-conversion-modes"
        ),
        ClaimCase(
            claim: Subjects.ScaleDecimalWideReachesCanonicalCore,
            id: "core.scale-decimal-wide-reaches-canonical-core"
        ),
        ClaimCase(
            claim: Subjects.Q1648PeerConversionExact,
            id: "q1648.peer-conversion-vs-fixedq4816"
        ),

    ];
    private static LawCase[] FixedQ3232Cases() => [
        // ---- FixedQ3232 (Q32.32): a scalar splitting integer and fraction bits evenly, the balanced point between
        // FixedQ4816's range-leaning and FixedQ1648's resolution-leaning splits. Non-transcendental sibling of the
        // scalar family above, retargeted at thirty-two fraction bits and a thirty-two-bit integer range; its
        // distinguishing law is the FixedQ4816 peer conversion. ----
        Case(
            id: "q3232.mul-vs-oracle",
            run: () => Laws.ScalarBinaryMatchesOracle(
                domain: Q3232Scalar,
                lawId: "q3232.mul-vs-oracle",
                oracle: Subjects.Q3232MultiplyOracle,
                subject: Subjects.Q3232Multiply,
                tier: Tier.Default
            )
        ),
        Case(
            id: "q3232.add-vs-oracle",
            run: () => Laws.ScalarBinaryMatchesOracle(
                domain: Q3232Scalar,
                lawId: "q3232.add-vs-oracle",
                oracle: Subjects.Q3232AddOracle,
                subject: Subjects.Q3232Add,
                tier: Tier.Default
            )
        ),
        Case(
            id: "q3232.mul-purity",
            run: () => Laws.PureScalarBinary(
                domain: Q3232Scalar,
                lawId: "q3232.mul-purity",
                op: Subjects.Q3232Multiply,
                tier: Tier.Default
            )
        ),
        ClaimCase(
            claim: Subjects.Q3232GridAndConstruction,
            id: "q3232.grid-and-construction"
        ),
        SweptCase(
            claim: Subjects.Q3232AdditiveOpsExact,
            domain: Q3232Scalar,
            id: "q3232.additive-ops-vs-oracle",
            width: 1
        ),
        SweptCase(
            claim: Subjects.Q3232CheckedOpsRefuse,
            domain: Q3232Scalar,
            id: "q3232.checked-ops-refuse",
            width: 1
        ),
        Case(
            id: "q3232.divide-vs-oracle",
            run: () => Laws.ScalarBinaryMatchesOracle(
                domain: Q3232ScalarDivision,
                lawId: "q3232.divide-vs-oracle",
                oracle: Subjects.Q3232DivideOracle,
                subject: Subjects.Q3232Divide,
                tier: Tier.Default
            )
        ),
        SweptCase(
            claim: Subjects.Q3232ModulusExact,
            domain: Q3232Scalar,
            id: "q3232.modulus-vs-oracle",
            width: 1
        ),
        SweptCase(
            claim: Subjects.Q3232OrderExact,
            domain: Q3232Scalar,
            id: "q3232.order-vs-oracle",
            width: 2
        ),
        SweptCase(
            claim: Subjects.Q3232MagnitudeSelectionExact,
            domain: Q3232Scalar,
            id: "q3232.magnitude-selection-vs-oracle",
            width: 1
        ),
        SweptCase(
            claim: Subjects.Q3232IntegralPartsExact,
            domain: Q3232Scalar,
            id: "q3232.integral-parts-vs-oracle",
            width: 1
        ),
        SweptCase(
            claim: Subjects.Q3232PredicatesClassify,
            domain: Q3232Scalar,
            id: "q3232.predicates-classify",
            width: 1
        ),
        SweptCase(
            claim: Subjects.Q3232LerpEndpointsAndOracle,
            domain: Q3232Scalar,
            id: "q3232.lerp-endpoints-and-oracle",
            width: 2
        ),
        SweptCase(
            claim: Subjects.Q3232TextRoundTrip,
            domain: Q3232Scalar,
            id: "q3232.text-round-trip",
            width: 1
        ),
        ClaimCase(
            claim: Subjects.Q3232TextRefusals,
            id: "q3232.text-refusals"
        ),
        ClaimCase(
            claim: Subjects.Q3232StyledParseIsGenuine,
            id: "q3232.styled-parse-is-genuine"
        ),
        ClaimCase(
            claim: Subjects.Q3232TextParseTies,
            id: "q3232.text-parse-ties"
        ),
        ClaimCase(
            claim: Subjects.Q3232DecimalConversionModes,
            id: "q3232.decimal-conversion-modes"
        ),
        ClaimCase(
            claim: Subjects.Q3232PeerConversionExact,
            id: "q3232.peer-conversion-vs-fixedq4816"
        ),

    ];
    private static LawCase[] ContributionFoldCases() => [
        // ---- FixedContributionFold: raw-once accumulation, optional pool, final range and terminal quantization ----
        Case(
            id: "contribution-fold.formula-vs-big-integer-oracle",
            run: () => {
                Laws.Claim(
                    claim: FixedContributionFoldClaims.FormulaExactGrid,
                    lawId: "contribution-fold.formula-vs-big-integer-oracle"
                );
                Laws.SweptClaim(
                    claim: FixedContributionFoldClaims.FormulaSample,
                    domain: ContributionFoldFormula,
                    lawId: "contribution-fold.formula-vs-big-integer-oracle",
                    tier: Tier.Default,
                    width: 4
                );
            }
        ),
        Case(
            id: "contribution-fold.no-pool-specialization",
            run: () => {
                Laws.Claim(
                    claim: FixedContributionFoldClaims.NoPoolExactGrid,
                    lawId: "contribution-fold.no-pool-specialization"
                );
                Laws.SweptClaim(
                    claim: FixedContributionFoldClaims.NoPoolSample,
                    domain: ContributionFoldNoPool,
                    lawId: "contribution-fold.no-pool-specialization",
                    tier: Tier.Default,
                    width: 2
                );
            }
        ),
        Case(
            id: "contribution-fold.raw-sum-order-independent",
            run: () => {
                Laws.Claim(
                    claim: FixedContributionFoldClaims.RawSumEveryPermutation,
                    lawId: "contribution-fold.raw-sum-order-independent"
                );
                Laws.SweptClaim(
                    claim: FixedContributionFoldClaims.RawSumSampledLonger,
                    domain: ContributionFoldOrder,
                    lawId: "contribution-fold.raw-sum-order-independent",
                    tier: Tier.Default,
                    width: 8
                );
            }
        ),
        SweptCase(
            claim: FixedContributionFoldClaims.AnalogPoolBound,
            domain: ContributionFoldAnalog,
            id: "contribution-fold.analog-pool-bound",
            width: 2
        ),
        ClaimCase(
            claim: FixedContributionFoldClaims.BinaryFlipBoundAndSharpness,
            id: "contribution-fold.binary-flip-bound-sharp"
        ),
        ClaimCase(
            claim: FixedContributionFoldClaims.BinaryCompositionByInduction,
            id: "contribution-fold.binary-composition-induction"
        ),
        SweptCase(
            claim: FixedContributionFoldClaims.TerminalQuantizationIdempotence,
            domain: ContributionFoldQuantization,
            id: "contribution-fold.terminal-quantization-idempotent",
            width: 2
        ),
        ClaimCase(
            claim: FixedContributionFoldClaims.OverflowBoundaryExact,
            id: "contribution-fold.overflow-boundary-exact"
        ),
        ClaimCase(
            claim: FixedContributionFoldClaims.ConfigurationRefusals,
            id: "contribution-fold.configuration-refusals"
        ),
        ClaimCase(
            claim: FixedContributionFoldClaims.DiscriminatingExamples,
            id: "contribution-fold.discriminating-examples"
        ),
        Case(
            id: "contribution-fold.site-composition-distribution-known-false",
            run: () => Laws.KnownFalse(
                counterexample: FixedContributionFoldClaims.SiteCompositionDoesNotDistribute,
                lawId: "contribution-fold.site-composition-distribution-known-false"
            )
        ),

    ];
    private static LawCase[] UFixedQ4816Cases() => [
        // ---- UFixedQ4816 carrier: the unsigned Q48.16 companion, wrapping into [0, 2⁶⁴) with MinValue at zero ----
        Case(
            id: "unsigned-scalar.mul-vs-oracle",
            run: () => Laws.ScalarBinaryMatchesOracle(
                domain: UnsignedScalar,
                lawId: "unsigned-scalar.mul-vs-oracle",
                oracle: Subjects.UnsignedFixedMultiplyOracle,
                subject: Subjects.UnsignedFixedMultiply,
                tier: Tier.Default
            )
        ),
        Case(
            id: "unsigned-scalar.div-vs-oracle",
            run: () => Laws.ScalarBinaryMatchesOracle(
                domain: UnsignedScalar,
                lawId: "unsigned-scalar.div-vs-oracle",
                oracle: Subjects.UnsignedFixedDivideOracle,
                subject: Subjects.UnsignedFixedDivide,
                tier: Tier.Default
            )
        ),
        Case(
            id: "unsigned-scalar.mul-purity",
            run: () => Laws.PureScalarBinary(
                domain: UnsignedScalar,
                lawId: "unsigned-scalar.mul-purity",
                op: Subjects.UnsignedFixedMultiply,
                tier: Tier.Default
            )
        ),
        SweptCase(
            claim: Subjects.UnsignedUncheckedKernelsExact,
            domain: UnsignedScalar,
            id: "unsigned-scalar.unchecked-kernels-vs-oracle",
            width: 1
        ),
        SweptCase(
            claim: Subjects.UnsignedWrappingAlgebraExact,
            domain: UnsignedScalar,
            id: "unsigned-scalar.wrapping-algebra-exact",
            width: 1
        ),
        SweptCase(
            claim: Subjects.UnsignedCheckedOperatorsRefuse,
            domain: UnsignedScalar,
            id: "unsigned-scalar.checked-operators-refuse",
            width: 1
        ),
        SweptCase(
            claim: Subjects.UnsignedSaturatingAndSelectionExact,
            domain: UnsignedScalar,
            id: "unsigned-scalar.saturating-and-selection-exact",
            width: 2
        ),
        SweptCase(
            claim: Subjects.UnsignedIntegerDecompositionExact,
            domain: UnsignedScalar,
            id: "unsigned-scalar.integer-decomposition-exact",
            width: 1
        ),
        SweptCase(
            claim: Subjects.UnsignedOrderAndComparisonExact,
            domain: UnsignedScalar,
            id: "unsigned-scalar.order-and-comparison-exact",
            width: 1
        ),
        SweptCase(
            claim: Subjects.UnsignedNumberPredicatesExact,
            domain: UnsignedScalar,
            id: "unsigned-scalar.number-predicates-exact",
            width: 1
        ),
        ClaimCase(
            claim: Subjects.UnsignedConstructionAndRefusals,
            id: "unsigned-scalar.construction-and-refusals"
        ),
        ClaimCase(
            claim: Subjects.UnsignedDoubleSeam,
            id: "unsigned-scalar.double-seam"
        ),
        SweptCase(
            claim: Subjects.UnsignedTextRoundTrip,
            domain: UnsignedScalar,
            id: "unsigned-scalar.text-round-trip",
            width: 1
        ),
        ClaimCase(
            claim: Subjects.UnsignedTextLadderAndRefusals,
            id: "unsigned-scalar.text-ladder-and-refusals"
        ),

    ];
    private static LawCase[] UnitInterval32Cases() => [
        // ---- UnitInterval32 carrier: the closed unit interval on the sampler's own grid ----
        Case(
            id: "closed-unit.mul-vs-oracle",
            run: () => Laws.ScalarBinaryMatchesOracle(
                domain: ClosedUnit,
                lawId: "closed-unit.mul-vs-oracle",
                oracle: Subjects.ClosedUnitMultiplyOracle,
                subject: Subjects.ClosedUnitMultiply,
                tier: Tier.Default
            )
        ),
        // The point of spending the thirty-third bit: both absorbing elements act EXACTLY, at every raw region and at
        // both endpoints, so nothing about the interval's closure is a rounding accident.
        SweptCase(
            claim: Subjects.ClosedUnitUnitAndZeroExact,
            domain: ClosedUnit,
            id: "closed-unit.unit-and-zero-exact",
            width: 1
        ),
        SweptCase(
            claim: Subjects.ClosedUnitBoundedOpsExact,
            domain: ClosedUnit,
            id: "closed-unit.bounded-ops-exact",
            width: 1
        ),
        // The kinship contract: the sampler's half-open grid embeds with no representation event, and the Q48.16 seam
        // states its one rounding out loud.
        SweptCase(
            claim: Subjects.ClosedUnitKinshipExact,
            domain: ClosedUnit,
            id: "closed-unit.kinship-exact",
            width: 1
        ),
        ClaimCase(
            claim: Subjects.ClosedUnitConstructionAndRefusals,
            id: "closed-unit.construction-and-refusals"
        ),
        // The three-factor product exists because a fused sum's term is a charge times two coefficients and the contract
        // is ONE rounding per returned coefficient, not one per pair. Its statement is the same one the pairwise product
        // makes, at the tripled scale.
        SweptCase(
            claim: Subjects.ClosedUnitTripleProductExact,
            domain: ClosedUnit,
            id: "closed-unit.triple-product-one-rounding",
            width: 2
        ),

    ];
    private static LawCase[] UnitFractionCases() => [
        // ---- UnitFraction16/UnitFraction32 carriers: the half-open unit fractions ----
        Case(
            id: "unit-fraction16.mul-vs-oracle",
            run: () => Laws.ScalarBinaryMatchesOracle(
                domain: UnitFraction16Domain,
                lawId: "unit-fraction16.mul-vs-oracle",
                oracle: UnitFractionClaims<UnitFraction16Width, UnitFraction16>.MultiplyOracle,
                subject: UnitFractionClaims<UnitFraction16Width, UnitFraction16>.Multiply,
                tier: Tier.Default
            )
        ),
        Case(
            id: "unit-fraction16.div-vs-oracle",
            run: () => Laws.ScalarBinaryMatchesOracle(
                domain: UnitFraction16Domain,
                lawId: "unit-fraction16.div-vs-oracle",
                oracle: UnitFractionClaims<UnitFraction16Width, UnitFraction16>.DivideOracle,
                subject: UnitFractionClaims<UnitFraction16Width, UnitFraction16>.Divide,
                tier: Tier.Default
            )
        ),
        SweptCase(
            claim: UnitFractionClaims<UnitFraction16Width, UnitFraction16>.ExactOpsAndOrder,
            domain: UnitFraction16Domain,
            id: "unit-fraction16.exact-ops-and-order",
            width: 1
        ),
        SweptCase(
            claim: UnitFractionClaims<UnitFraction16Width, UnitFraction16>.ShiftsMatchOracle,
            domain: UnitFraction16Domain,
            id: "unit-fraction16.shifts-vs-oracle",
            width: 1
        ),
        ClaimCase(
            claim: UnitFractionClaims<UnitFraction16Width, UnitFraction16>.ConstructionAndRefusals,
            id: "unit-fraction16.construction-and-refusals"
        ),
        SweptCase(
            claim: UnitFractionClaims<UnitFraction16Width, UnitFraction16>.TextMatchesOracle,
            domain: UnitFraction16Domain,
            id: "unit-fraction16.text-vs-oracle",
            width: 1
        ),
        ClaimCase(
            claim: UnitFractionClaims<UnitFraction16Width, UnitFraction16>.ParseLadderHolds,
            id: "unit-fraction16.parse-ladder"
        ),
        SweptCase(
            claim: UnitFractionClaims<UnitFraction16Width, UnitFraction16>.DoubleProjectionExact,
            domain: UnitFraction16Domain,
            id: "unit-fraction16.double-projection-exact",
            width: 1
        ),
        Case(
            id: "unit-fraction32.mul-vs-oracle",
            run: () => Laws.ScalarBinaryMatchesOracle(
                domain: UnitFraction32Domain,
                lawId: "unit-fraction32.mul-vs-oracle",
                oracle: UnitFractionClaims<UnitFraction32Width, UnitFraction32>.MultiplyOracle,
                subject: UnitFractionClaims<UnitFraction32Width, UnitFraction32>.Multiply,
                tier: Tier.Default
            )
        ),
        Case(
            id: "unit-fraction32.div-vs-oracle",
            run: () => Laws.ScalarBinaryMatchesOracle(
                domain: UnitFraction32Domain,
                lawId: "unit-fraction32.div-vs-oracle",
                oracle: UnitFractionClaims<UnitFraction32Width, UnitFraction32>.DivideOracle,
                subject: UnitFractionClaims<UnitFraction32Width, UnitFraction32>.Divide,
                tier: Tier.Default
            )
        ),
        SweptCase(
            claim: UnitFractionClaims<UnitFraction32Width, UnitFraction32>.ExactOpsAndOrder,
            domain: UnitFraction32Domain,
            id: "unit-fraction32.exact-ops-and-order",
            width: 1
        ),
        SweptCase(
            claim: UnitFractionClaims<UnitFraction32Width, UnitFraction32>.ShiftsMatchOracle,
            domain: UnitFraction32Domain,
            id: "unit-fraction32.shifts-vs-oracle",
            width: 1
        ),
        ClaimCase(
            claim: UnitFractionClaims<UnitFraction32Width, UnitFraction32>.ConstructionAndRefusals,
            id: "unit-fraction32.construction-and-refusals"
        ),
        SweptCase(
            claim: UnitFractionClaims<UnitFraction32Width, UnitFraction32>.TextMatchesOracle,
            domain: UnitFraction32Domain,
            id: "unit-fraction32.text-vs-oracle",
            width: 1
        ),
        ClaimCase(
            claim: UnitFractionClaims<UnitFraction32Width, UnitFraction32>.ParseLadderHolds,
            id: "unit-fraction32.parse-ladder"
        ),
        SweptCase(
            claim: UnitFractionClaims<UnitFraction32Width, UnitFraction32>.DoubleProjectionExact,
            domain: UnitFraction32Domain,
            id: "unit-fraction32.double-projection-exact",
            width: 1
        ),
        // The seam the closed interval's remarks describe in prose, stated from the FRACTION side. closed-unit.kinship-exact
        // already pins the embedding and its refusal at one from the interval side; this case does not restate them.
        SweptCase(
            claim: Subjects.UnitFraction32KinshipExact,
            domain: UnitFraction32Domain,
            id: "unit-fraction32.kinship-exact",
            width: 1
        ),

    ];
    private static LawCase[] ComplexRelationCases() => [
        // ---- FixedComplex ((0, −1)) ----
        Case(
            id: "complex.mul-vs-oracle",
            run: () => Laws.BinaryMatchesOracle(
                lawId: "complex.mul-vs-oracle",
                domain: Complex,
                tier: Tier.Default,
                subject: Subjects.ComplexMultiply,
                oracle: Subjects.MultiplyOracle(
                    pRaw: 0L,
                    qRaw: ComplexQ
                )
            )
        ),
        Case(
            id: "complex.twin-quad",
            run: () => Laws.TwinBinary(
                lawId: "complex.twin-quad",
                domain: Complex,
                tier: Tier.Default,
                first: Subjects.ComplexMultiply,
                second: Subjects.AlgebraMultiply(
                    pRaw: 0L,
                    qRaw: ComplexQ
                ),
                witness: Subjects.MultiplyOracle(
                    pRaw: 0L,
                    qRaw: ComplexQ
                )
            )
        ),
        Case(
            id: "complex.mul-purity",
            run: () => Laws.PureBinary(
                domain: Complex,
                lawId: "complex.mul-purity",
                op: Subjects.ComplexMultiply,
                tier: Tier.Default
            )
        ),
        Case(
            id: "complex.conjugate-involution",
            run: () => Laws.RoundTrip(
                domain: Complex,
                forward: Subjects.ComplexConjugate,
                inverse: Subjects.ComplexConjugate,
                lawId: "complex.conjugate-involution",
                tier: Tier.Default
            )
        ),
        Case(
            id: "complex.negate-involution",
            run: () => Laws.RoundTrip(
                domain: Complex,
                forward: Subjects.ComplexNegate,
                inverse: Subjects.ComplexNegate,
                lawId: "complex.negate-involution",
                tier: Tier.Default
            )
        ),
        Case(
            id: "complex.multiplicative-identity",
            run: () => Laws.IdentityElement(
                domain: Complex,
                identityU: OneRaw,
                identityV: 0L,
                lawId: "complex.multiplicative-identity",
                op: Subjects.ComplexMultiply,
                tier: Tier.Default
            )
        ),
        // Conjugation distributes over multiplication where no wrap occurs; the bounded sublattice is its exact home
        // (at MinValue the two's-complement negation is asymmetric, so the identity is not a full-range law).
        Case(
            id: "complex.conjugate-distributes",
            run: () => Laws.ConjugateSymmetry(
                conj: Subjects.ComplexConjugate,
                domain: Sublattice,
                lawId: "complex.conjugate-distributes",
                mul: Subjects.ComplexMultiply,
                tier: Tier.Default
            )
        ),
        Case(
            id: "algebra.complex-lane-vs-oracle",
            run: () => Laws.BinaryMatchesOracle(
                lawId: "algebra.complex-lane-vs-oracle",
                domain: Complex,
                tier: Tier.Default,
                subject: Subjects.AlgebraMultiply(
                    pRaw: 0L,
                    qRaw: ComplexQ
                ),
                oracle: Subjects.MultiplyOracle(
                    pRaw: 0L,
                    qRaw: ComplexQ
                )
            )
        ),

    ];
    private static LawCase[] SplitRelationCases() => [
        // ---- FixedSplit ((0, +1)) ----
        Case(
            id: "split.mul-vs-oracle",
            run: () => Laws.BinaryMatchesOracle(
                lawId: "split.mul-vs-oracle",
                domain: Split,
                tier: Tier.Default,
                subject: Subjects.SplitMultiply,
                oracle: Subjects.MultiplyOracle(
                    pRaw: 0L,
                    qRaw: SplitQ
                )
            )
        ),
        Case(
            id: "split.twin-quad",
            run: () => Laws.TwinBinary(
                lawId: "split.twin-quad",
                domain: Split,
                tier: Tier.Default,
                first: Subjects.SplitMultiply,
                second: Subjects.AlgebraMultiply(
                    pRaw: 0L,
                    qRaw: SplitQ
                ),
                witness: Subjects.MultiplyOracle(
                    pRaw: 0L,
                    qRaw: SplitQ
                )
            )
        ),
        Case(
            id: "split.norm-vs-oracle",
            run: () => Laws.ScalarMatchesOracle(
                lawId: "split.norm-vs-oracle",
                domain: Split,
                tier: Tier.Default,
                subject: Subjects.SplitNorm,
                oracle: Subjects.NormOracle(
                    pRaw: 0L,
                    qRaw: SplitQ
                )
            )
        ),
        Case(
            id: "split.norm-twin-quad",
            run: () => Laws.ScalarTwin(
                lawId: "split.norm-twin-quad",
                domain: Split,
                tier: Tier.Default,
                first: Subjects.SplitNorm,
                second: Subjects.AlgebraNorm(
                    pRaw: 0L,
                    qRaw: SplitQ
                ),
                witness: Subjects.NormOracle(
                    pRaw: 0L,
                    qRaw: SplitQ
                )
            )
        ),
        Case(
            id: "split.mul-purity",
            run: () => Laws.PureBinary(
                domain: Split,
                lawId: "split.mul-purity",
                op: Subjects.SplitMultiply,
                tier: Tier.Default
            )
        ),
        Case(
            id: "split.conjugate-involution",
            run: () => Laws.RoundTrip(
                domain: Split,
                forward: Subjects.SplitConjugate,
                inverse: Subjects.SplitConjugate,
                lawId: "split.conjugate-involution",
                tier: Tier.Default
            )
        ),
        Case(
            id: "split.norm-multiplicative",
            run: () => Laws.NormMultiplicativity(
                combineNorms: Subjects.FixedMultiply,
                domain: Sublattice,
                lawId: "split.norm-multiplicative",
                mul: Subjects.SplitMultiply,
                norm: Subjects.SplitNorm,
                tier: Tier.Default
            )
        ),

    ];
    private static LawCase[] DualRelationCases() => [
        // ---- FixedDual<FixedQ4816> ((0, 0)) ----
        Case(
            id: "dual.mul-vs-oracle",
            run: () => Laws.BinaryMatchesOracle(
                lawId: "dual.mul-vs-oracle",
                domain: Dual,
                tier: Tier.Default,
                subject: Subjects.DualMultiply,
                oracle: Subjects.MultiplyOracle(
                    pRaw: 0L,
                    qRaw: 0L
                )
            )
        ),
        Case(
            id: "dual.twin-quad",
            run: () => Laws.TwinBinary(
                lawId: "dual.twin-quad",
                domain: Dual,
                tier: Tier.Default,
                first: Subjects.DualMultiply,
                second: Subjects.AlgebraMultiply(
                    pRaw: 0L,
                    qRaw: 0L
                ),
                witness: Subjects.MultiplyOracle(
                    pRaw: 0L,
                    qRaw: 0L
                )
            )
        ),
        Case(
            id: "dual.mul-purity",
            run: () => Laws.PureBinary(
                domain: Dual,
                lawId: "dual.mul-purity",
                op: Subjects.DualMultiply,
                tier: Tier.Default
            )
        ),

    ];
    private static LawCase[] ComplexRestCases() => [
        // ---- FixedComplex: the rest of the planar rotation type ----
        SweptCase(
            claim: Subjects.ComplexAdditiveGroupExact,
            domain: Complex,
            id: "complex.additive-group-exact",
            width: 2
        ),
        SweptCase(
            claim: Subjects.ComplexPresentationSeam,
            domain: Complex,
            id: "complex.presentation-seam",
            width: 2
        ),
        Case(
            id: "complex.div-vs-oracle",
            run: () => Laws.BinaryMatchesOracle(
                domain: ComplexDivide,
                lawId: "complex.div-vs-oracle",
                oracle: Subjects.ComplexDivideOracle,
                subject: Subjects.ComplexDivide,
                tier: Tier.Default
            )
        ),
        ClaimCase(
            claim: Subjects.ComplexDivRefusalAndUnit,
            id: "complex.div-refusal-and-unit"
        ),
        SweptCase(
            claim: Subjects.ComplexMagnitudeExact,
            domain: ComplexDirection,
            id: "complex.magnitude-vs-oracle",
            width: 2
        ),
        SweptCase(
            claim: Subjects.ComplexNormalizeUnitDirection,
            domain: ComplexDirection,
            id: "complex.normalize-unit-direction",
            width: 2
        ),
        SweptCase(
            claim: Subjects.ComplexFromToDirection,
            domain: ComplexDirection,
            id: "complex.from-to-direction",
            width: 2
        ),
        ClaimCase(
            claim: Subjects.ComplexAngleSeam,
            id: "complex.angle-seam"
        ),
        ClaimCase(
            claim: Subjects.ComplexMultiplyRoutesAllocateNothing,
            id: "complex.multiply-routes-allocate-nothing"
        ),
        Case(
            id: "complex.rotate-vs-oracle",
            run: () => Laws.BinaryMatchesOracle(
                lawId: "complex.rotate-vs-oracle",
                domain: ComplexRotate,
                tier: Tier.Default,
                subject: Subjects.ComplexRotate,
                oracle: Subjects.MultiplyOracle(
                    pRaw: 0L,
                    qRaw: ComplexQ
                )
            )
        ),

    ];
    private static LawCase[] SplitRestCases() => [
        // ---- FixedSplit: the rest of the hyperbolic sibling ----
        SweptCase(
            claim: Subjects.SplitAdditiveGroupExact,
            domain: Split,
            id: "split.additive-group-exact",
            width: 2
        ),
        Case(
            id: "split.div-vs-oracle",
            run: () => Laws.BinaryMatchesOracle(
                domain: SplitDivide,
                lawId: "split.div-vs-oracle",
                oracle: Subjects.SplitDivideOracle,
                subject: Subjects.SplitDivide,
                tier: Tier.Default
            )
        ),
        SweptCase(
            claim: Subjects.SplitUnitAndDivision,
            domain: Split,
            id: "split.unit-and-division",
            width: 2
        ),
        Case(
            id: "split.transform-vs-oracle",
            run: () => Laws.BinaryMatchesOracle(
                lawId: "split.transform-vs-oracle",
                domain: SplitTransform,
                tier: Tier.Default,
                subject: Subjects.SplitTransform,
                oracle: Subjects.MultiplyOracle(
                    pRaw: 0L,
                    qRaw: SplitQ
                )
            )
        ),
        ClaimCase(
            claim: Subjects.SplitRapidityLadderClaim,
            id: "split.rapidity-ladder"
        ),

    ];
    private static LawCase[] DualRestCases() => [
        // ---- FixedDual: the rest of the dual construction, INCLUDING the two kernels the covered member id hides ----
        SweptCase(
            claim: Subjects.DualAdditiveGroupExact,
            domain: Dual,
            id: "dual.additive-group-exact",
            width: 2
        ),
        SweptCase(
            claim: Subjects.DualSeedsAndIdentities,
            domain: Dual,
            id: "dual.seeds-and-identities",
            width: 2
        ),
        Case(
            id: "dual.divide-vs-oracle",
            run: () => Laws.BinaryMatchesOracle(
                domain: DualDivide,
                lawId: "dual.divide-vs-oracle",
                oracle: Subjects.DualDivideOracle,
                subject: Subjects.DualDivide,
                tier: Tier.Default
            )
        ),
        ClaimCase(
            claim: Subjects.DualDivideRefusals,
            id: "dual.divide-refusals"
        ),
        SweptCase(
            claim: Subjects.DualTranscendentalLifts,
            domain: Dual,
            id: "dual.transcendental-lifts",
            width: 2
        ),
        Case(
            id: "dual.quaternion-mul-vs-oracle",
            run: () => Laws.VectorMatchesOracle(
                domain: DualQuaternion,
                lawId: "dual.quaternion-mul-vs-oracle",
                oracle: Subjects.DualQuaternionMultiplyOracle,
                subject: Subjects.DualQuaternionMultiplyLanes,
                tier: Tier.Default,
                width: 8
            )
        ),
        Case(
            id: "dual.generic-carrier-two-roundings",
            run: () => Laws.VectorMatchesOracle(
                domain: DualGeneric,
                lawId: "dual.generic-carrier-two-roundings",
                oracle: Subjects.DualSplitMultiplyOracle,
                subject: Subjects.DualSplitMultiplyLanes,
                tier: Tier.Default,
                width: 4
            )
        ),

    ];
    private static LawCase[] QuaternionCases() => [
        // ---- FixedQuaternion ----
        Case(
            id: "quaternion.mul-vs-oracle",
            run: () => Laws.VectorMatchesOracle(
                domain: Quaternion,
                lawId: "quaternion.mul-vs-oracle",
                oracle: Subjects.QuaternionMultiplyOracle,
                subject: Subjects.QuaternionMultiplyLanes,
                tier: Tier.Default,
                width: 4
            )
        ),
        SweptCase(
            claim: Subjects.QuaternionDotExact,
            domain: Quaternion,
            id: "quaternion.dot-vs-oracle",
            width: 4
        ),
        SweptCase(
            claim: Subjects.QuaternionScaleExact,
            domain: Quaternion,
            id: "quaternion.scale-vs-oracle",
            width: 4
        ),
        SweptCase(
            claim: Subjects.QuaternionAdditiveGroupExact,
            domain: Quaternion,
            id: "quaternion.additive-group-exact",
            width: 4
        ),
        SweptCase(
            claim: Subjects.QuaternionConjugateAntiautomorphism,
            domain: QuaternionSublattice,
            id: "quaternion.conjugate-antiautomorphism",
            width: 4
        ),
        SweptCase(
            claim: Subjects.QuaternionNormExact,
            domain: QuaternionDirection,
            id: "quaternion.norm-vs-oracle",
            width: 4
        ),
        SweptCase(
            claim: Subjects.QuaternionInverseExact,
            domain: QuaternionDirection,
            id: "quaternion.inverse-vs-oracle",
            width: 4
        ),
        SweptCase(
            claim: Subjects.QuaternionNormalizeUnitDirection,
            domain: QuaternionDirection,
            id: "quaternion.normalize-unit-direction",
            width: 4
        ),
        SweptCase(
            claim: Subjects.QuaternionRotateExact,
            domain: QuaternionRotate,
            id: "quaternion.rotate-vs-oracle",
            width: 4
        ),
        ClaimCase(
            claim: Subjects.QuaternionAxisAngleLadderClaim,
            id: "quaternion.axis-angle-ladder"
        ),
        // The inbound seam. Judged against the SAME ladder as vector.adoption-ladder, deliberately: the two doors
        // must agree, and sharing the table is what would catch them drifting apart. Its second leg states what a
        // three-lane ladder cannot — that the seam does not renormalize.
        ClaimCase(
            claim: Subjects.QuaternionAdoptionMatchesLadder,
            id: "quaternion.adoption-ladder"
        ),
        ClaimCase(
            claim: Subjects.QuaternionExpLogSeam,
            id: "quaternion.exp-log-seam"
        ),

        // SinCosRaw was gated by nothing until these two cases: the case above says so in its own leg text. It is
        // internal, so the coverage manifest cannot name it and the hole was invisible to the ratchet. The reference
        // is Oracles.EncloseSinCos carried past the signed carrier by the
        // angle-addition identity, with the envelope derived from |c - 2^64/2pi| <= 1/2 rather than fitted to what the
        // subject happens to do. Proved by masking the top angle bit — the exact defect the member exists to avoid —
        // which reddens these two and NOTHING else in the tier.
        ClaimCase(
            claim: TransformKernelClaims.SinCosRawFullUnsignedWidthSurface,
            id: "quaternion.sincos-raw-full-unsigned-width"
        ),
        ClaimCase(
            claim: TransformKernelClaims.SinCosRawWidthSweepSurface,
            id: "quaternion.sincos-raw-width-sweep"
        ),
        SweptCase(
            claim: Subjects.QuaternionFromToShortestArc,
            domain: QuaternionDirection,
            id: "quaternion.from-to-shortest-arc",
            width: 4
        ),
        ClaimCase(
            claim: Subjects.QuaternionSlerpEndpointsAndArc,
            id: "quaternion.slerp-endpoints-and-arc"
        ),

        // The renderer's seam, and the one member of this type an algebra law cannot reach: the argument ORDER into
        // System.Numerics.Quaternion. Swapping X and W in ToQuaternion leaves every other case in this suite green,
        // which is exactly why the waiver that stood here — 'the algebra laws pin the exact raw contract instead' — was
        // false about the only thing this member decides on its own.
        ClaimCase(
            claim: Subjects.QuaternionPresentationMatchesLadder,
            id: "quaternion.presentation-ladder"
        ),

    ];
    private static LawCase[] FractionalRelationCases() => [
        // ---- one fractional relation (0, ½): the fused fractional lane vs the oracle ----
        Case(
            id: "algebra.fractional-mul-vs-oracle",
            run: () => Laws.BinaryMatchesOracle(
                lawId: "algebra.fractional-mul-vs-oracle",
                domain: Fractional,
                tier: Tier.Default,
                subject: Subjects.AlgebraMultiply(
                    pRaw: 0L,
                    qRaw: HalfQ
                ),
                oracle: Subjects.MultiplyOracle(
                    pRaw: 0L,
                    qRaw: HalfQ
                )
            )
        ),
        Case(
            id: "algebra.fractional-norm-vs-oracle",
            run: () => Laws.ScalarMatchesOracle(
                lawId: "algebra.fractional-norm-vs-oracle",
                domain: Fractional,
                tier: Tier.Default,
                subject: Subjects.AlgebraNorm(
                    pRaw: 0L,
                    qRaw: HalfQ
                ),
                oracle: Subjects.NormOracle(
                    pRaw: 0L,
                    qRaw: HalfQ
                )
            )
        ),
        Case(
            id: "algebra.fractional-mobius-vs-oracle",
            run: () => Laws.MobiusMatchesOracle(
                lawId: "algebra.fractional-mobius-vs-oracle",
                domain: Fractional,
                tier: Tier.Default,
                subject: Subjects.AlgebraMobius(
                    pRaw: 0L,
                    qRaw: HalfQ
                ),
                oracleNumerator: Subjects.MobiusNumeratorOracle(
                    pRaw: 0L,
                    qRaw: HalfQ
                )
            )
        ),

    ];
    private static LawCase[] MobiusCases() => [
        // ---- Möbius exactness for integer relations ----
        Case(
            id: "mobius.integer-0,-1",
            run: () => Laws.MobiusMatchesOracle(
                lawId: "mobius.integer-0,-1",
                domain: Mobius,
                tier: Tier.Default,
                subject: Subjects.AlgebraMobius(
                    pRaw: 0L,
                    qRaw: ComplexQ
                ),
                oracleNumerator: Subjects.MobiusNumeratorOracle(
                    pRaw: 0L,
                    qRaw: ComplexQ
                )
            )
        ),
        Case(
            id: "mobius.integer-1,1",
            run: () => Laws.MobiusMatchesOracle(
                lawId: "mobius.integer-1,1",
                domain: Mobius,
                tier: Tier.Default,
                subject: Subjects.AlgebraMobius(
                    pRaw: OneRaw,
                    qRaw: OneRaw
                ),
                oracleNumerator: Subjects.MobiusNumeratorOracle(
                    pRaw: OneRaw,
                    qRaw: OneRaw
                )
            )
        ),
        Case(
            id: "mobius.integer-2,1",
            run: () => Laws.MobiusMatchesOracle(
                lawId: "mobius.integer-2,1",
                domain: Mobius,
                tier: Tier.Default,
                subject: Subjects.AlgebraMobius(
                    pRaw: (2L * OneRaw),
                    qRaw: OneRaw
                ),
                oracleNumerator: Subjects.MobiusNumeratorOracle(
                    pRaw: (2L * OneRaw),
                    qRaw: OneRaw
                )
            )
        ),

    ];
    private static LawCase[] IntegerDivisionCases() => [
        // ---- Integer floored division: the generics the private Int128/BigInteger copies collapsed into ----
        // The carrier's raw longs are the operand source, so the domain's edge bias lands on the signs and the extremes
        // where floored and truncated division disagree. The oracle divides in arbitrary width, where the carrier's one
        // unrepresentable quotient is an ordinary value.
        Case(
            id: "integer.floor-divide-vs-oracle",
            run: () => {
                Laws.ScalarBinaryMatchesOracle(
                    domain: Scalar,
                    lawId: "integer.floor-divide-vs-oracle",
                    oracle: Subjects.FloorDivideOracle,
                    subject: Subjects.FloorDivide,
                    tier: Tier.Default
                );
                Laws.Claim(
                    claim: Subjects.IntegerDivisionLimitsRefuse,
                    lawId: "integer.floor-divide-vs-oracle"
                );
            }
        ),
        Case(
            id: "integer.ceiling-divide-vs-oracle",
            run: () => Laws.ScalarBinaryMatchesOracle(
                domain: Scalar,
                lawId: "integer.ceiling-divide-vs-oracle",
                oracle: Subjects.CeilingDivideOracle,
                subject: Subjects.CeilingDivide,
                tier: Tier.Default
            )
        ),
        // The pair is pinned component-wise: its quotient against the same oracle the standalone quotient answers to,
        // and its remainder against the exact floored remainder — so a pair that agreed only in aggregate would fail.
        Case(
            id: "integer.floor-divrem-quotient",
            run: () => Laws.ScalarBinaryMatchesOracle(
                domain: Scalar,
                lawId: "integer.floor-divrem-quotient",
                oracle: Subjects.FloorDivideOracle,
                subject: Subjects.FloorDivRemQuotient,
                tier: Tier.Default
            )
        ),
        Case(
            id: "integer.floor-divrem-remainder",
            run: () => Laws.ScalarBinaryMatchesOracle(
                domain: Scalar,
                lawId: "integer.floor-divrem-remainder",
                oracle: Subjects.FloorDivRemRemainderOracle,
                subject: Subjects.FloorDivRemRemainder,
                tier: Tier.Default
            )
        ),

    ];
}
