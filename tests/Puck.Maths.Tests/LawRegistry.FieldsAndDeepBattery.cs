namespace Puck.Maths.Tests;

internal static partial class LawRegistry {
    private static LawCase[] BinaryPolynomialRingCases() => [
        // ---- the GF(2)[t] ring beneath the binary fields ----
        // EVERYTHING in this family is EXACT. There is no rounding discipline anywhere in BinaryPolynomial, so the
        // substrate condition does not merely get discharged leg by leg — it never arises, and each leg below says so
        // in those words rather than reciting a ties-to-even story that does not apply. What IS live here is
        // reduction (the fold's direction and shift), the width and carry edges the packed carrier imposes, and the
        // refusal contracts.
        SweptCase(
            claim: Subjects.BinaryPolynomialAdditiveAndAccessors,
            domain: BinaryPolynomialRing,
            id: "polynomial.additive-group-and-accessors",
            width: 2
        ),
        Case(
            id: "polynomial.multiply-vs-carryless-oracle",
            run: () => {
                Laws.ScalarBinaryMatchesOracle(
                    domain: BinaryPolynomialRing,
                    lawId: "polynomial.multiply-vs-carryless-oracle",
                    oracle: Subjects.BinaryPolynomialMultiplyOracle,
                    subject: Subjects.BinaryPolynomialMultiply,
                    tier: Tier.Default
                );
                Laws.SweptClaim(
                    claim: Subjects.BinaryPolynomialCheckedMultiplyAndRingLaws,
                    domain: BinaryPolynomialRing,
                    lawId: "polynomial.multiply-vs-carryless-oracle",
                    tier: Tier.Default,
                    width: 2
                );
            }
        ),
        SweptCase(
            claim: Subjects.BinaryPolynomialDivRemVsOracle,
            domain: BinaryPolynomialDivision,
            id: "polynomial.divrem-vs-monomial-oracle",
            width: 2
        ),
        SweptCase(
            claim: Subjects.BinaryPolynomialGcdVsOracle,
            domain: BinaryPolynomialGcd,
            id: "polynomial.gcd-vs-binary-descent-oracle",
            width: 2
        ),
        SweptCase(
            claim: Subjects.BinaryPolynomialShiftsAreMonomialArithmetic,
            domain: BinaryPolynomialRing,
            id: "polynomial.shifts-are-monomial-arithmetic",
            width: 2
        ),
        ClaimCase(
            id: "polynomial.irreducible-census-and-trial-division",
            claim: () => Subjects.BinaryPolynomialIrreducibility(
                censusDegree: 12,
                trialDegree: 8
            )
        ),
        ClaimCase(
            id: "polynomial.primitive-order-and-census",
            claim: () => Subjects.BinaryPolynomialPrimitivity(censusDegree: 10)
        ),
        // IsIrreducible is cited here even though the body never calls it: FactorOddCycle decides every candidate with
        // it, so a wrong decision moves the factor list this case compares against the cyclotomic cosets — which the
        // campaign's mutation probe confirmed in both directions. Nothing else in the body is credited on that basis.
        ClaimCase(
            claim: Subjects.BinaryPolynomialFactorOddCycle,
            id: "polynomial.factor-odd-cycle-vs-cyclotomic-cosets"
        ),
        // DivRem is this family's own hot path: operator / and operator % are one call through to it
        // (BinaryPolynomial.cs:100-108), GreatestCommonDivisor reaches it through operator % (cs:220), and
        // FactorOddCycle's quotient loop calls it directly (cs:171). NOT IsIrreducible's delegate, which a previous
        // wording claimed: that route reaches BinaryFieldKernels.PolynomialRemainder, a self-contained shift-and-XOR
        // long division over the packed carrier (BinaryFieldKernels.cs:953-986), and touches DivRem nowhere.
        SweptCase(
            claim: Subjects.BinaryPolynomialDivRemVsOracle,
            domain: SmokeDomain,
            id: "smoke.polynomial-divrem-vs-monomial-oracle",
            width: 2
        ),

    ];
    private static LawCase[] BinaryFieldQuotientCases() => [
        // ---- the GF(2^k) quotients ----
        // EVERYTHING here is EXACT too. No rounding discipline exists anywhere in BinaryField<T>, so the substrate
        // condition drops out of every leg below and each says so in those words rather than reciting a ties-to-even
        // story that does not apply. What IS live is reduction (which modulus the fold actually applies, and in which
        // direction), representation (the leading term the tail form deliberately elides), the width and carry edges
        // five carriers impose, and the refusal contracts. Hardware-versus-fallback and rung-versus-scalar parity is
        // Post's binary-field stage and is deliberately NOT re-gated here: these laws exercise the mathematics through
        // the public surface.
        SweptCase(
            claim: Subjects.BinaryFieldProductAndReductionExact,
            domain: BinaryFieldDomain,
            id: "binary-field.product-and-reduction-vs-oracle",
            width: 3
        ),
        // Deliberately NOT citing BinaryFieldCatalog: the field axioms hold under ANY modulus with a non-zero constant
        // term, so this case could not catch a wrong catalog constant and must not claim to. BinaryFieldTail is
        // withheld for the same reason and was cited here in error: the tail IS the modulus, and a field running under
        // a legal modulus other than the one it was handed satisfies every line of this case — the campaign's probe did
        // exactly that and reddened five other binary-field cases while this one stayed green. ReductionTail answers to
        // binary-field.product-and-reduction-vs-oracle, which reads it back against the published pair.
        // BinaryFieldDegreeMember stays: Degree drives the operand fold, so it is load-bearing here.
        SweptCase(
            claim: Subjects.BinaryFieldAxiomsExact,
            domain: BinaryFieldAxioms,
            id: "binary-field.axioms-at-five-carriers",
            width: 3
        ),
        // Only the five catalog fields here: Inverse, Divide and SquareRoot's uniqueness all require an irreducible
        // modulus, and the catalog is the set this suite has an irreducibility statement for. A drawn modulus would be
        // reducible almost always and the statements would be meaningless.
        Case(
            id: "binary-field.multiplicative-group-vs-oracle",
            run: () => {
                Laws.SweptClaim(
                    claim: Subjects.BinaryFieldGroupExact,
                    domain: BinaryFieldGroup,
                    lawId: "binary-field.multiplicative-group-vs-oracle",
                    tier: Tier.Default,
                    width: 3
                );
                Laws.Claim(
                    claim: Subjects.BinaryFieldGroupRefusals,
                    lawId: "binary-field.multiplicative-group-vs-oracle"
                );
            }
        ),
        // A fixed claim rather than a swept one on purpose: a region statement is about LENGTH, ALIASING and the
        // vector rungs' tails, and the arithmetic each element carries is pinned element by element by the three cases
        // above. Sweeping the content would re-buy what those already own at a thousand times the cost.
        ClaimCase(
            claim: Subjects.BinaryFieldRegionsExact,
            id: "binary-field.regions-vs-oracle"
        ),
        ClaimCase(
            claim: Subjects.BinaryFieldConstructionAndRefusals,
            id: "binary-field.construction-and-refusals"
        ),
        ClaimCase(
            id: "binary-field.irreducibility-vs-trial-division",
            claim: () => Subjects.BinaryFieldIrreducibility(
                censusDegree: 8,
                trialDegree: 8
            )
        ),

        // The wide degrees the case above can only take on the catalog's word. It calls IsIrreducible() on all five
        // presets, but a `true` there is the subject reporting on itself; nothing independent says the degree-32, -64
        // and -128 moduli are irreducible. These two prove it — positives by an exact multiplicative-order certificate
        // in BigInteger, negatives by carryless construction — and the sweep is the same body at scale, which is why it
        // builds its whole basis inline rather than consuming a Domain.
        ClaimCase(
            claim: BinaryFieldWideDegreeClaims.WideDegreeIrreducibilityCertificatesSurface,
            id: "binary-field.wide-degree-irreducibility-certificates"
        ),
        ClaimCase(
            claim: BinaryFieldWideDegreeClaims.WideDegreeIrreducibilitySweepSurface,
            id: "binary-field.wide-degree-irreducibility-sweep"
        ),
        // The family's hottest kernel: every region rung's table and matrix, every inversion chain step, every
        // exponentiation and every square resolves to Multiply.
        Case(
            id: "smoke.binary-field-product-vs-oracle",
            run: () => {
                Laws.VectorMatchesOracle(
                    lawId: "smoke.binary-field-product-vs-oracle",
                    domain: SmokeDomain,
                    tier: Tier.Smoke,
                    width: 8,
                    subject: Subjects.BinaryFieldMultiply8,
                    oracle: Subjects.BinaryFieldProductOracle(
                        degree: 8,
                        reductionTail: 0x1BUL
                    )
                );
                Laws.VectorMatchesOracle(
                    lawId: "smoke.binary-field-product-vs-oracle",
                    domain: SmokeDomain,
                    tier: Tier.Smoke,
                    width: 16,
                    subject: Subjects.BinaryFieldMultiply16,
                    oracle: Subjects.BinaryFieldProductOracle(
                        degree: 16,
                        reductionTail: 0x2BUL
                    )
                );
            }
        ),

    ];
    private static LawCase[] PrimeFieldCases() => [
        // ---- the prime field ----
        // EVERYTHING here is EXACT too — nothing rounds, saturates or approximates anywhere in PrimeField64 — so the
        // substrate condition drops out of every leg below and each says so in those words rather than reciting a
        // ties-to-even story that does not apply. What IS live is reduction (which modulus fold actually applies),
        // representation (Montgomery form leaking into an answer), the width and carry edges the 2^62 ceiling imposes,
        // and the refusal contracts. The three probable-prime members were WAIVED until this campaign; the rulings that
        // struck those waivers are in the campaign notes, and their replacement statements are C9 through C12 below.
        // Nothing here re-points IsPrime at the composition, and every primality statement is measured against
        // Oracles.ExactPrimality — a BigInteger decision outside Puck.Maths entirely — so no tier of this family
        // becomes a tautology if IsPrime is ever re-pointed at it.
        ClaimCase(
            claim: Subjects.PrimeFieldCreateAndRefusals,
            id: "prime-field.create-and-refusals"
        ),
        SweptCase(
            claim: Subjects.PrimeFieldArithmeticExact,
            domain: PrimeFieldBand,
            id: "prime-field.arithmetic-vs-oracle",
            width: 1
        ),
        SweptCase(
            claim: Subjects.PrimeFieldPowMatchesModularPower,
            domain: PrimeFieldChain,
            id: "prime-field.pow-vs-modpow",
            width: 1
        ),
        ClaimCase(
            claim: Subjects.PrimeFieldInverseAndBatch,
            id: "prime-field.inverse-and-batch"
        ),
        SweptCase(
            claim: Subjects.PrimeFieldLegendreMatchesReciprocity,
            domain: PrimeFieldRoot,
            id: "prime-field.legendre-vs-reciprocity",
            width: 1
        ),
        SweptCase(
            claim: Subjects.PrimeFieldSquareRootExact,
            domain: PrimeFieldRoot,
            id: "prime-field.sqrt-descent-and-refusal",
            width: 1
        ),
        ClaimCase(
            claim: Subjects.PrimeFieldIsPrimeAgainstSieveAndWitnesses,
            id: "prime-field.is-prime-vs-sieve-and-witness-ladder"
        ),
        SweptCase(
            claim: Subjects.PrimeFieldIsPrimeMatchesWitnessOracle,
            domain: PrimeFieldPrimality,
            id: "prime-field.is-prime-vs-witness-oracle",
            width: 1
        ),

        // The exhaustive scale the two cases above only sample. The Baillie-PSW sweep visits every 32-bit value; it
        // runs in full because it was MEASURED at five to six minutes rather than assumed too expensive. Its oracle is
        // a segmented sieve of Eratosthenes written in the claims file — deliberately not a second Puck.Maths
        // primality kernel, which would let one shared defect green both sides.
        ClaimCase(
            claim: PrimalityScaleClaims.MontgomeryChainsSurface,
            id: "prime-field.montgomery-chains-exhaustive"
        ),
        ClaimCase(
            claim: PrimalityScaleClaims.BailliePswSurface,
            id: "prime-field.baillie-psw-exhaustive"
        ),
        SweptCase(
            claim: Subjects.PrimeFieldStrongRoundMatchesOracle,
            domain: PrimeFieldPrimality,
            id: "prime-field.strong-round-vs-oracle",
            width: 1
        ),
        SweptCase(
            claim: Subjects.PrimeFieldLucasMatchesCompanionMatrix,
            domain: PrimeFieldLucas,
            id: "prime-field.lucas-vs-companion-matrix",
            width: 1
        ),
        Case(
            id: "prime-field.baillie-composition",
            run: () => {
                Laws.SweptClaim(
                    claim: Subjects.PrimeFieldBaillieComposition,
                    domain: PrimeFieldPrimality,
                    lawId: "prime-field.baillie-composition",
                    tier: Tier.Default,
                    width: 1
                );
                Laws.Claim(
                    claim: Subjects.PrimeFieldBaillieCarriage,
                    lawId: "prime-field.baillie-composition"
                );
            }
        ),
        ClaimCase(
            claim: Subjects.PrimeFieldPseudoprimePopulations,
            id: "prime-field.pseudoprime-populations"
        ),

    ];
    private static LawCase[] ExtensionFieldCases() => [
        // ---- the quadratic extension field: F_p(sqrt(d)) as a pair over PrimeField64 ----
        Case(
            id: "extension-field.ring-vs-oracle",
            run: () => Laws.SweptClaim(
                lawId: "extension-field.ring-vs-oracle",
                domain: ExtensionField,
                tier: Tier.Default,
                width: 2,
                claim: Subjects.ExtensionRingExact(full: false)
            )
        ),
        Case(
            id: "extension-field.norm-trace-frobenius-vs-oracle",
            run: () => Laws.SweptClaim(
                lawId: "extension-field.norm-trace-frobenius-vs-oracle",
                domain: ExtensionFieldNorm,
                tier: Tier.Default,
                width: 2,
                claim: Subjects.ExtensionNormTraceFrobeniusExact(full: false)
            )
        ),
        SweptCase(
            claim: Subjects.ExtensionInverseExact,
            domain: ExtensionFieldInverse,
            id: "extension-field.inverse-vs-oracle",
            width: 2
        ),
        // Deliberately NOT citing ExtensionFrobenius, ExtensionNorm or ExtensionFromBase: legs 2 and 3 are ABOUT
        // conjugation and the norm, but they reach neither member. The claim body calls only Pow, Multiply, One, Zero
        // and the Element surface, and Pow is square-and-multiply over Multiply alone, so a wrong Frobenius, Norm or
        // FromBase cannot move this case — the campaign's probe broke all three at once and it stayed green. They
        // answer to extension-field.norm-trace-frobenius-vs-oracle, which reads them directly.
        SweptCase(
            claim: Subjects.ExtensionPowExact,
            domain: ExtensionFieldPower,
            id: "extension-field.pow-vs-oracle",
            width: 2
        ),
        ClaimCase(
            claim: Subjects.ExtensionBatchInverseExact,
            id: "extension-field.batch-inverse-vs-oracle"
        ),
        // ExtensionMultiply and ExtensionOne are cited because the field-invariant sweep below closes each inverse
        // through them — Multiply(element, Inverse(element)) == One over every accepted generator at five primes — so
        // this case would fail if either were wrong. Neither was cited before that sweep existed.
        ClaimCase(
            claim: Subjects.ExtensionConstructionAndRefusals,
            id: "extension-field.construction-and-refusals"
        ),
        Case(
            id: "extension-field.product-vs-oracle",
            run: () => Laws.BinaryMatchesOracle(
                lawId: "extension-field.product-vs-oracle",
                domain: ExtensionFieldProduct,
                tier: Tier.Default,
                subject: Subjects.ExtensionProduct(entry: 2),
                oracle: Subjects.ExtensionProductOracle(entry: 2)
            )
        ),

        // This family's hot path: Inverse, LegendreCharacter, TrySqrt's descent and all four primality entry points
        // reach their arithmetic through Pow's Montgomery chain, so a smoke run that never touched it would report
        // confidence it does not have.
        SweptCase(
            claim: Subjects.PrimeFieldPowMatchesModularPowerSmoke,
            domain: SmokeDomain,
            id: "smoke.prime-field-pow-vs-modpow",
            width: 1
        ),
        // The extension's hot path: Pow is a chain of the product, Inverse forms the norm out of it and BatchInverse is
        // a running product of it, so every other operation in the type bottoms out here.
        Case(
            id: "smoke.extension-field-product-vs-oracle",
            run: () => Laws.BinaryMatchesOracle(
                lawId: "smoke.extension-field-product-vs-oracle",
                domain: SmokeDomain,
                tier: Tier.Smoke,
                subject: Subjects.ExtensionProduct(entry: 2),
                oracle: Subjects.ExtensionProductOracle(entry: 2)
            )
        ),

    ];
    private static LawCase[] SamplingRefusalCases() => [
        // ---- sampling: the public refusal contracts of the wing's two constructing surfaces ----
        // The Sampling wing's expensive statistical evidence lives in Post's digital-net stage and in the Deep
        // distribution cases below. What the standard tier owes is the fast half: the contracts a caller reads off the
        // XML and acts on, which no statistical measurement is even shaped to state.
        ClaimCase(
            claim: Subjects.DigitalNetDirectionNumberRefusals,
            id: "sampling.direction-number-refusal-ladder"
        ),
        ClaimCase(
            claim: Subjects.AliasTableRefusalsAndFixedTwins,
            id: "sampling.alias-refusal-and-fixed-twins"
        ),
        // The cone table is the wing's one same-machine-replay type, and its contract was written about the double
        // pair it discards rather than the float pair it stores. This case states the surviving property.
        ClaimCase(
            claim: Subjects.ConeDirectionTableContract,
            id: "sampling.cone-table-stored-norm-and-uniqueness"
        ),
        ClaimCase(
            claim: Subjects.PcgReferenceVectorAndState,
            id: "sampling.pcg-reference-vector-and-state"
        ),
        ClaimCase(
            claim: Subjects.DigitalNetSampleAndShuffleIdentities,
            id: "sampling.digital-net-identities-and-net-property"
        ),
        ClaimCase(
            claim: Subjects.FieldNoiseBoundsAndTwins,
            id: "sampling.field-noise-bounds-and-gradient"
        ),
        ClaimCase(
            claim: Subjects.Pcg3dLatticeNoiseReferenceAndCorners,
            id: "sampling.pcg3d-lattice-noise-reference-and-corners"
        ),
        ClaimCase(
            claim: Subjects.NormalQuantileLadderAndRefusals,
            id: "sampling.normal-quantile-ladder"
        ),
        ClaimCase(
            claim: Subjects.LowDiscrepancyRecurrence,
            id: "sampling.low-discrepancy-recurrence"
        ),
        ClaimCase(
            claim: Subjects.SecureRandomContracts,
            id: "sampling.secure-random-intervals"
        ),

    ];
    private static LawCase[] DeepEdgeCrossCases() => [
        // ---- Deep: exhaustive edge cross batteries ----
        Case(
            id: "deep.presented-clifford-twin",
            run: () => Laws.VectorTwin(
                lawId: "deep.presented-clifford-twin",
                domain: Presented,
                tier: Tier.Deep,
                width: 8,
                first: Subjects.PresentedCliffordMultiply(
                    degenerateCount: 0,
                    negativeCount: 0,
                    positiveCount: 3
                ),
                second: Subjects.GeometricMultiply(
                    degenerateCount: 0,
                    negativeCount: 0,
                    positiveCount: 3
                ),
                witness: Subjects.CliffordProductOracle(
                    degenerateCount: 0,
                    negativeCount: 0,
                    positiveCount: 3
                )
            )
        ),
        ClaimCase(
            claim: OracleClaims.ComplementCliffordSignaturesDeep,
            id: "deep.presented-complement-all-signatures"
        ),
        Case(
            id: "deep.complex-mul-vs-oracle",
            run: () => Laws.BinaryMatchesOracle(
                lawId: "deep.complex-mul-vs-oracle",
                domain: Complex,
                tier: Tier.Deep,
                subject: Subjects.ComplexMultiply,
                oracle: Subjects.MultiplyOracle(
                    pRaw: 0L,
                    qRaw: ComplexQ
                )
            )
        ),
        Case(
            id: "deep.split-mul-vs-oracle",
            run: () => Laws.BinaryMatchesOracle(
                lawId: "deep.split-mul-vs-oracle",
                domain: Split,
                tier: Tier.Deep,
                subject: Subjects.SplitMultiply,
                oracle: Subjects.MultiplyOracle(
                    pRaw: 0L,
                    qRaw: SplitQ
                )
            )
        ),
        Case(
            id: "deep.quaternion-mul-vs-oracle",
            run: () => Laws.VectorMatchesOracle(
                domain: Quaternion,
                lawId: "deep.quaternion-mul-vs-oracle",
                oracle: Subjects.QuaternionMultiplyOracle,
                subject: Subjects.QuaternionMultiplyLanes,
                tier: Tier.Deep,
                width: 4
            )
        ),
        Case(
            id: "deep.dual-quaternion-mul-vs-oracle",
            run: () => Laws.VectorMatchesOracle(
                domain: DualQuaternion,
                lawId: "deep.dual-quaternion-mul-vs-oracle",
                oracle: Subjects.DualQuaternionMultiplyOracle,
                subject: Subjects.DualQuaternionMultiplyLanes,
                tier: Tier.Deep,
                width: 8
            )
        ),
        Case(
            id: "deep.complex-div-vs-oracle",
            run: () => Laws.BinaryMatchesOracle(
                domain: ComplexDivide,
                lawId: "deep.complex-div-vs-oracle",
                oracle: Subjects.ComplexDivideOracle,
                subject: Subjects.ComplexDivide,
                tier: Tier.Deep
            )
        ),
        Case(
            id: "deep.complex-rotate-vs-oracle",
            run: () => Laws.BinaryMatchesOracle(
                lawId: "deep.complex-rotate-vs-oracle",
                domain: ComplexRotate,
                tier: Tier.Deep,
                subject: Subjects.ComplexRotate,
                oracle: Subjects.MultiplyOracle(
                    pRaw: 0L,
                    qRaw: ComplexQ
                )
            )
        ),
        Case(
            id: "deep.split-div-vs-oracle",
            run: () => Laws.BinaryMatchesOracle(
                domain: SplitDivide,
                lawId: "deep.split-div-vs-oracle",
                oracle: Subjects.SplitDivideOracle,
                subject: Subjects.SplitDivide,
                tier: Tier.Deep
            )
        ),
        Case(
            id: "deep.split-transform-vs-oracle",
            run: () => Laws.BinaryMatchesOracle(
                lawId: "deep.split-transform-vs-oracle",
                domain: SplitTransform,
                tier: Tier.Deep,
                subject: Subjects.SplitTransform,
                oracle: Subjects.MultiplyOracle(
                    pRaw: 0L,
                    qRaw: SplitQ
                )
            )
        ),
        Case(
            id: "deep.fractional-mul-vs-oracle",
            run: () => Laws.BinaryMatchesOracle(
                lawId: "deep.fractional-mul-vs-oracle",
                domain: Fractional,
                tier: Tier.Deep,
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
        // The last admissible tangle width: all 377 diagrams and all 142,129 ordered pairs against the arc-tracing
        // oracle, which is the width the 512-normal-form cap makes the boundary of the construction.
        ClaimCase(
            claim: Subjects.TangleDeepSweep,
            id: "deep.presented-tangle-sweep"
        ),
        // The narrow width is FINITE, so at Deep the sampling comes off entirely: every raw the type can hold is rendered,
        // parsed back, projected and complemented, and multiplied and divided against a committed divisor band.
        ClaimCase(
            claim: Subjects.UnitFraction16Exhaustive,
            id: "deep.unit-fraction16-exhaustive"
        ),
        Case(
            id: "deep.unit-fraction32-mul-vs-oracle",
            run: () => Laws.ScalarBinaryMatchesOracle(
                domain: UnitFraction32Domain,
                lawId: "deep.unit-fraction32-mul-vs-oracle",
                oracle: UnitFractionClaims<UnitFraction32Width, UnitFraction32>.MultiplyOracle,
                subject: UnitFractionClaims<UnitFraction32Width, UnitFraction32>.Multiply,
                tier: Tier.Deep
            )
        ),
        // The one division kernel the family landed without a Deep mirror, and the mirror is stronger IN KIND rather
        // than only in sample count: the Default law's operand fold takes (min, max), so its quotient never exceeds one
        // and the Math.Min clamp fires at exactly one point — the edge square's diagonal, where the quotient is exactly
        // 2³² and nothing rounds. Everything above one rested on three rows of a hand ladder. This mirror drops the
        // ordering, so the ulong quotient grows toward 2⁶⁴ on live operands and the clamp becomes load-bearing.
        Case(
            id: "deep.unit-fraction32-div-vs-oracle",
            run: () => Laws.ScalarBinaryMatchesOracle(
                domain: UnitFraction32Domain,
                lawId: "deep.unit-fraction32-div-vs-oracle",
                oracle: UnitFractionClaims<UnitFraction32Width, UnitFraction32>.DivideUnorderedOracle,
                subject: UnitFractionClaims<UnitFraction32Width, UnitFraction32>.DivideUnordered,
                tier: Tier.Deep
            )
        ),
        SweptCase(
            claim: UnitFractionClaims<UnitFraction32Width, UnitFraction32>.TextMatchesOracle,
            domain: UnitFraction32Domain,
            id: "deep.unit-fraction32-text-vs-oracle",
            width: 1
        ),
        Case(
            id: "deep.fixed-divide-vs-oracle",
            run: () => Laws.ScalarBinaryMatchesOracle(
                domain: ScalarDivision,
                lawId: "deep.fixed-divide-vs-oracle",
                oracle: Subjects.FixedDivideOracle,
                subject: Subjects.FixedDivide,
                tier: Tier.Deep
            )
        ),
        SweptCase(
            claim: Subjects.FixedTranscendentalDeepSweep,
            domain: ScalarTranscendental,
            id: "deep.fixed-transcendental-envelope",
            width: 1
        ),
        SweptCase(
            claim: Subjects.FixedTextRoundTrip,
            domain: ScalarText,
            id: "deep.fixed-text-round-trip",
            width: 1
        ),
        Case(
            id: "deep.unsigned-scalar-div-vs-oracle",
            run: () => Laws.ScalarBinaryMatchesOracle(
                domain: UnsignedScalar,
                lawId: "deep.unsigned-scalar-div-vs-oracle",
                oracle: Subjects.UnsignedFixedDivideOracle,
                subject: Subjects.UnsignedFixedDivide,
                tier: Tier.Deep
            )
        ),
        // The sampling comes off entirely here: every branch of the five integer maps and the three integrality
        // classifiers is decided by the sixteen-bit fraction word and the parity of the integer part, so sweeping ALL
        // 2¹⁶ fraction words at seven integer parts is exhaustive over the branch space rather than merely wider.
        ClaimCase(
            claim: Subjects.UnsignedFractionSweep,
            id: "deep.unsigned-scalar-fraction-sweep"
        ),
        Case(
            id: "deep.vector-plane-products-vs-oracle",
            run: () => Laws.BinaryMatchesOracle(
                domain: Vector,
                lawId: "deep.vector-plane-products-vs-oracle",
                oracle: Subjects.PlaneProductsOracle,
                subject: Subjects.PlaneProducts,
                tier: Tier.Deep
            )
        ),
        Case(
            id: "deep.vector-narrow-lane-vs-oracle",
            run: () => Laws.BinaryMatchesOracle(
                domain: VectorNarrow,
                lawId: "deep.vector-narrow-lane-vs-oracle",
                oracle: Subjects.NarrowPlaneProductsOracle,
                subject: Subjects.NarrowPlaneProducts,
                tier: Tier.Deep
            )
        ),
        Case(
            id: "deep.vector-cross-vs-oracle",
            run: () => {
                Laws.VectorMatchesOracle(
                    domain: Vector,
                    lawId: "deep.vector-cross-vs-oracle",
                    oracle: Subjects.SpaceCrossLanesOracle,
                    subject: Subjects.SpaceCrossLanes,
                    tier: Tier.Deep,
                    width: 3
                );
                Laws.VectorMatchesOracle(
                    domain: Vector,
                    lawId: "deep.vector-cross-vs-oracle",
                    oracle: Subjects.SpaceDotLanesOracle,
                    subject: Subjects.SpaceDotLanes,
                    tier: Tier.Deep,
                    width: 3
                );
            }
        ),
        SweptCase(
            claim: Subjects.VectorNormalizeMatchesOracles,
            domain: VectorDirection,
            id: "deep.vector-normalize-vs-ideal-and-staged",
            width: 3
        ),
        Case(
            id: "deep.rigid-compose-vs-oracle",
            run: () => Laws.VectorMatchesOracle(
                domain: Rigid,
                lawId: "deep.rigid-compose-vs-oracle",
                oracle: Subjects.RigidComposeOracle,
                subject: Subjects.RigidComposeLanes,
                tier: Tier.Deep,
                width: 8
            )
        ),
        SweptCase(
            claim: Subjects.PositionDeltaExact,
            domain: PositionDelta,
            id: "deep.position-delta-vs-oracle",
            width: 6
        ),
        SweptCase(
            claim: Subjects.PositionTranslateExact,
            domain: PositionTranslate,
            id: "deep.position-translate-vs-oracle",
            width: 6
        ),
        SweptCase(
            claim: Subjects.RigidTransformPointExact,
            domain: RigidPoint,
            id: "deep.rigid-transform-point-vs-oracle",
            width: 8
        ),
        SweptCase(
            claim: Subjects.RateScheduleVsLedger,
            domain: Rate,
            id: "deep.rate-schedule-vs-ledger",
            width: 4
        ),
        // No Deep mirror stands for polynomial.additive-group-and-accessors or
        // polynomial.shifts-are-monomial-arithmetic, and the campaign says why rather than leaving the gap silent:
        // both are exact identities over operations linear in their operand (+, the two shifts) or over one accessor,
        // and every seam either carries is a FIXED LADDER — the identity constants, the shift-count ladder at 63/64,
        // the written forms — which a wider random batch does not touch. A mirror there would buy draw volume over
        // ground the Default battery already crosses, which the tier's own contract calls buying nothing.
        Case(
            id: "deep.polynomial-multiply-vs-carryless-oracle",
            run: () => {
                Laws.ScalarBinaryMatchesOracle(
                    domain: BinaryPolynomialRing,
                    lawId: "deep.polynomial-multiply-vs-carryless-oracle",
                    oracle: Subjects.BinaryPolynomialMultiplyOracle,
                    subject: Subjects.BinaryPolynomialMultiply,
                    tier: Tier.Deep
                );
                Laws.SweptClaim(
                    claim: Subjects.BinaryPolynomialCheckedMultiplyAndRingLaws,
                    domain: BinaryPolynomialRing,
                    lawId: "deep.polynomial-multiply-vs-carryless-oracle",
                    tier: Tier.Deep,
                    width: 2
                );
            }
        ),
        SweptCase(
            claim: Subjects.BinaryPolynomialDivRemVsOracle,
            domain: BinaryPolynomialDivision,
            id: "deep.polynomial-divrem-vs-monomial-oracle",
            width: 2
        ),
        SweptCase(
            claim: Subjects.BinaryPolynomialGcdVsOracle,
            domain: BinaryPolynomialGcd,
            id: "deep.polynomial-gcd-vs-binary-descent-oracle",
            width: 2
        ),
        ClaimCase(
            id: "deep.polynomial-irreducible-census-and-trial-division",
            claim: () => Subjects.BinaryPolynomialIrreducibility(
                censusDegree: 16,
                trialDegree: 12
            )
        ),
        Case(
            id: "deep.polynomial-primitive-order-and-census",
            run: () => {
                Laws.Claim(
                    lawId: "deep.polynomial-primitive-order-and-census",
                    claim: () => Subjects.BinaryPolynomialPrimitivity(censusDegree: 14)
                );
                Laws.Claim(
                    claim: Subjects.BinaryPolynomialPrimitiveSearch,
                    lawId: "deep.polynomial-primitive-order-and-census"
                );
            }
        ),
        ClaimCase(
            claim: Subjects.BinaryPolynomialFactorOddCycleExhaustive,
            id: "deep.polynomial-factor-odd-cycle-vs-cyclotomic-cosets"
        ),
        Case(
            id: "deep.binary-field-multiplicative-group",
            run: () => {
                Laws.SweptClaim(
                    lawId: "deep.binary-field-multiplicative-group",
                    domain: BinaryFieldGroup,
                    tier: Tier.Deep,
                    width: 3,
                    claim: (left, right) => Subjects.BinaryFieldGroupExact(
                        everyDegree: true,
                        left: left,
                        right: right
                    )
                );
                Laws.Claim(
                    claim: Subjects.BinaryFieldGroupRefusals,
                    lawId: "deep.binary-field-multiplicative-group"
                );
            }
        ),
        ClaimCase(
            id: "deep.binary-field-irreducible-census",
            claim: () => Subjects.BinaryFieldIrreducibility(
                censusDegree: 16,
                trialDegree: 12
            )
        ),
        ClaimCase(
            claim: Subjects.BinaryFieldDegree8Exhaustive,
            id: "deep.binary-field-degree8-exhaustive"
        ),
        // prime-field.create-and-refusals has NO Deep mirror by design: its statement is a fixed refusal ladder rather
        // than a sweep, and it already runs the full fifteen-rung modulus ladder at Default. A "stronger" version
        // would only be a longer list of the same shape.
        SweptCase(
            claim: Subjects.PrimeFieldArithmeticExactDeep,
            domain: PrimeFieldBand,
            id: "deep.prime-field-arithmetic-vs-oracle",
            width: 1
        ),
        SweptCase(
            claim: Subjects.PrimeFieldPowMatchesModularPowerDeep,
            domain: PrimeFieldChain,
            id: "deep.prime-field-pow-vs-modpow",
            width: 1
        ),
        ClaimCase(
            claim: Subjects.PrimeFieldInverseAndBatchDeep,
            id: "deep.prime-field-inverse-and-batch"
        ),
        // TWO statements in one Deep case, each leg naming the id it mirrors: the character and the square root sweep
        // the same operand stream at Default through their shared prime-field-root key, and mirroring them apart would
        // break that sharing.
        Case(
            id: "deep.prime-field-root-and-character",
            run: () => {
                Laws.SweptClaim(
                    claim: Subjects.PrimeFieldLegendreMatchesReciprocityDeep,
                    domain: PrimeFieldRoot,
                    lawId: "deep.prime-field-root-and-character",
                    tier: Tier.Deep,
                    width: 1
                );
                Laws.SweptClaim(
                    claim: Subjects.PrimeFieldSquareRootExactDeep,
                    domain: PrimeFieldRoot,
                    lawId: "deep.prime-field-root-and-character",
                    tier: Tier.Deep,
                    width: 1
                );
            }
        ),
        Case(
            id: "deep.prime-field-is-prime-exact",
            run: () => {
                Laws.Claim(
                    claim: Subjects.PrimeFieldIsPrimeAgainstSieveAndWitnessesDeep,
                    lawId: "deep.prime-field-is-prime-exact"
                );
                Laws.SweptClaim(
                    claim: Subjects.PrimeFieldIsPrimeMatchesWitnessOracle,
                    domain: PrimeFieldPrimality,
                    lawId: "deep.prime-field-is-prime-exact",
                    tier: Tier.Deep,
                    width: 1
                );
            }
        ),
        SweptCase(
            claim: Subjects.PrimeFieldStrongRoundMatchesOracle,
            domain: PrimeFieldPrimality,
            id: "deep.prime-field-strong-round-vs-oracle",
            width: 1
        ),
        SweptCase(
            claim: Subjects.PrimeFieldLucasMatchesCompanionMatrixDeep,
            domain: PrimeFieldLucas,
            id: "deep.prime-field-lucas-vs-companion-matrix",
            width: 1
        ),
        Case(
            id: "deep.prime-field-baillie-and-populations",
            run: () => {
                Laws.SweptClaim(
                    claim: Subjects.PrimeFieldBaillieComposition,
                    domain: PrimeFieldPrimality,
                    lawId: "deep.prime-field-baillie-and-populations",
                    tier: Tier.Deep,
                    width: 1
                );
                Laws.Claim(
                    claim: Subjects.PrimeFieldBaillieCarriage,
                    lawId: "deep.prime-field-baillie-and-populations"
                );
                Laws.Claim(
                    claim: Subjects.PrimeFieldPseudoprimePopulationsDeep,
                    lawId: "deep.prime-field-baillie-and-populations"
                );
            }
        ),
        Case(
            id: "deep.extension-field-product-vs-oracle",
            run: () => Laws.BinaryMatchesOracle(
                lawId: "deep.extension-field-product-vs-oracle",
                domain: ExtensionFieldProduct,
                tier: Tier.Deep,
                subject: Subjects.ExtensionProduct(entry: 2),
                oracle: Subjects.ExtensionProductOracle(entry: 2)
            )
        ),
        // TWO statements in one Deep case, each leg naming the id it mirrors: the ring statement and the norm/trace
        // statement are separate keys at Default and stay separate here, so each pair advances one counter together.
        Case(
            id: "deep.extension-field-ring-and-norm",
            run: () => {
                Laws.SweptClaim(
                    lawId: "deep.extension-field-ring-and-norm",
                    domain: ExtensionField,
                    tier: Tier.Deep,
                    width: 2,
                    claim: Subjects.ExtensionRingExact(full: true)
                );
                Laws.SweptClaim(
                    lawId: "deep.extension-field-ring-and-norm",
                    domain: ExtensionFieldNorm,
                    tier: Tier.Deep,
                    width: 2,
                    claim: Subjects.ExtensionNormTraceFrobeniusExact(full: true)
                );
            }
        ),
    ];
    private static LawCase[] PcgReferenceCases() => [
        // ---- The statistical, lattice and presented-kernel statements, grouped by the surface each interrogates ----
        // ---- Pcg32XshRr vs the reference implementation ----
        ClaimCase(
            claim: SamplingClaims.PcgTranscribedReferenceAndDecorrelationSurface,
            id: "sampling.pcg-transcribed-reference-and-decorrelation"
        ),
    ];
    private static LawCase[] Log2GaussianAliasCases() => [
        // ---- log2 / gaussian / alias table ----
        ClaimCase(
            claim: SamplingClaims.GaussianMomentsCdfTailSurface,
            id: "sampling.gaussian-moments-cdf-tail"
        ),
        ClaimCase(
            claim: SamplingClaims.ShuffleUniformitySurface,
            id: "sampling.shuffle-permutation-uniformity"
        ),
        ClaimCase(
            claim: SamplingClaims.AliasTableFrequencyDistributionSurface,
            id: "sampling.alias-table-frequency-distribution"
        ),

        // The full-volume counterparts of the three above, plus the measured star discrepancy nothing else in the tree
        // carries. Each gates at the TIGHTER of its published threshold and eight standard errors derived from the
        // sample count in the same run, so the volume buys real tightening rather than a restated bound. They sit at
        // Deep, not Exhaustive, because the four together measure
        // 2.5 seconds: a cheap case parked behind an opt-in tier loses its everyday coverage, and it would be the
        // WEAKER reduced-volume sibling left guarding Deep. Each shares its sibling's seed and multiplies its draw
        // count, so the sibling's samples are a prefix of these under strictly looser thresholds.
        ClaimCase(
            claim: SamplingDistributionClaims.GaussianMomentsCdfTailAtScaleSurface,
            id: "sampling.gaussian-moments-cdf-tail-at-scale"
        ),
        ClaimCase(
            claim: SamplingDistributionClaims.ShuffleUniformityAtScaleSurface,
            id: "sampling.shuffle-permutation-uniformity-at-scale"
        ),
        ClaimCase(
            claim: SamplingDistributionClaims.AliasTableFrequencyAtScaleSurface,
            id: "sampling.alias-table-frequency-at-scale"
        ),
        ClaimCase(
            claim: SamplingDistributionClaims.CertifiedLowDiscrepancyMeasuredAcrossScalesSurface,
            id: "sampling.certified-low-discrepancy-measured-across-scales"
        ),
    ];
    private static LawCase[] FieldNoiseCases() => [
        // ---- field noise + low discrepancy ----
        ClaimCase(
            claim: SamplingClaims.FieldNoisePeriodicityAndDistributionSurface,
            id: "sampling.field-noise-periodicity-canary-and-distribution"
        ),
    ];
    private static LawCase[] CertifiedLowDiscrepancyCases() => [
        // ---- CertifiedLowDiscrepancy ----
        ClaimCase(
            claim: SamplingClaims.CertifiedLowDiscrepancyBoundTeethAndGapSurface,
            id: "sampling.certified-low-discrepancy-bound-and-teeth"
        ),
    ];
    private static LawCase[] SymmetryLatticeCases() => [
        // ---- SymmetryLattice (the exact E8 root system CyclicRotation is the heartbeat of) ----
        // Deep rather than Exhaustive: 240 x 240 reflection pairs is milliseconds, not a full-carrier sweep. Exhaustive
        // is opt-in and rarely run, so parking a cheap case there would cost the statement its everyday coverage
        // without buying breadth.
        ClaimCase(
            claim: LatticeClaims.SymmetryLatticeExactStructureSurface,
            id: "integer.symmetry-lattice-exact-structure"
        ),
        // Default rather than Deep: twelve words over 240 nodes and one 240 x 240 pairing sweep is well under a second.
        ClaimCase(
            claim: LatticeClaims.SymmetryWordAndPairingSurface,
            id: "integer.symmetry-word-permutation-order-and-pairing"
        ),
    ];
    private static LawCase[] HilbertCurveCases() => [
        // ---- HilbertCurve (locality-preserving space-filling curve) ----
        // Deep rather than Exhaustive for the same reason: orders one through nine is a sub-second sweep.
        ClaimCase(
            claim: LatticeClaims.HilbertCurveExhaustiveBijectionSurface,
            id: "integer.hilbert-curve-bijection-and-locality"
        ),

        // The case above proves the curve is a bijection ON its domain; these two say what happens off it and who owns
        // the lattice's shared metadata. Both were previously unstated, and both failed silently rather than loudly.
        ClaimCase(
            claim: LatticeClaims.HilbertCurveRefusesOutsideItsDomain,
            id: "integer.hilbert-curve-refuses-outside-its-domain"
        ),
        ClaimCase(
            claim: LatticeClaims.RayCycleFactorsAreNotWritableByConsumers,
            id: "integer.ray-cycle-factors-are-not-writable-by-consumers"
        ),
        ClaimCase(
            claim: LatticeClaims.HilbertCurveHighOrderRoundTripSurface,
            id: "integer.hilbert-curve-high-order-round-trip"
        ),
    ];
    private static LawCase[] HexagonalCoordinateCases() => [
        // ---- HexagonalCoordinate (exact Eisenstein-integer hex grid) ----
        ClaimCase(
            claim: LatticeClaims.HexagonalCoordinateAlgebraicStructureSurface,
            id: "vector.hexagonal-coordinate-ring-and-rotation"
        ),
        ClaimCase(
            claim: LatticeClaims.HexagonalCoordinateLengthMatchesGraphDistanceSurface,
            id: "vector.hexagonal-coordinate-length-matches-graph-distance"
        ),
        ClaimCase(
            claim: LatticeClaims.HexagonalCoordinateRoundIsNearestCellSurface,
            id: "vector.hexagonal-coordinate-round-is-nearest-cell"
        ),
    ];
    private static LawCase[] ScalarSpecificationCases() => [
        // ---- scalar specification oracles ----
        ClaimCase(
            claim: ScalarFieldClaims.JacobiSymbolFixedWidthVsExactDescentSurface,
            id: "scalar.jacobi-symbol-fixed-width-vs-exact-descent"
        ),
        ClaimCase(
            claim: ScalarFieldClaims.BinaryIntegerWideCarrierSurface,
            id: "scalar.binary-integer-wide-carrier-vs-oracle"
        ),

        // The two conversion seams where the cross-machine promise was resting on the host rather than on code: a NaN
        // whose integer conversion the CLI does not specify, and a rendering that read the ambient culture. Both are
        // contract statements, so the suite now enforces the boundary the README draws instead of describing it.
        ClaimCase(
            claim: ScalarFieldClaims.ConversionSeamsDoNotDependOnTheHost,
            id: "scalar.conversion-seams-do-not-depend-on-the-host"
        ),
    ];
    private static LawCase[] BinaryFieldCrcCases() => [
        // ---- binary field: the suite's only CRC statement ----
        ClaimCase(
            claim: ScalarFieldClaims.BinaryPolynomialCrc32PublishedVectorSurface,
            id: "algebra.binary-polynomial-crc32-published-vector"
        ),
    ];
    private static LawCase[] MetallicQuasicrystalAccessCases() => [
        // ---- MetallicQuasicrystal random access ----
        ClaimCase(
            claim: QuasicrystalClaims.MetallicRandomAccessMatchesStreamedWord,
            id: "quasicrystal.metallic-random-access-vs-streamed-word"
        ),
    ];
    private static LawCase[] ModularTransformCases() => [
        // ---- ModularTransform + ContinuedFraction ----
        ClaimCase(
            claim: QuasicrystalClaims.ModularTransformClassesAndCuspAction,
            id: "quasicrystal.modular-transform-classes-and-cusp-action"
        ),
        ClaimCase(
            claim: QuasicrystalClaims.GaussReductionEntersFundamentalDomain,
            id: "quasicrystal.gauss-reduction-into-fundamental-domain"
        ),

        // The DECISION rather than the reduction, at the signed carrier's corners. The case above sweeps operands at or
        // below 24, where no width matters; the definiteness test needs 129 signed bits at the extremes and so both
        // admitted an indefinite form and refused a positive-definite one. Its oracle forms the discriminant in
        // BigInteger, sharing nothing with a production check that now compares unsigned magnitudes and never forms the
        // difference at all — which is the point, since the old oracle recomputed the subject's own expression.
        ClaimCase(
            claim: QuasicrystalClaims.GaussReductionDefinitenessAcrossTheCarrier,
            id: "quasicrystal.gauss-reduction-definiteness-across-the-carrier"
        ),
        ClaimCase(
            claim: QuasicrystalClaims.ContinuedFractionPeriodsAndFullWidthRegressions,
            id: "quasicrystal.continued-fraction-periods-and-full-width"
        ),
    ];
    private static LawCase[] QuadraticInflationCases() => [
        // ---- QuadraticInflation + MetallicQuasicrystal ----
        ClaimCase(
            claim: QuasicrystalClaims.QuadraticInflationInvariantsAndPolynomialTails,
            id: "quasicrystal.inflation-invariants-and-polynomial-tails"
        ),
        ClaimCase(
            claim: QuasicrystalClaims.MetallicReproducesGoldenSilverAndIsAFixedPoint,
            id: "quasicrystal.metallic-reproduces-golden-silver-fixed-point"
        ),
    ];
    private static LawCase[] QuadraticQuasicrystalCases() => [
        // ---- QuadraticQuasicrystal (the general chain) ----
        ClaimCase(
            claim: QuasicrystalClaims.GeneralQuasicrystalIsSturmianAndTileLengthConsistent,
            id: "quasicrystal.general-chain-is-sturmian-and-tile-length-consistent"
        ),
        ClaimCase(
            claim: QuasicrystalClaims.QuadraticQuasicrystalWidthAndPeriodRegressions,
            id: "quasicrystal.width-and-period-regressions"
        ),
    ];
    private static LawCase[] QuadraticQuasicrystalChainCases() => [
        // ---- QuadraticQuasicrystal.Chain random access ----
        ClaimCase(
            claim: QuasicrystalClaims.ChainSingleTermMatchesMetallicAndNewPeriodsWalk,
            id: "quasicrystal.chain-single-term-matches-metallic-and-new-periods"
        ),
    ];
    private static LawCase[] QuaternionDualStatCases() => [
        // ---- quaternion / dual ----
        // The three ladder cases below declare Leg.Structural rather than Leg.PublishedConstant. Their literals were
        // computed offline in double from the closed form, then cross-checked against the shipped kernel to confirm
        // the tolerance before being copied in — so the band was set by observing the subject. That is a regression
        // pin, not classical evidence, and condition (C) forbids calling it independent.
        ClaimCase(
            claim: GeometryClaims.QuaternionFromAxisAngleLadderSurface,
            id: "quaternion.from-axis-angle-ladder-transcription"
        ),
        ClaimCase(
            claim: GeometryClaims.QuaternionExpLogSurface,
            id: "quaternion.exp-log-ladder-transcription"
        ),
        ClaimCase(
            claim: GeometryClaims.QuaternionSlerpSurface,
            id: "quaternion.slerp-ladder-transcription"
        ),
        ClaimCase(
            claim: GeometryClaims.QuaternionAlgebraicSanitySurface,
            id: "quaternion.algebraic-sanity-and-fromto-poles"
        ),
        ClaimCase(
            claim: GeometryClaims.QuaternionFromToAlignmentSurface,
            id: "quaternion.from-to-full-width-alignment"
        ),
        ClaimCase(
            claim: GeometryClaims.DualDerivativeSurface,
            id: "dual.chain-rule-ladder-and-exact-spot-checks"
        ),
    ];
    private static LawCase[] Vector2WedgeDotCases() => [
        // ---- vector2 wedge/dot ----
        ClaimCase(
            claim: GeometryClaims.QuaternionHamiltonProductDotInverseSurface,
            id: "quaternion.hamilton-product-dot-inverse-full-width"
        ),
        ClaimCase(
            claim: GeometryClaims.QuaternionRotateScheduleTranscriptionSurface,
            id: "quaternion.rotate-schedule-transcription-full-width"
        ),
        ClaimCase(
            claim: GeometryClaims.Vector2FullWidthOracleAndIdentitiesSurface,
            id: "vector.plane-full-width-oracle-and-identities"
        ),
        ClaimCase(
            claim: GeometryClaims.Vector3DotCrossOracleSurface,
            id: "vector.space-full-width-oracle-and-length-policy"
        ),
    ];
    private static LawCase[] ComplexRigidStatCases() => [
        // ---- complex / rigid transform ----
        ClaimCase(
            claim: GeometryClaims.ComplexDivisionMultiplyFullWidthOracleSurface,
            id: "complex.division-multiply-full-width-oracle"
        ),
        ClaimCase(
            claim: GeometryClaims.ComplexFromToAndScaleSafetySurface,
            id: "complex.from-to-full-width-alignment-and-scale-safety"
        ),
        ClaimCase(
            claim: GeometryClaims.NormalizeFullWidthOracleSurface,
            id: "quaternion.normalize-full-width-oracle-and-four-square-carry"
        ),
        ClaimCase(
            claim: GeometryClaims.RigidTransformRoundTripSurface,
            id: "rigid.round-trip-ladder-self-consistency"
        ),
    ];
    private static LawCase[] PresentedAlgebraSurfaceCases() => [
        // ---- the presented algebra: kernels, modules, diagrams and the zeta seam ----
        ClaimCase(
            claim: PresentedKernelClaims.CliffordSignatureLadderMatchesGeometricAlgebra,
            id: "presented.clifford-signature-ladder-vs-geometric"
        ),
        ClaimCase(
            claim: PresentedKernelClaims.OctonionTwistCocycleCountMatchesDoublingAssociatorSupport,
            id: "presented.octonion-twist-cocycle-count"
        ),
        ClaimCase(
            claim: PresentedKernelClaims.WideBinaryFieldTwinsShippedKernel,
            id: "presented.binary-field-wide-degrees-twin"
        ),
        ClaimCase(
            claim: PresentedKernelClaims.SedenionPairSumZeroDivisorCount,
            id: "presented.sedenion-pair-zero-divisor-count"
        ),
        ClaimCase(
            claim: PresentedKernelClaims.PathAlgebraArgumentValidationRefusesByShape,
            id: "presented.path-algebra-argument-validation"
        ),
        ClaimCase(
            claim: PresentedModuleClaims.LiveAssociatorMatchesDoublingTower,
            id: "presented.live-associator-vs-doubling-tower"
        ),
        ClaimCase(
            claim: PresentedModuleClaims.SedenionQuadrupleBracketingsExhaustive,
            id: "presented.sedenion-quadruple-bracketing-exhaustive"
        ),
        ClaimCase(
            claim: PresentedDiagramClaims.BraidingCertificateSelfConsistentAtEightInstances,
            id: "presented.braiding-self-consistent-eight-instances"
        ),
        ClaimCase(
            claim: PresentedDiagramClaims.QuantumTorusChargeMatchesSkewPairing,
            id: "presented.quantum-torus-vs-skew-pairing"
        ),
        ClaimCase(
            claim: PresentedDiagramClaims.FunctorTwinsTransferAtVariedLength,
            id: "presented.functor-twins-transfer-varied-length"
        ),
        ClaimCase(
            claim: PresentedZetaClaims.UnitIntervalPowerOfTwoEnvelopeBoundary,
            id: "presented.unit-interval-power-of-two-envelope-boundary"
        ),
        ClaimCase(
            claim: PresentedZetaClaims.UnitIntervalFusedCompetingTermsVsOracle,
            id: "presented.unit-interval-fused-competing-terms-vs-oracle"
        ),
    ];
    private static LawCase[] StageSweepCases() => [
        // ---- the partitioner, digital-net, binary-field and fixed-point stage sweeps ----
        ClaimCase(
            claim: MonotonicPartitionerClaims.RoutingIsDeterministicMonotonicAndUniformSurface,
            id: "core.monotonic-partitioner-full-domain-sweep"
        ),
        ClaimCase(
            claim: MonotonicPartitionerClaims.MetricsMatchReferenceChainWalkSurface,
            id: "core.monotonic-partitioner-metrics-vs-reference-walk"
        ),
        ClaimCase(
            claim: MonotonicPartitionerClaims.GuidRoutesThroughTrailingEntropyProtocolSurface,
            id: "core.monotonic-partitioner-guid-protocol-pin"
        ),
        ClaimCase(
            claim: MonotonicPartitionerClaims.BucketCountOutOfRangeRefusesSurface,
            id: "core.monotonic-partitioner-bucket-count-refusals"
        ),
        ClaimCase(
            claim: WorldCoordClaims.BinaryIntegerSignedExtremesAndRefusalsSurface,
            id: "core.binary-integer-signed-extremes-and-refusals"
        ),
        ClaimCase(
            claim: DigitalNetClaims.NetPropertyThroughOrderFourteenSurface,
            id: "sampling.digital-net-property-through-order-fourteen"
        ),
        ClaimCase(
            claim: DigitalNetClaims.ShiftedAndShuffledBlocksAreNetsSurface,
            id: "sampling.digital-net-shifted-and-shuffled-blocks-are-nets"
        ),
        ClaimCase(
            claim: DigitalNetClaims.RadicalInverseFullRangeSurface,
            id: "sampling.digital-net-radical-inverse-full-range"
        ),
        ClaimCase(
            claim: DigitalNetClaims.ConeTableBuildPurityAndQuantizedCoverageSurface,
            id: "sampling.cone-table-build-purity-and-quantized-coverage"
        ),
        ClaimCase(
            claim: BinaryFieldRegionClaims.NarrowDegreeInverseSurface,
            id: "binary-field.narrow-degree-inverse-vs-oracle"
        ),
        ClaimCase(
            claim: BinaryFieldRegionClaims.NarrowDegreeRegionsSurface,
            id: "binary-field.narrow-degree-regions-vs-oracle"
        ),
        ClaimCase(
            claim: BinaryFieldRegionClaims.RegionTiersVsScalarRungSurface,
            id: "binary-field.region-tiers-vs-scalar-rung"
        ),
        ClaimCase(
            claim: BinaryFieldRegionClaims.WideRegionTiersVsScalarRungSurface,
            id: "binary-field.wide-region-tiers-vs-scalar-rung"
        ),
        ClaimCase(
            claim: BinaryFieldRegionClaims.RegionLengthsVsScalarRungSurface,
            id: "binary-field.region-lengths-vs-scalar-rung"
        ),
        ClaimCase(
            claim: ReedSolomonClaims.GeneratorRootsSurface,
            id: "reed-solomon.generator-roots-vs-oracle"
        ),
        ClaimCase(
            claim: ReedSolomonClaims.PublishedRemainderSurface,
            id: "reed-solomon.published-remainder"
        ),
        ClaimCase(
            claim: ReedSolomonClaims.CodewordSyndromesSurface,
            id: "reed-solomon.codeword-syndromes-vanish"
        ),
        ClaimCase(
            claim: ReedSolomonClaims.SurfaceRefusalsAndWideCarrierSurface,
            id: "reed-solomon.refusals-and-wide-carrier"
        ),
        ClaimCase(
            claim: MoveTowardAndEmitterClaims.MoveTowardSurface,
            id: "vector.move-toward-boundaries-and-segment"
        ),
        ClaimCase(
            claim: MoveTowardAndEmitterClaims.ScalarMoveTowardSurface,
            id: "scalar.move-toward-boundaries-and-segment"
        ),
        ClaimCase(
            claim: MoveTowardAndEmitterClaims.MoveTowardCarrierExtremesSurface,
            id: "vector.move-toward-carrier-extremes"
        ),
        ClaimCase(
            claim: MoveTowardAndEmitterClaims.ScalarMoveTowardCarrierExtremesSurface,
            id: "scalar.move-toward-carrier-extremes"
        ),
        ClaimCase(
            claim: MoveTowardAndEmitterClaims.RustPortEmitterSurface,
            id: "core.rust-port-emitters-are-pure-and-live"
        ),
        ClaimCase(
            claim: AngularFrequencyAndRationalClaims.AngularFrequencySurface,
            id: "algebra.angular-frequency-exact-and-vs-double"
        ),
        ClaimCase(
            claim: AngularFrequencyAndRationalClaims.RationalAlgebraSurface,
            id: "algebra.rational-field-axioms"
        ),
        ClaimCase(
            claim: FixedPointContractClaims.LayerSequenceWalkerAndBoundedHorizonSurface,
            id: "core.layer-sequence-walker-and-bounded-horizon"
        ),
        ClaimCase(
            claim: FixedPointContractClaims.BitwisePairSignedNarrowAndWideCarriersSurface,
            id: "core.bitwise-pair-signed-narrow-and-wide-carriers"
        ),
        ClaimCase(
            claim: FixedPointContractClaims.FieldNoiseWidePositionAliasAndRebaseSurface,
            id: "sampling.field-noise-wide-position-alias-and-rebase"
        ),
        ClaimCase(
            claim: FixedPointContractClaims.UnsignedSquareRootUInt128CarrierBoundarySurface,
            id: "core.unsigned-square-root-uint128-carrier-boundary"
        ),
        ClaimCase(
            claim: FixedPointContractClaims.FixedTickConversionRoundsUpAgainstRationalArithmetic,
            id: "core.fixed-tick-conversion-rounds-up-against-rational-arithmetic"
        ),
        ClaimCase(
            claim: FixedPointContractClaims.TryRoundRationalScalesTiesAndRefuses,
            id: "core.round-rational-scales-ties-and-refuses"
        ),
        ClaimCase(
            claim: FixedPointContractClaims.TryDurationEngineTicksExactAgainstDecimalBits,
            id: "core.fixed-tick-conversion-exact-refuses-inexact-decimals"
        ),
        ClaimCase(
            claim: Subjects.CyclicRotationPlaneCountIsCoxeterConjugacyPairCount,
            id: "scalar.cyclic-rotation-plane-count-matches-coxeter-conjugacy"
        ),
        ClaimCase(
            claim: FieldNoiseOracleClaims.FieldNoiseSampleMatchesExactOracle,
            id: "sampling.field-noise-sample-vs-exact-oracle"
        ),
    ];
    private static LawCase[] PresentedStructureCases() => [
        // ---- the presented structure surface and the quadratic-integer wing ----
        ClaimCase(
            claim: PresentedStructureClaims.ConformalCliffordCellsSurface,
            id: "presented.clifford-conformal-cells-vs-oracle"
        ),
        ClaimCase(
            claim: PresentedStructureClaims.SedenionBasisVsDoublingTowerSurface,
            id: "presented.sedenion-basis-vs-doubling-tower"
        ),
        ClaimCase(
            claim: PresentedStructureClaims.QuiverCountingStarVsWalkOracleSurface,
            id: "presented.quiver-counting-star-vs-walk-oracle"
        ),
        ClaimCase(
            claim: PresentedStructureClaims.DirichletDivisorCubeSurface,
            id: "presented.divisibility-cubed-divisor-count"
        ),
        ClaimCase(
            claim: PresentedStructureClaims.WeightedDualityEquivalenceSurface,
            id: "presented.duality-weighted-equivalence-vs-enumeration"
        ),
        ClaimCase(
            claim: PresentedStructureClaims.NonMetricComplementBeyondEuclideanSurface,
            id: "presented.complement-wedge-and-incidence-beyond-euclidean"
        ),
        ClaimCase(
            claim: PresentedStructureClaims.TransferFunctorLegacyCopiesSurface,
            id: "presented.transfer-functor-vs-legacy-copies"
        ),
        ClaimCase(
            claim: PresentedStructureClaims.MotorSandwichVsGeometricAlgebraSurface,
            id: "presented.motor-sandwich-vs-geometric-algebra"
        ),
        ClaimCase(
            claim: PresentedStructureClaims.ShuffleNearCapBasisSurface,
            id: "presented.shuffle-near-cap-basis"
        ),
        ClaimCase(
            claim: PresentedStructureClaims.HomologyTorusFreeRankTwoSurface,
            id: "presented.homology-torus-free-rank-two"
        ),
        ClaimCase(
            claim: QuadraticIntegerClaims.ClassNumberOneWorldsFactorSurface,
            id: "quadratic-integer.class-number-one-worlds-factor-prime-canonical"
        ),
        ClaimCase(
            claim: QuadraticIntegerClaims.GoldenUnitAndSplittingSurface,
            id: "quadratic-integer.golden-unit-and-splitting-vs-jacobi"
        ),
        ClaimCase(
            claim: QuadraticIntegerClaims.SumOfTwoSquaresAndWitnessSurface,
            id: "quadratic-integer.sum-of-two-squares-and-class-group-witness"
        ),
        ClaimCase(
            claim: QuadraticIntegerClaims.FactorizationDeterminismSurface,
            id: "quadratic-integer.factorization-is-deterministic"
        ),
        ClaimCase(
            claim: QuadraticIntegerClaims.FastTierRoutingSurface,
            id: "quadratic-integer.fast-tier-routing-vs-independent-reference"
        ),
        ClaimCase(
            claim: QuadraticIntegerClaims.RealOrderFundamentalUnitVsRetiredScanSurface,
            id: "quadratic-integer.real-order-fundamental-unit-vs-retired-scan"
        ),
        ClaimCase(
            claim: QuadraticIntegerClaims.LandmineAndDescriptorInvarianceSurface,
            id: "quadratic-integer.landmine-and-descriptor-invariance"
        ),
        ClaimCase(
            claim: QuadraticIntegerClaims.PellDelegationVsRetiredConvergentLoopSurface,
            id: "quadratic-integer.pell-delegation-vs-retired-convergent-loop"
        ),
        ClaimCase(
            claim: QuadraticIntegerClaims.AuditHangCompletesForcedSignSurface,
            id: "quadratic-integer.audit-hang-completes-forced-sign"
        ),
        ClaimCase(
            claim: QuadraticIntegerClaims.RealOrderPrimeNormExistenceVsRetiredOrbitBoxSurface,
            id: "quadratic-integer.real-order-prime-norm-existence-vs-retired-orbit-box"
        ),
        ClaimCase(
            claim: QuadraticIntegerClaims.RealOrderFactorizationBeyondOrbitBoxSurface,
            id: "quadratic-integer.real-order-factorization-beyond-orbit-box"
        ),
        ClaimCase(
            claim: DoublingTowerClaims.RealQuadraticTwinLaneSurface,
            id: "algebra.real-quadratic-twin-lane"
        ),
        ClaimCase(
            claim: DoublingTowerClaims.QuadraticTwinLinearOpsFullRangeSurface,
            id: "algebra.quadratic-twin-linear-ops-full-range"
        ),
        ClaimCase(
            claim: DoublingTowerClaims.DoublingFloor1MatchesFixedComplexSurface,
            id: "algebra.doubling-floor1-matches-fixed-complex"
        ),
        ClaimCase(
            claim: DoublingTowerClaims.DoublingFloor2MatchesFixedQuaternionSurface,
            id: "algebra.doubling-floor2-matches-fixed-quaternion"
        ),
        ClaimCase(
            claim: DoublingTowerClaims.DoublingFloor2CommutatorWitnessSurface,
            id: "algebra.doubling-floor2-commutator-witness"
        ),
        ClaimCase(
            claim: DoublingTowerClaims.DoublingFloor3OctonionNormVsOracleSurface,
            id: "algebra.doubling-floor3-octonion-norm-vs-oracle"
        ),
        Case(
            id: "presented.clifford-planar-complex-twin",
            run: () => Laws.TwinBinary(
                domain: CliffordPlanarComplex,
                first: GeometricAlgebraClaims.GeometricPlanarComplexSubject,
                lawId: "presented.clifford-planar-complex-twin",
                second: Subjects.ComplexMultiply,
                tier: Tier.Default,
                witness: GeometricAlgebraClaims.ComplexOracleWitness
            )
        ),
        Case(
            id: "presented.clifford-planar-split-twin",
            run: () => Laws.TwinBinary(
                domain: CliffordPlanarSplit,
                first: GeometricAlgebraClaims.GeometricPlanarSplitSubject,
                lawId: "presented.clifford-planar-split-twin",
                second: Subjects.SplitMultiply,
                tier: Tier.Default,
                witness: GeometricAlgebraClaims.SplitOracleWitness
            )
        ),
        Case(
            id: "presented.clifford-planar-dual-twin",
            run: () => Laws.TwinBinary(
                domain: CliffordPlanarDual,
                first: GeometricAlgebraClaims.GeometricPlanarDualSubject,
                lawId: "presented.clifford-planar-dual-twin",
                second: Subjects.DualMultiply,
                tier: Tier.Default,
                witness: GeometricAlgebraClaims.DualOracleWitness
            )
        ),
        Case(
            id: "presented.clifford-quaternion-even-twin",
            run: () => Laws.VectorTwin(
                domain: CliffordQuaternionEven,
                first: GeometricAlgebraClaims.GeometricQuaternionEvenFirst,
                lawId: "presented.clifford-quaternion-even-twin",
                second: GeometricAlgebraClaims.GeometricQuaternionEvenSecond,
                tier: Tier.Default,
                width: 4,
                witness: null
            )
        ),
        SweptCase(
            claim: GeometricAlgebraClaims.GeometricMotorRigidTransformSurface,
            domain: CliffordMotor,
            id: "presented.clifford-motor-rigid-transform-twin",
            width: 10
        ),
        SweptCase(
            claim: GeometricAlgebraClaims.GeometricReverseSurface,
            domain: CliffordReverse,
            id: "presented.clifford-reverse-anti-automorphism",
            width: 16
        ),
        SweptCase(
            claim: GeometricAlgebraClaims.GeometricMultivectorDecompositionSurface,
            domain: CliffordMultivector,
            id: "presented.clifford-multivector-decomposition",
            width: 16
        ),
        SweptCase(
            claim: GeometricAlgebraClaims.MonogenicExactSurface,
            domain: MonogenicExact,
            id: "algebra.monogenic-degree2-and-degree3-match-independent-reference",
            width: 8
        ),
        ClaimCase(
            claim: GeometricAlgebraClaims.MonogenicPlasticRatioSurface,
            id: "algebra.monogenic-plastic-ratio-recurrence"
        ),
        Case(
            id: "algebra.monogenic-fixed-fusion-diverges-from-reference",
            run: () => Laws.DivergenceCanary(
                domain: MonogenicFusion,
                fused: GeometricAlgebraClaims.MonogenicFusedMultiply,
                lawId: "algebra.monogenic-fixed-fusion-diverges-from-reference",
                minimumDivergences: 100,
                perProduct: GeometricAlgebraClaims.MonogenicPerProductMultiply,
                tier: Tier.Default,
                width: 3
            )
        ),

    ];
    private static LawCase[] MeetCases() => [
        // ---- meet: the attenuation carriers — the lawful core of the authority system's narrowing pipeline ----
        // Every case sweeps all three shipped carriers: MeetMask64, MeetQuantity64, and the product closed at
        // mask × quantity (the envelope shape the intended consumers pair). The identity/absorber/monotonicity cases
        // are the discriminating ones — union and maximum satisfy idempotence, commutativity and associativity too, so
        // only Top/Bottom/never-widens separate a meet from its dual. The authority DECISION is deliberately absent:
        // it is not a lattice (order-dependent exclusivity, rule-reporting verdicts, non-commuting grant transitions),
        // and only the envelope attenuation codified here is algebra.
        SweptCase(
            claim: MeetClaims.MeetIsAssociative,
            domain: MeetAssociative,
            id: "meet.associative",
            width: 3
        ),
        SweptCase(
            claim: MeetClaims.MeetNeverWidens,
            domain: MeetMonotonicity,
            id: "meet.attenuation-never-widens",
            width: 3
        ),
        SweptCase(
            claim: MeetClaims.BottomAbsorbs,
            domain: MeetBottomAbsorption,
            id: "meet.bottom-absorbing",
            width: 2
        ),
        SweptCase(
            claim: MeetClaims.MeetIsCommutative,
            domain: MeetCommutative,
            id: "meet.commutative",
            width: 2
        ),
        SweptCase(
            claim: MeetClaims.MeetIsIdempotent,
            domain: MeetIdempotent,
            id: "meet.idempotent",
            width: 2
        ),
        SweptCase(
            claim: MeetClaims.OrderAgreesWithMeet,
            domain: MeetOrderCoherence,
            id: "meet.order-agrees-with-meet",
            width: 2
        ),
        SweptCase(
            claim: MeetClaims.ProductComposesComponentwise,
            domain: MeetProductComposition,
            id: "meet.product-composes-componentwise",
            width: 3
        ),
        SweptCase(
            claim: MeetClaims.TopIsIdentity,
            domain: MeetTopIdentity,
            id: "meet.top-identity",
            width: 2
        ),

    ];
}
