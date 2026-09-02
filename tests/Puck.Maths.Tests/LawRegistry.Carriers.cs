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
                oracle: Subjects.UnitFraction32MultiplyOracle,
                subject: Subjects.UnitFraction32Multiply,
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
        Case(
            id: "smoke.presented-star-unguarded-refuses",
            run: () => Laws.Claim(
                claim: Subjects.UnguardedStarRefuses,
                lawId: "smoke.presented-star-unguarded-refuses"
            )
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
        Case(
            id: "smoke.presented-tangle-composes",
            run: () => Laws.Claim(
                claim: Subjects.TangleComposesAtSmokeWidth,
                lawId: "smoke.presented-tangle-composes"
            )
        ),
        // Ledger row 19's transfer, named: the morphism out of the free monoid on one letter per partial quotient,
        // carrying each to that quotient's digit element, reaches the element the transfer's own fold reaches and the
        // entries its module run reads out.
        Case(
            id: "smoke.presented-functor-twin",
            run: () => Laws.Claim(
                claim: Subjects.FunctorTwinsTransfer,
                lawId: "smoke.presented-functor-twin"
            )
        ),
        // The second product at its smallest: the seven words of two letters at a window of two, their whole table
        // against the interleaving enumeration, and the one-letter quasi-shuffle whose collisions are already there.
        Case(
            id: "smoke.presented-shuffle-twin",
            run: () => Laws.Claim(
                claim: Subjects.ShuffleComposesAtSmokeWindow,
                lawId: "smoke.presented-shuffle-twin"
            )
        ),

    ];
    private static LawCase[] RootCoreCases() => [
        // ---- Root Core/Sampling public-surface laws ----
        Case(
            id: "sampling.bit-mix-constants-invert",
            run: () => Laws.Claim(
                claim: CoreSurfaceClaims.BitMixConstantsInvertSurface,
                lawId: "sampling.bit-mix-constants-invert"
            )
        ),
        Case(
            id: "sampling.bit-mix-is-a-permutation",
            run: () => Laws.Claim(
                claim: CoreSurfaceClaims.BitMixIsAPermutationSurface,
                lawId: "sampling.bit-mix-is-a-permutation"
            )
        ),
        Case(
            id: "scalar.cyclic-rotation-closes-its-loop",
            run: () => Laws.Claim(
                claim: CoreSurfaceClaims.CyclicRotationStructureSurface,
                lawId: "scalar.cyclic-rotation-closes-its-loop"
            )
        ),
        Case(
            id: "core.big-integer-is-prime-vs-oracle",
            run: () => Laws.Claim(
                claim: CoreSurfaceClaims.BigIntegerIsPrimeSurface,
                lawId: "core.big-integer-is-prime-vs-oracle"
            )
        ),
        Case(
            id: "core.big-integer-prime-factors-vs-word-kernel",
            run: () => Laws.Claim(
                claim: CoreSurfaceClaims.BigIntegerPrimeFactorsSurface,
                lawId: "core.big-integer-prime-factors-vs-word-kernel"
            )
        ),

        // The full-carrier scale the case above only samples, plus the Jacobi statement it cannot make. The Jacobi law
        // sits at Deep rather than Exhaustive on purpose: it is a two-second grid of statements, not a carrier-wide
        // sweep, so parking it behind an opt-in tier would cost it its everyday coverage. Its oracle is the
        // factor-and-Euler DEFINITION, with no reciprocity step anywhere in it, so at composite moduli it cannot pick
        // the same wrong value as the subject by running the library's own sibling descent on both sides.
        // The two the factorization surface could not make before: it took the gate's word at the one value the twelve
        // bases decide wrongly, and its depth was the operand's multiplicity rather than the heap.
        Case(
            id: "core.witness-set-boundary-factors-exactly",
            run: () => Laws.Claim(
                claim: CoreSurfaceClaims.WitnessSetBoundaryFactorsExactly,
                lawId: "core.witness-set-boundary-factors-exactly"
            )
        ),
        Case(
            id: "core.prime-counting-is-dense-against-a-sieve",
            run: () => Laws.Claim(
                claim: CoreSurfaceClaims.PrimeCountingIsDenseAgainstASieve,
                lawId: "core.prime-counting-is-dense-against-a-sieve"
            )
        ),
        Case(
            id: "core.deep-multiplicity-factors-without-stack-growth",
            run: () => Laws.Claim(
                claim: CoreSurfaceClaims.DeepMultiplicityFactorsWithoutStackGrowth,
                lawId: "core.deep-multiplicity-factors-without-stack-growth"
            )
        ),

        Case(
            id: "core.jacobi-symbol-cross-carrier",
            run: () => Laws.Claim(
                claim: PrimalityScaleClaims.JacobiSymbolSurface,
                lawId: "core.jacobi-symbol-cross-carrier"
            )
        ),

        // Descriptors and elements that compared unequal to themselves, and an exponential that answered outside the
        // subdomain it documents instead of refusing. All three were silent: array identity masquerading as value
        // equality, and a closed form read off the scalar lane of a square that was not scalar.
        Case(
            id: "algebra.clifford-descriptor-identity-is-the-signature",
            run: () => Laws.Claim(
                claim: GeometricAlgebraClaims.CliffordDescriptorIdentitySurface,
                lawId: "algebra.clifford-descriptor-identity-is-the-signature"
            )
        ),
        Case(
            id: "algebra.clifford-exponential-scalar-square-domain",
            run: () => Laws.Claim(
                claim: GeometricAlgebraClaims.CliffordExponentialDomainSurface,
                lawId: "algebra.clifford-exponential-scalar-square-domain"
            )
        ),
        Case(
            id: "algebra.monogenic-identity-is-tail-and-coordinates",
            run: () => Laws.Claim(
                claim: GeometricAlgebraClaims.MonogenicIdentitySurface,
                lawId: "algebra.monogenic-identity-is-tail-and-coordinates"
            )
        ),

        // Carriers that admitted values they advertise they do not hold, a letter mask that broadened a split predicate
        // into a false positive, and colour indices that were never checked at all.
        Case(
            id: "presented.rational-material-admits-only-rationals",
            run: () => Laws.Claim(
                claim: OracleClaims.RationalMaterialAdmitsOnlyRationals,
                lawId: "presented.rational-material-admits-only-rationals"
            )
        ),
        Case(
            id: "presented.counting-material-admits-only-naturals",
            run: () => Laws.Claim(
                claim: OracleClaims.CountingMaterialAdmitsOnlyNaturals,
                lawId: "presented.counting-material-admits-only-naturals"
            )
        ),
        Case(
            id: "presented.letter-mask-refuses-a-split-block",
            run: () => Laws.Claim(
                claim: OracleClaims.LetterMaskRefusesASplitBlock,
                lawId: "presented.letter-mask-refuses-a-split-block"
            )
        ),
        Case(
            id: "presented.generator-colours-are-bounded-indices",
            run: () => Laws.Claim(
                claim: OracleClaims.GeneratorColoursAreBoundedIndices,
                lawId: "presented.generator-colours-are-bounded-indices"
            )
        ),
        Case(
            id: "core.factorization-full-width-sweep",
            run: () => Laws.Claim(
                claim: PrimalityScaleClaims.FactorizationFullWidthSurface,
                lawId: "core.factorization-full-width-sweep"
            )
        ),
        Case(
            id: "core.big-integer-square-root-vs-unsigned",
            run: () => Laws.Claim(
                claim: CoreSurfaceClaims.BigIntegerSquareRootSurface,
                lawId: "core.big-integer-square-root-vs-unsigned"
            )
        ),
        Case(
            id: "core.big-integer-to-double-vs-exact-neighbours",
            run: () => Laws.Claim(
                claim: CoreSurfaceClaims.BigIntegerToDoubleSurface,
                lawId: "core.big-integer-to-double-vs-exact-neighbours"
            )
        ),
        Case(
            id: "core.big-integer-modular-inverse-vs-hensel",
            run: () => Laws.Claim(
                claim: CoreSurfaceClaims.BigIntegerModularInverseSurface,
                lawId: "core.big-integer-modular-inverse-vs-hensel"
            )
        ),
        Case(
            id: "core.big-integer-modular-square-root-vs-prime-field",
            run: () => Laws.Claim(
                claim: CoreSurfaceClaims.BigIntegerModularSquareRootSurface,
                lawId: "core.big-integer-modular-square-root-vs-prime-field"
            )
        ),
        Case(
            id: "core.binary-integer-contracts",
            run: () => Laws.Claim(
                claim: CoreSurfaceClaims.BinaryIntegerSurface,
                lawId: "core.binary-integer-contracts"
            )
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
        Case(
            id: "core.number-theory-contracts",
            run: () => Laws.Claim(
                claim: CoreSurfaceClaims.NumberTheorySurface,
                lawId: "core.number-theory-contracts"
            )
        ),
        Case(
            id: "core.real-quadratic-field-descriptor",
            run: () => Laws.Claim(
                claim: CoreSurfaceClaims.RealQuadraticFieldSurface,
                lawId: "core.real-quadratic-field-descriptor"
            )
        ),
        Case(
            id: "core.real-quadratic-field-and-conversion",
            run: () => Laws.Claim(
                claim: CoreSurfaceClaims.RealQuadraticSurface,
                lawId: "core.real-quadratic-field-and-conversion"
            )
        ),
        Case(
            id: "core.prime-extensions-vs-trial-division",
            run: () => Laws.Claim(
                claim: CoreSurfaceClaims.PrimeExtensionsSurface,
                lawId: "core.prime-extensions-vs-trial-division"
            )
        ),
        Case(
            id: "core.unsigned-integer-contracts",
            run: () => Laws.Claim(
                claim: CoreSurfaceClaims.UnsignedIntegerSurface,
                lawId: "core.unsigned-integer-contracts"
            )
        ),
        Case(
            id: "core.fnv1a-published-vector",
            run: () => Laws.Claim(
                claim: CoreSurfaceClaims.Fnv1aSurface,
                lawId: "core.fnv1a-published-vector"
            )
        ),
        Case(
            id: "core.monotonic-partitioner-fast-invariants",
            run: () => Laws.Claim(
                claim: CoreSurfaceClaims.MonotonicPartitionerSurface,
                lawId: "core.monotonic-partitioner-fast-invariants"
            )
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
        Case(
            id: "scalar.grid-and-construction",
            run: () => Laws.Claim(
                claim: Subjects.FixedGridAndConstruction,
                lawId: "scalar.grid-and-construction"
            )
        ),
        // The OUTWARD double seam. It was waived as presentation-only for the whole of this suite's life; the waiver's
        // premise does not survive its own siblings, because unit-fraction16/32.double-projection-exact pin the same
        // conversion exactly and unsigned-scalar.double-seam pins the unsigned twin of THIS one — a Q48.16 narrowing with
        // genuine precision loss — against a hand ladder. Inexact is not unspecified: the map is a total function of the
        // raw and every value it takes is decidable in integers.
        Case(
            id: "scalar.double-projection-vs-oracle",
            run: () => Laws.SweptClaim(
                claim: Subjects.FixedDoubleProjectionExact,
                domain: Scalar,
                lawId: "scalar.double-projection-vs-oracle",
                tier: Tier.Default,
                width: 1
            )
        ),
        Case(
            id: "scalar.additive-ops-vs-oracle",
            run: () => Laws.SweptClaim(
                claim: Subjects.FixedAdditiveOpsExact,
                domain: Scalar,
                lawId: "scalar.additive-ops-vs-oracle",
                tier: Tier.Default,
                width: 1
            )
        ),
        Case(
            id: "scalar.checked-ops-refuse",
            run: () => Laws.SweptClaim(
                claim: Subjects.FixedCheckedOpsRefuse,
                domain: Scalar,
                lawId: "scalar.checked-ops-refuse",
                tier: Tier.Default,
                width: 1
            )
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
        Case(
            id: "scalar.modulus-vs-oracle",
            run: () => Laws.SweptClaim(
                claim: Subjects.FixedModulusExact,
                domain: Scalar,
                lawId: "scalar.modulus-vs-oracle",
                tier: Tier.Default,
                width: 1
            )
        ),
        Case(
            id: "scalar.order-vs-oracle",
            run: () => Laws.SweptClaim(
                claim: Subjects.FixedOrderExact,
                domain: Scalar,
                lawId: "scalar.order-vs-oracle",
                tier: Tier.Default,
                width: 2
            )
        ),
        Case(
            id: "scalar.magnitude-selection-vs-oracle",
            run: () => Laws.SweptClaim(
                claim: Subjects.FixedMagnitudeSelectionExact,
                domain: Scalar,
                lawId: "scalar.magnitude-selection-vs-oracle",
                tier: Tier.Default,
                width: 1
            )
        ),
        Case(
            id: "scalar.integral-parts-vs-oracle",
            run: () => Laws.SweptClaim(
                claim: Subjects.FixedIntegralPartsExact,
                domain: Scalar,
                lawId: "scalar.integral-parts-vs-oracle",
                tier: Tier.Default,
                width: 1
            )
        ),
        Case(
            id: "scalar.predicates-classify",
            run: () => Laws.SweptClaim(
                claim: Subjects.FixedPredicatesClassify,
                domain: Scalar,
                lawId: "scalar.predicates-classify",
                tier: Tier.Default,
                width: 1
            )
        ),
        Case(
            id: "scalar.lerp-endpoints-and-oracle",
            run: () => Laws.SweptClaim(
                claim: Subjects.FixedLerpEndpointsAndOracle,
                domain: Scalar,
                lawId: "scalar.lerp-endpoints-and-oracle",
                tier: Tier.Default,
                width: 2
            )
        ),
        Case(
            id: "scalar.text-round-trip",
            run: () => Laws.SweptClaim(
                claim: Subjects.FixedTextRoundTrip,
                domain: ScalarText,
                lawId: "scalar.text-round-trip",
                tier: Tier.Default,
                width: 1
            )
        ),
        Case(
            id: "scalar.text-ladder-and-refusals",
            run: () => Laws.Claim(
                claim: Subjects.FixedTextLadderAndRefusals,
                lawId: "scalar.text-ladder-and-refusals"
            )
        ),
        Case(
            id: "scalar.styled-exponent-compensation",
            run: () => Laws.Claim(
                claim: Subjects.StyledExponentCompensation,
                lawId: "scalar.styled-exponent-compensation"
            )
        ),
        Case(
            id: "scalar.generic-conversion-modes",
            run: () => Laws.Claim(
                claim: Subjects.GenericConversionModes,
                lawId: "scalar.generic-conversion-modes"
            )
        ),
        Case(
            id: "scalar.culture-token-ambiguity-refused",
            run: () => Laws.Claim(
                claim: Subjects.CultureTokenAmbiguityRefused,
                lawId: "scalar.culture-token-ambiguity-refused"
            )
        ),
        Case(
            id: "scalar.sqrt-exact",
            run: () => Laws.SweptClaim(
                claim: Subjects.FixedSqrtExact,
                domain: ScalarTranscendental,
                lawId: "scalar.sqrt-exact",
                tier: Tier.Default,
                width: 1
            )
        ),
        Case(
            id: "scalar.log2-vs-series",
            run: () => Laws.SweptClaim(
                claim: Subjects.FixedLog2WithinEnvelope,
                domain: ScalarTranscendental,
                lawId: "scalar.log2-vs-series",
                tier: Tier.Default,
                width: 1
            )
        ),
        Case(
            id: "scalar.exp2-vs-series",
            run: () => Laws.SweptClaim(
                claim: Subjects.FixedExp2WithinEnvelope,
                domain: ScalarTranscendental,
                lawId: "scalar.exp2-vs-series",
                tier: Tier.Default,
                width: 1
            )
        ),
        Case(
            id: "scalar.sincos-vs-series",
            run: () => Laws.SweptClaim(
                claim: Subjects.FixedSinCosWithinEnvelope,
                domain: ScalarTranscendental,
                lawId: "scalar.sincos-vs-series",
                tier: Tier.Default,
                width: 1
            )
        ),
        Case(
            id: "scalar.atan2-vs-series",
            run: () => Laws.SweptClaim(
                claim: Subjects.FixedAtan2WithinEnvelope,
                domain: ScalarTranscendental,
                lawId: "scalar.atan2-vs-series",
                tier: Tier.Default,
                width: 1
            )
        ),
        Case(
            id: "scalar.pow-exact-lattice",
            run: () => Laws.Claim(
                claim: Subjects.FixedPowExactLattice,
                lawId: "scalar.pow-exact-lattice"
            )
        ),
        Case(
            id: "scalar.pow-envelope",
            run: () => Laws.SweptClaim(
                claim: Subjects.FixedPowWithinEnvelope,
                domain: ScalarTranscendental,
                lawId: "scalar.pow-envelope",
                tier: Tier.Default,
                width: 1
            )
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
        Case(
            id: "q1648.grid-and-construction",
            run: () => Laws.Claim(
                claim: Subjects.Q1648GridAndConstruction,
                lawId: "q1648.grid-and-construction"
            )
        ),
        Case(
            id: "q1648.additive-ops-vs-oracle",
            run: () => Laws.SweptClaim(
                claim: Subjects.Q1648AdditiveOpsExact,
                domain: Q1648Scalar,
                lawId: "q1648.additive-ops-vs-oracle",
                tier: Tier.Default,
                width: 1
            )
        ),
        Case(
            id: "q1648.checked-ops-refuse",
            run: () => Laws.SweptClaim(
                claim: Subjects.Q1648CheckedOpsRefuse,
                domain: Q1648Scalar,
                lawId: "q1648.checked-ops-refuse",
                tier: Tier.Default,
                width: 1
            )
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
        Case(
            id: "q1648.modulus-vs-oracle",
            run: () => Laws.SweptClaim(
                claim: Subjects.Q1648ModulusExact,
                domain: Q1648Scalar,
                lawId: "q1648.modulus-vs-oracle",
                tier: Tier.Default,
                width: 1
            )
        ),
        Case(
            id: "q1648.order-vs-oracle",
            run: () => Laws.SweptClaim(
                claim: Subjects.Q1648OrderExact,
                domain: Q1648Scalar,
                lawId: "q1648.order-vs-oracle",
                tier: Tier.Default,
                width: 2
            )
        ),
        Case(
            id: "q1648.magnitude-selection-vs-oracle",
            run: () => Laws.SweptClaim(
                claim: Subjects.Q1648MagnitudeSelectionExact,
                domain: Q1648Scalar,
                lawId: "q1648.magnitude-selection-vs-oracle",
                tier: Tier.Default,
                width: 1
            )
        ),
        Case(
            id: "q1648.integral-parts-vs-oracle",
            run: () => Laws.SweptClaim(
                claim: Subjects.Q1648IntegralPartsExact,
                domain: Q1648Scalar,
                lawId: "q1648.integral-parts-vs-oracle",
                tier: Tier.Default,
                width: 1
            )
        ),
        Case(
            id: "q1648.predicates-classify",
            run: () => Laws.SweptClaim(
                claim: Subjects.Q1648PredicatesClassify,
                domain: Q1648Scalar,
                lawId: "q1648.predicates-classify",
                tier: Tier.Default,
                width: 1
            )
        ),
        Case(
            id: "q1648.lerp-endpoints-and-oracle",
            run: () => Laws.SweptClaim(
                claim: Subjects.Q1648LerpEndpointsAndOracle,
                domain: Q1648Scalar,
                lawId: "q1648.lerp-endpoints-and-oracle",
                tier: Tier.Default,
                width: 2
            )
        ),
        Case(
            id: "q1648.text-round-trip",
            run: () => Laws.SweptClaim(
                claim: Subjects.Q1648TextRoundTrip,
                domain: Q1648Scalar,
                lawId: "q1648.text-round-trip",
                tier: Tier.Default,
                width: 1
            )
        ),
        Case(
            id: "q1648.text-refusals",
            run: () => Laws.Claim(
                claim: Subjects.Q1648TextRefusals,
                lawId: "q1648.text-refusals"
            )
        ),
        Case(
            id: "q1648.styled-parse-is-genuine",
            run: () => Laws.Claim(
                claim: Subjects.Q1648StyledParseIsGenuine,
                lawId: "q1648.styled-parse-is-genuine"
            )
        ),
        Case(
            id: "q1648.text-parse-ties",
            run: () => Laws.Claim(
                claim: Subjects.Q1648TextParseTies,
                lawId: "q1648.text-parse-ties"
            )
        ),
        Case(
            id: "q1648.decimal-conversion-modes",
            run: () => Laws.Claim(
                claim: Subjects.Q1648DecimalConversionModes,
                lawId: "q1648.decimal-conversion-modes"
            )
        ),
        Case(
            id: "core.scale-decimal-wide-reaches-canonical-core",
            run: () => Laws.Claim(
                claim: Subjects.ScaleDecimalWideReachesCanonicalCore,
                lawId: "core.scale-decimal-wide-reaches-canonical-core"
            )
        ),
        Case(
            id: "q1648.peer-conversion-vs-fixedq4816",
            run: () => Laws.Claim(
                claim: Subjects.Q1648PeerConversionExact,
                lawId: "q1648.peer-conversion-vs-fixedq4816"
            )
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
        Case(
            id: "q3232.grid-and-construction",
            run: () => Laws.Claim(
                claim: Subjects.Q3232GridAndConstruction,
                lawId: "q3232.grid-and-construction"
            )
        ),
        Case(
            id: "q3232.additive-ops-vs-oracle",
            run: () => Laws.SweptClaim(
                claim: Subjects.Q3232AdditiveOpsExact,
                domain: Q3232Scalar,
                lawId: "q3232.additive-ops-vs-oracle",
                tier: Tier.Default,
                width: 1
            )
        ),
        Case(
            id: "q3232.checked-ops-refuse",
            run: () => Laws.SweptClaim(
                claim: Subjects.Q3232CheckedOpsRefuse,
                domain: Q3232Scalar,
                lawId: "q3232.checked-ops-refuse",
                tier: Tier.Default,
                width: 1
            )
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
        Case(
            id: "q3232.modulus-vs-oracle",
            run: () => Laws.SweptClaim(
                claim: Subjects.Q3232ModulusExact,
                domain: Q3232Scalar,
                lawId: "q3232.modulus-vs-oracle",
                tier: Tier.Default,
                width: 1
            )
        ),
        Case(
            id: "q3232.order-vs-oracle",
            run: () => Laws.SweptClaim(
                claim: Subjects.Q3232OrderExact,
                domain: Q3232Scalar,
                lawId: "q3232.order-vs-oracle",
                tier: Tier.Default,
                width: 2
            )
        ),
        Case(
            id: "q3232.magnitude-selection-vs-oracle",
            run: () => Laws.SweptClaim(
                claim: Subjects.Q3232MagnitudeSelectionExact,
                domain: Q3232Scalar,
                lawId: "q3232.magnitude-selection-vs-oracle",
                tier: Tier.Default,
                width: 1
            )
        ),
        Case(
            id: "q3232.integral-parts-vs-oracle",
            run: () => Laws.SweptClaim(
                claim: Subjects.Q3232IntegralPartsExact,
                domain: Q3232Scalar,
                lawId: "q3232.integral-parts-vs-oracle",
                tier: Tier.Default,
                width: 1
            )
        ),
        Case(
            id: "q3232.predicates-classify",
            run: () => Laws.SweptClaim(
                claim: Subjects.Q3232PredicatesClassify,
                domain: Q3232Scalar,
                lawId: "q3232.predicates-classify",
                tier: Tier.Default,
                width: 1
            )
        ),
        Case(
            id: "q3232.lerp-endpoints-and-oracle",
            run: () => Laws.SweptClaim(
                claim: Subjects.Q3232LerpEndpointsAndOracle,
                domain: Q3232Scalar,
                lawId: "q3232.lerp-endpoints-and-oracle",
                tier: Tier.Default,
                width: 2
            )
        ),
        Case(
            id: "q3232.text-round-trip",
            run: () => Laws.SweptClaim(
                claim: Subjects.Q3232TextRoundTrip,
                domain: Q3232Scalar,
                lawId: "q3232.text-round-trip",
                tier: Tier.Default,
                width: 1
            )
        ),
        Case(
            id: "q3232.text-refusals",
            run: () => Laws.Claim(
                claim: Subjects.Q3232TextRefusals,
                lawId: "q3232.text-refusals"
            )
        ),
        Case(
            id: "q3232.styled-parse-is-genuine",
            run: () => Laws.Claim(
                claim: Subjects.Q3232StyledParseIsGenuine,
                lawId: "q3232.styled-parse-is-genuine"
            )
        ),
        Case(
            id: "q3232.text-parse-ties",
            run: () => Laws.Claim(
                claim: Subjects.Q3232TextParseTies,
                lawId: "q3232.text-parse-ties"
            )
        ),
        Case(
            id: "q3232.decimal-conversion-modes",
            run: () => Laws.Claim(
                claim: Subjects.Q3232DecimalConversionModes,
                lawId: "q3232.decimal-conversion-modes"
            )
        ),
        Case(
            id: "q3232.peer-conversion-vs-fixedq4816",
            run: () => Laws.Claim(
                claim: Subjects.Q3232PeerConversionExact,
                lawId: "q3232.peer-conversion-vs-fixedq4816"
            )
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
        Case(
            id: "contribution-fold.analog-pool-bound",
            run: () => Laws.SweptClaim(
                claim: FixedContributionFoldClaims.AnalogPoolBound,
                domain: ContributionFoldAnalog,
                lawId: "contribution-fold.analog-pool-bound",
                tier: Tier.Default,
                width: 2
            )
        ),
        Case(
            id: "contribution-fold.binary-flip-bound-sharp",
            run: () => Laws.Claim(
                claim: FixedContributionFoldClaims.BinaryFlipBoundAndSharpness,
                lawId: "contribution-fold.binary-flip-bound-sharp"
            )
        ),
        Case(
            id: "contribution-fold.binary-composition-induction",
            run: () => Laws.Claim(
                claim: FixedContributionFoldClaims.BinaryCompositionByInduction,
                lawId: "contribution-fold.binary-composition-induction"
            )
        ),
        Case(
            id: "contribution-fold.terminal-quantization-idempotent",
            run: () => Laws.SweptClaim(
                claim: FixedContributionFoldClaims.TerminalQuantizationIdempotence,
                domain: ContributionFoldQuantization,
                lawId: "contribution-fold.terminal-quantization-idempotent",
                tier: Tier.Default,
                width: 2
            )
        ),
        Case(
            id: "contribution-fold.overflow-boundary-exact",
            run: () => Laws.Claim(
                claim: FixedContributionFoldClaims.OverflowBoundaryExact,
                lawId: "contribution-fold.overflow-boundary-exact"
            )
        ),
        Case(
            id: "contribution-fold.configuration-refusals",
            run: () => Laws.Claim(
                claim: FixedContributionFoldClaims.ConfigurationRefusals,
                lawId: "contribution-fold.configuration-refusals"
            )
        ),
        Case(
            id: "contribution-fold.discriminating-examples",
            run: () => Laws.Claim(
                claim: FixedContributionFoldClaims.DiscriminatingExamples,
                lawId: "contribution-fold.discriminating-examples"
            )
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
        Case(
            id: "unsigned-scalar.unchecked-kernels-vs-oracle",
            run: () => Laws.SweptClaim(
                claim: Subjects.UnsignedUncheckedKernelsExact,
                domain: UnsignedScalar,
                lawId: "unsigned-scalar.unchecked-kernels-vs-oracle",
                tier: Tier.Default,
                width: 1
            )
        ),
        Case(
            id: "unsigned-scalar.wrapping-algebra-exact",
            run: () => Laws.SweptClaim(
                claim: Subjects.UnsignedWrappingAlgebraExact,
                domain: UnsignedScalar,
                lawId: "unsigned-scalar.wrapping-algebra-exact",
                tier: Tier.Default,
                width: 1
            )
        ),
        Case(
            id: "unsigned-scalar.checked-operators-refuse",
            run: () => Laws.SweptClaim(
                claim: Subjects.UnsignedCheckedOperatorsRefuse,
                domain: UnsignedScalar,
                lawId: "unsigned-scalar.checked-operators-refuse",
                tier: Tier.Default,
                width: 1
            )
        ),
        Case(
            id: "unsigned-scalar.saturating-and-selection-exact",
            run: () => Laws.SweptClaim(
                claim: Subjects.UnsignedSaturatingAndSelectionExact,
                domain: UnsignedScalar,
                lawId: "unsigned-scalar.saturating-and-selection-exact",
                tier: Tier.Default,
                width: 2
            )
        ),
        Case(
            id: "unsigned-scalar.integer-decomposition-exact",
            run: () => Laws.SweptClaim(
                claim: Subjects.UnsignedIntegerDecompositionExact,
                domain: UnsignedScalar,
                lawId: "unsigned-scalar.integer-decomposition-exact",
                tier: Tier.Default,
                width: 1
            )
        ),
        Case(
            id: "unsigned-scalar.order-and-comparison-exact",
            run: () => Laws.SweptClaim(
                claim: Subjects.UnsignedOrderAndComparisonExact,
                domain: UnsignedScalar,
                lawId: "unsigned-scalar.order-and-comparison-exact",
                tier: Tier.Default,
                width: 1
            )
        ),
        Case(
            id: "unsigned-scalar.number-predicates-exact",
            run: () => Laws.SweptClaim(
                claim: Subjects.UnsignedNumberPredicatesExact,
                domain: UnsignedScalar,
                lawId: "unsigned-scalar.number-predicates-exact",
                tier: Tier.Default,
                width: 1
            )
        ),
        Case(
            id: "unsigned-scalar.construction-and-refusals",
            run: () => Laws.Claim(
                claim: Subjects.UnsignedConstructionAndRefusals,
                lawId: "unsigned-scalar.construction-and-refusals"
            )
        ),
        Case(
            id: "unsigned-scalar.double-seam",
            run: () => Laws.Claim(
                claim: Subjects.UnsignedDoubleSeam,
                lawId: "unsigned-scalar.double-seam"
            )
        ),
        Case(
            id: "unsigned-scalar.text-round-trip",
            run: () => Laws.SweptClaim(
                claim: Subjects.UnsignedTextRoundTrip,
                domain: UnsignedScalar,
                lawId: "unsigned-scalar.text-round-trip",
                tier: Tier.Default,
                width: 1
            )
        ),
        Case(
            id: "unsigned-scalar.text-ladder-and-refusals",
            run: () => Laws.Claim(
                claim: Subjects.UnsignedTextLadderAndRefusals,
                lawId: "unsigned-scalar.text-ladder-and-refusals"
            )
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
        Case(
            id: "closed-unit.unit-and-zero-exact",
            run: () => Laws.SweptClaim(
                claim: Subjects.ClosedUnitUnitAndZeroExact,
                domain: ClosedUnit,
                lawId: "closed-unit.unit-and-zero-exact",
                tier: Tier.Default,
                width: 1
            )
        ),
        Case(
            id: "closed-unit.bounded-ops-exact",
            run: () => Laws.SweptClaim(
                claim: Subjects.ClosedUnitBoundedOpsExact,
                domain: ClosedUnit,
                lawId: "closed-unit.bounded-ops-exact",
                tier: Tier.Default,
                width: 1
            )
        ),
        // The kinship contract: the sampler's half-open grid embeds with no representation event, and the Q48.16 seam
        // states its one rounding out loud.
        Case(
            id: "closed-unit.kinship-exact",
            run: () => Laws.SweptClaim(
                claim: Subjects.ClosedUnitKinshipExact,
                domain: ClosedUnit,
                lawId: "closed-unit.kinship-exact",
                tier: Tier.Default,
                width: 1
            )
        ),
        Case(
            id: "closed-unit.construction-and-refusals",
            run: () => Laws.Claim(
                claim: Subjects.ClosedUnitConstructionAndRefusals,
                lawId: "closed-unit.construction-and-refusals"
            )
        ),
        // The three-factor product exists because a fused sum's term is a charge times two coefficients and the contract
        // is ONE rounding per returned coefficient, not one per pair. Its statement is the same one the pairwise product
        // makes, at the tripled scale.
        Case(
            id: "closed-unit.triple-product-one-rounding",
            run: () => Laws.SweptClaim(
                claim: Subjects.ClosedUnitTripleProductExact,
                domain: ClosedUnit,
                lawId: "closed-unit.triple-product-one-rounding",
                tier: Tier.Default,
                width: 2
            )
        ),

    ];
    private static LawCase[] UnitFractionCases() => [
        // ---- UnitFraction16/UnitFraction32 carriers: the half-open unit fractions ----
        Case(
            id: "unit-fraction16.mul-vs-oracle",
            run: () => Laws.ScalarBinaryMatchesOracle(
                domain: UnitFraction16Domain,
                lawId: "unit-fraction16.mul-vs-oracle",
                oracle: Subjects.UnitFraction16MultiplyOracle,
                subject: Subjects.UnitFraction16Multiply,
                tier: Tier.Default
            )
        ),
        Case(
            id: "unit-fraction16.div-vs-oracle",
            run: () => Laws.ScalarBinaryMatchesOracle(
                domain: UnitFraction16Domain,
                lawId: "unit-fraction16.div-vs-oracle",
                oracle: Subjects.UnitFraction16DivideOracle,
                subject: Subjects.UnitFraction16Divide,
                tier: Tier.Default
            )
        ),
        Case(
            id: "unit-fraction16.exact-ops-and-order",
            run: () => Laws.SweptClaim(
                claim: Subjects.UnitFraction16ExactOpsAndOrder,
                domain: UnitFraction16Domain,
                lawId: "unit-fraction16.exact-ops-and-order",
                tier: Tier.Default,
                width: 1
            )
        ),
        Case(
            id: "unit-fraction16.shifts-vs-oracle",
            run: () => Laws.SweptClaim(
                claim: Subjects.UnitFraction16ShiftsMatchOracle,
                domain: UnitFraction16Domain,
                lawId: "unit-fraction16.shifts-vs-oracle",
                tier: Tier.Default,
                width: 1
            )
        ),
        Case(
            id: "unit-fraction16.construction-and-refusals",
            run: () => Laws.Claim(
                claim: Subjects.UnitFraction16ConstructionAndRefusals,
                lawId: "unit-fraction16.construction-and-refusals"
            )
        ),
        Case(
            id: "unit-fraction16.text-vs-oracle",
            run: () => Laws.SweptClaim(
                claim: Subjects.UnitFraction16TextMatchesOracle,
                domain: UnitFraction16Domain,
                lawId: "unit-fraction16.text-vs-oracle",
                tier: Tier.Default,
                width: 1
            )
        ),
        Case(
            id: "unit-fraction16.parse-ladder",
            run: () => Laws.Claim(
                claim: Subjects.UnitFraction16ParseLadderHolds,
                lawId: "unit-fraction16.parse-ladder"
            )
        ),
        Case(
            id: "unit-fraction16.double-projection-exact",
            run: () => Laws.SweptClaim(
                claim: Subjects.UnitFraction16DoubleProjectionExact,
                domain: UnitFraction16Domain,
                lawId: "unit-fraction16.double-projection-exact",
                tier: Tier.Default,
                width: 1
            )
        ),
        Case(
            id: "unit-fraction32.mul-vs-oracle",
            run: () => Laws.ScalarBinaryMatchesOracle(
                domain: UnitFraction32Domain,
                lawId: "unit-fraction32.mul-vs-oracle",
                oracle: Subjects.UnitFraction32MultiplyOracle,
                subject: Subjects.UnitFraction32Multiply,
                tier: Tier.Default
            )
        ),
        Case(
            id: "unit-fraction32.div-vs-oracle",
            run: () => Laws.ScalarBinaryMatchesOracle(
                domain: UnitFraction32Domain,
                lawId: "unit-fraction32.div-vs-oracle",
                oracle: Subjects.UnitFraction32DivideOracle,
                subject: Subjects.UnitFraction32Divide,
                tier: Tier.Default
            )
        ),
        Case(
            id: "unit-fraction32.exact-ops-and-order",
            run: () => Laws.SweptClaim(
                claim: Subjects.UnitFraction32ExactOpsAndOrder,
                domain: UnitFraction32Domain,
                lawId: "unit-fraction32.exact-ops-and-order",
                tier: Tier.Default,
                width: 1
            )
        ),
        Case(
            id: "unit-fraction32.shifts-vs-oracle",
            run: () => Laws.SweptClaim(
                claim: Subjects.UnitFraction32ShiftsMatchOracle,
                domain: UnitFraction32Domain,
                lawId: "unit-fraction32.shifts-vs-oracle",
                tier: Tier.Default,
                width: 1
            )
        ),
        Case(
            id: "unit-fraction32.construction-and-refusals",
            run: () => Laws.Claim(
                claim: Subjects.UnitFraction32ConstructionAndRefusals,
                lawId: "unit-fraction32.construction-and-refusals"
            )
        ),
        Case(
            id: "unit-fraction32.text-vs-oracle",
            run: () => Laws.SweptClaim(
                claim: Subjects.UnitFraction32TextMatchesOracle,
                domain: UnitFraction32Domain,
                lawId: "unit-fraction32.text-vs-oracle",
                tier: Tier.Default,
                width: 1
            )
        ),
        Case(
            id: "unit-fraction32.parse-ladder",
            run: () => Laws.Claim(
                claim: Subjects.UnitFraction32ParseLadderHolds,
                lawId: "unit-fraction32.parse-ladder"
            )
        ),
        Case(
            id: "unit-fraction32.double-projection-exact",
            run: () => Laws.SweptClaim(
                claim: Subjects.UnitFraction32DoubleProjectionExact,
                domain: UnitFraction32Domain,
                lawId: "unit-fraction32.double-projection-exact",
                tier: Tier.Default,
                width: 1
            )
        ),
        // The seam the closed interval's remarks describe in prose, stated from the FRACTION side. closed-unit.kinship-exact
        // already pins the embedding and its refusal at one from the interval side; this case does not restate them.
        Case(
            id: "unit-fraction32.kinship-exact",
            run: () => Laws.SweptClaim(
                claim: Subjects.UnitFraction32KinshipExact,
                domain: UnitFraction32Domain,
                lawId: "unit-fraction32.kinship-exact",
                tier: Tier.Default,
                width: 1
            )
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
        Case(
            id: "complex.additive-group-exact",
            run: () => Laws.SweptClaim(
                claim: Subjects.ComplexAdditiveGroupExact,
                domain: Complex,
                lawId: "complex.additive-group-exact",
                tier: Tier.Default,
                width: 2
            )
        ),
        Case(
            id: "complex.presentation-seam",
            run: () => Laws.SweptClaim(
                claim: Subjects.ComplexPresentationSeam,
                domain: Complex,
                lawId: "complex.presentation-seam",
                tier: Tier.Default,
                width: 2
            )
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
        Case(
            id: "complex.div-refusal-and-unit",
            run: () => Laws.Claim(
                claim: Subjects.ComplexDivRefusalAndUnit,
                lawId: "complex.div-refusal-and-unit"
            )
        ),
        Case(
            id: "complex.magnitude-vs-oracle",
            run: () => Laws.SweptClaim(
                claim: Subjects.ComplexMagnitudeExact,
                domain: ComplexDirection,
                lawId: "complex.magnitude-vs-oracle",
                tier: Tier.Default,
                width: 2
            )
        ),
        Case(
            id: "complex.normalize-unit-direction",
            run: () => Laws.SweptClaim(
                claim: Subjects.ComplexNormalizeUnitDirection,
                domain: ComplexDirection,
                lawId: "complex.normalize-unit-direction",
                tier: Tier.Default,
                width: 2
            )
        ),
        Case(
            id: "complex.from-to-direction",
            run: () => Laws.SweptClaim(
                claim: Subjects.ComplexFromToDirection,
                domain: ComplexDirection,
                lawId: "complex.from-to-direction",
                tier: Tier.Default,
                width: 2
            )
        ),
        Case(
            id: "complex.angle-seam",
            run: () => Laws.Claim(
                claim: Subjects.ComplexAngleSeam,
                lawId: "complex.angle-seam"
            )
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
        Case(
            id: "split.additive-group-exact",
            run: () => Laws.SweptClaim(
                claim: Subjects.SplitAdditiveGroupExact,
                domain: Split,
                lawId: "split.additive-group-exact",
                tier: Tier.Default,
                width: 2
            )
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
        Case(
            id: "split.unit-and-division",
            run: () => Laws.SweptClaim(
                claim: Subjects.SplitUnitAndDivision,
                domain: Split,
                lawId: "split.unit-and-division",
                tier: Tier.Default,
                width: 2
            )
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
        Case(
            id: "split.rapidity-ladder",
            run: () => Laws.Claim(
                claim: Subjects.SplitRapidityLadderClaim,
                lawId: "split.rapidity-ladder"
            )
        ),

    ];
    private static LawCase[] DualRestCases() => [
        // ---- FixedDual: the rest of the dual construction, INCLUDING the two kernels the covered member id hides ----
        Case(
            id: "dual.additive-group-exact",
            run: () => Laws.SweptClaim(
                claim: Subjects.DualAdditiveGroupExact,
                domain: Dual,
                lawId: "dual.additive-group-exact",
                tier: Tier.Default,
                width: 2
            )
        ),
        Case(
            id: "dual.seeds-and-identities",
            run: () => Laws.SweptClaim(
                claim: Subjects.DualSeedsAndIdentities,
                domain: Dual,
                lawId: "dual.seeds-and-identities",
                tier: Tier.Default,
                width: 2
            )
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
        Case(
            id: "dual.divide-refusals",
            run: () => Laws.Claim(
                claim: Subjects.DualDivideRefusals,
                lawId: "dual.divide-refusals"
            )
        ),
        Case(
            id: "dual.transcendental-lifts",
            run: () => Laws.SweptClaim(
                claim: Subjects.DualTranscendentalLifts,
                domain: Dual,
                lawId: "dual.transcendental-lifts",
                tier: Tier.Default,
                width: 2
            )
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
        Case(
            id: "quaternion.dot-vs-oracle",
            run: () => Laws.SweptClaim(
                claim: Subjects.QuaternionDotExact,
                domain: Quaternion,
                lawId: "quaternion.dot-vs-oracle",
                tier: Tier.Default,
                width: 4
            )
        ),
        Case(
            id: "quaternion.scale-vs-oracle",
            run: () => Laws.SweptClaim(
                claim: Subjects.QuaternionScaleExact,
                domain: Quaternion,
                lawId: "quaternion.scale-vs-oracle",
                tier: Tier.Default,
                width: 4
            )
        ),
        Case(
            id: "quaternion.additive-group-exact",
            run: () => Laws.SweptClaim(
                claim: Subjects.QuaternionAdditiveGroupExact,
                domain: Quaternion,
                lawId: "quaternion.additive-group-exact",
                tier: Tier.Default,
                width: 4
            )
        ),
        Case(
            id: "quaternion.conjugate-antiautomorphism",
            run: () => Laws.SweptClaim(
                claim: Subjects.QuaternionConjugateAntiautomorphism,
                domain: QuaternionSublattice,
                lawId: "quaternion.conjugate-antiautomorphism",
                tier: Tier.Default,
                width: 4
            )
        ),
        Case(
            id: "quaternion.norm-vs-oracle",
            run: () => Laws.SweptClaim(
                claim: Subjects.QuaternionNormExact,
                domain: QuaternionDirection,
                lawId: "quaternion.norm-vs-oracle",
                tier: Tier.Default,
                width: 4
            )
        ),
        Case(
            id: "quaternion.inverse-vs-oracle",
            run: () => Laws.SweptClaim(
                claim: Subjects.QuaternionInverseExact,
                domain: QuaternionDirection,
                lawId: "quaternion.inverse-vs-oracle",
                tier: Tier.Default,
                width: 4
            )
        ),
        Case(
            id: "quaternion.normalize-unit-direction",
            run: () => Laws.SweptClaim(
                claim: Subjects.QuaternionNormalizeUnitDirection,
                domain: QuaternionDirection,
                lawId: "quaternion.normalize-unit-direction",
                tier: Tier.Default,
                width: 4
            )
        ),
        Case(
            id: "quaternion.rotate-vs-oracle",
            run: () => Laws.SweptClaim(
                claim: Subjects.QuaternionRotateExact,
                domain: QuaternionRotate,
                lawId: "quaternion.rotate-vs-oracle",
                tier: Tier.Default,
                width: 4
            )
        ),
        Case(
            id: "quaternion.axis-angle-ladder",
            run: () => Laws.Claim(
                claim: Subjects.QuaternionAxisAngleLadderClaim,
                lawId: "quaternion.axis-angle-ladder"
            )
        ),
        // The inbound seam. Judged against the SAME ladder as vector.adoption-ladder, deliberately: the two doors
        // must agree, and sharing the table is what would catch them drifting apart. Its second leg states what a
        // three-lane ladder cannot — that the seam does not renormalize.
        Case(
            id: "quaternion.adoption-ladder",
            run: () => Laws.Claim(
                claim: Subjects.QuaternionAdoptionMatchesLadder,
                lawId: "quaternion.adoption-ladder"
            )
        ),
        Case(
            id: "quaternion.exp-log-seam",
            run: () => Laws.Claim(
                claim: Subjects.QuaternionExpLogSeam,
                lawId: "quaternion.exp-log-seam"
            )
        ),

        // SinCosRaw was gated by nothing until these two cases: the case above says so in its own leg text. It is
        // internal, so the coverage manifest cannot name it and the hole was invisible to the ratchet. The reference
        // is Oracles.EncloseSinCos carried past the signed carrier by the
        // angle-addition identity, with the envelope derived from |c - 2^64/2pi| <= 1/2 rather than fitted to what the
        // subject happens to do. Proved by masking the top angle bit — the exact defect the member exists to avoid —
        // which reddens these two and NOTHING else in the tier.
        Case(
            id: "quaternion.sincos-raw-full-unsigned-width",
            run: () => Laws.Claim(
                claim: TransformKernelClaims.SinCosRawFullUnsignedWidthSurface,
                lawId: "quaternion.sincos-raw-full-unsigned-width"
            )
        ),
        Case(
            id: "quaternion.sincos-raw-width-sweep",
            run: () => Laws.Claim(
                claim: TransformKernelClaims.SinCosRawWidthSweepSurface,
                lawId: "quaternion.sincos-raw-width-sweep"
            )
        ),
        Case(
            id: "quaternion.from-to-shortest-arc",
            run: () => Laws.SweptClaim(
                claim: Subjects.QuaternionFromToShortestArc,
                domain: QuaternionDirection,
                lawId: "quaternion.from-to-shortest-arc",
                tier: Tier.Default,
                width: 4
            )
        ),
        Case(
            id: "quaternion.slerp-endpoints-and-arc",
            run: () => Laws.Claim(
                claim: Subjects.QuaternionSlerpEndpointsAndArc,
                lawId: "quaternion.slerp-endpoints-and-arc"
            )
        ),

        // The renderer's seam, and the one member of this type an algebra law cannot reach: the argument ORDER into
        // System.Numerics.Quaternion. Swapping X and W in ToQuaternion leaves every other case in this suite green,
        // which is exactly why the waiver that stood here — 'the algebra laws pin the exact raw contract instead' — was
        // false about the only thing this member decides on its own.
        Case(
            id: "quaternion.presentation-ladder",
            run: () => Laws.Claim(
                claim: Subjects.QuaternionPresentationMatchesLadder,
                lawId: "quaternion.presentation-ladder"
            )
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
