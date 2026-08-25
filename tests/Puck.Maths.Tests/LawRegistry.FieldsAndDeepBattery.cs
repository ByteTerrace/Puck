namespace Puck.Maths.Tests;

internal static partial class LawRegistry {
    private static LawCase[] BinaryPolynomialRingCases() => [
        // ---- the GF(2)[t] ring beneath the binary fields ----
        // EVERYTHING in this family is EXACT. There is no rounding discipline anywhere in BinaryPolynomial, so the
        // substrate condition does not merely get discharged leg by leg — it never arises, and each leg below says so
        // in those words rather than reciting a ties-to-even story that does not apply. What IS live here is
        // reduction (the fold's direction and shift), the width and carry edges the packed carrier imposes, and the
        // refusal contracts.
        Case(
            id: "polynomial.additive-group-and-accessors",
            run: () => Laws.SweptClaim(
                claim: Subjects.BinaryPolynomialAdditiveAndAccessors,
                domain: BinaryPolynomialRing,
                lawId: "polynomial.additive-group-and-accessors",
                tier: Tier.Default,
                width: 2
            )
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
        Case(
            id: "polynomial.divrem-vs-monomial-oracle",
            run: () => Laws.SweptClaim(
                claim: Subjects.BinaryPolynomialDivRemVsOracle,
                domain: BinaryPolynomialDivision,
                lawId: "polynomial.divrem-vs-monomial-oracle",
                tier: Tier.Default,
                width: 2
            )
        ),
        Case(
            id: "polynomial.gcd-vs-binary-descent-oracle",
            run: () => Laws.SweptClaim(
                claim: Subjects.BinaryPolynomialGcdVsOracle,
                domain: BinaryPolynomialGcd,
                lawId: "polynomial.gcd-vs-binary-descent-oracle",
                tier: Tier.Default,
                width: 2
            )
        ),
        Case(
            id: "polynomial.shifts-are-monomial-arithmetic",
            run: () => Laws.SweptClaim(
                claim: Subjects.BinaryPolynomialShiftsAreMonomialArithmetic,
                domain: BinaryPolynomialRing,
                lawId: "polynomial.shifts-are-monomial-arithmetic",
                tier: Tier.Default,
                width: 2
            )
        ),
        Case(
            id: "polynomial.irreducible-census-and-trial-division",
            run: () => Laws.Claim(
                lawId: "polynomial.irreducible-census-and-trial-division",
                claim: () => Subjects.BinaryPolynomialIrreducibility(
                    censusDegree: 12,
                    trialDegree: 8
                )
            )
        ),
        Case(
            id: "polynomial.primitive-order-and-census",
            run: () => Laws.Claim(
                lawId: "polynomial.primitive-order-and-census",
                claim: () => Subjects.BinaryPolynomialPrimitivity(censusDegree: 10)
            )
        ),
        // IsIrreducible is cited here even though the body never calls it: FactorOddCycle decides every candidate with
        // it, so a wrong decision moves the factor list this case compares against the cyclotomic cosets — which the
        // campaign's mutation probe confirmed in both directions. Nothing else in the body is credited on that basis.
        Case(
            id: "polynomial.factor-odd-cycle-vs-cyclotomic-cosets",
            run: () => Laws.Claim(
                claim: Subjects.BinaryPolynomialFactorOddCycle,
                lawId: "polynomial.factor-odd-cycle-vs-cyclotomic-cosets"
            )
        ),
        // DivRem is this family's own hot path: operator / and operator % are one call through to it
        // (BinaryPolynomial.cs:100-108), GreatestCommonDivisor reaches it through operator % (cs:220), and
        // FactorOddCycle's quotient loop calls it directly (cs:171). NOT IsIrreducible's delegate, which a previous
        // wording claimed: that route reaches BinaryFieldKernels.PolynomialRemainder, a self-contained shift-and-XOR
        // long division over the packed carrier (BinaryFieldKernels.cs:953-986), and touches DivRem nowhere.
        Case(
            id: "smoke.polynomial-divrem-vs-monomial-oracle",
            run: () => Laws.SweptClaim(
                claim: Subjects.BinaryPolynomialDivRemVsOracle,
                domain: SmokeDomain,
                lawId: "smoke.polynomial-divrem-vs-monomial-oracle",
                tier: Tier.Smoke,
                width: 2
            )
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
        Case(
            id: "binary-field.product-and-reduction-vs-oracle",
            run: () => Laws.SweptClaim(
                claim: Subjects.BinaryFieldProductAndReductionExact,
                domain: BinaryFieldDomain,
                lawId: "binary-field.product-and-reduction-vs-oracle",
                tier: Tier.Default,
                width: 3
            )
        ),
        // Deliberately NOT citing BinaryFieldCatalog: the field axioms hold under ANY modulus with a non-zero constant
        // term, so this case could not catch a wrong catalog constant and must not claim to. BinaryFieldTail is
        // withheld for the same reason and was cited here in error: the tail IS the modulus, and a field running under
        // a legal modulus other than the one it was handed satisfies every line of this case — the campaign's probe did
        // exactly that and reddened five other binary-field cases while this one stayed green. ReductionTail answers to
        // binary-field.product-and-reduction-vs-oracle, which reads it back against the published pair.
        // BinaryFieldDegreeMember stays: Degree drives the operand fold, so it is load-bearing here.
        Case(
            id: "binary-field.axioms-at-five-carriers",
            run: () => Laws.SweptClaim(
                claim: Subjects.BinaryFieldAxiomsExact,
                domain: BinaryFieldAxioms,
                lawId: "binary-field.axioms-at-five-carriers",
                tier: Tier.Default,
                width: 3
            )
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
        Case(
            id: "binary-field.regions-vs-oracle",
            run: () => Laws.Claim(
                claim: Subjects.BinaryFieldRegionsExact,
                lawId: "binary-field.regions-vs-oracle"
            )
        ),
        Case(
            id: "binary-field.construction-and-refusals",
            run: () => Laws.Claim(
                claim: Subjects.BinaryFieldConstructionAndRefusals,
                lawId: "binary-field.construction-and-refusals"
            )
        ),
        Case(
            id: "binary-field.irreducibility-vs-trial-division",
            run: () => Laws.Claim(
                lawId: "binary-field.irreducibility-vs-trial-division",
                claim: () => Subjects.BinaryFieldIrreducibility(
                    censusDegree: 8,
                    trialDegree: 8
                )
            )
        ),

        // The wide degrees the case above can only take on the catalog's word. It calls IsIrreducible() on all five
        // presets, but a `true` there is the subject reporting on itself; nothing independent says the degree-32, -64
        // and -128 moduli are irreducible. These two prove it — positives by an exact multiplicative-order certificate
        // in BigInteger, negatives by carryless construction — and the sweep is the same body at scale, which is why it
        // builds its whole basis inline rather than consuming a Domain.
        Case(
            id: "binary-field.wide-degree-irreducibility-certificates",
            run: () => Laws.Claim(
                claim: BinaryFieldWideDegreeClaims.WideDegreeIrreducibilityCertificatesSurface,
                lawId: "binary-field.wide-degree-irreducibility-certificates"
            )
        ),
        Case(
            id: "binary-field.wide-degree-irreducibility-sweep",
            run: () => Laws.Claim(
                claim: BinaryFieldWideDegreeClaims.WideDegreeIrreducibilitySweepSurface,
                lawId: "binary-field.wide-degree-irreducibility-sweep"
            )
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
        Case(
            id: "prime-field.create-and-refusals",
            run: () => Laws.Claim(
                claim: Subjects.PrimeFieldCreateAndRefusals,
                lawId: "prime-field.create-and-refusals"
            )
        ),
        Case(
            id: "prime-field.arithmetic-vs-oracle",
            run: () => Laws.SweptClaim(
                claim: Subjects.PrimeFieldArithmeticExact,
                domain: PrimeFieldBand,
                lawId: "prime-field.arithmetic-vs-oracle",
                tier: Tier.Default,
                width: 1
            )
        ),
        Case(
            id: "prime-field.pow-vs-modpow",
            run: () => Laws.SweptClaim(
                claim: Subjects.PrimeFieldPowMatchesModularPower,
                domain: PrimeFieldChain,
                lawId: "prime-field.pow-vs-modpow",
                tier: Tier.Default,
                width: 1
            )
        ),
        Case(
            id: "prime-field.inverse-and-batch",
            run: () => Laws.Claim(
                claim: Subjects.PrimeFieldInverseAndBatch,
                lawId: "prime-field.inverse-and-batch"
            )
        ),
        Case(
            id: "prime-field.legendre-vs-reciprocity",
            run: () => Laws.SweptClaim(
                claim: Subjects.PrimeFieldLegendreMatchesReciprocity,
                domain: PrimeFieldRoot,
                lawId: "prime-field.legendre-vs-reciprocity",
                tier: Tier.Default,
                width: 1
            )
        ),
        Case(
            id: "prime-field.sqrt-descent-and-refusal",
            run: () => Laws.SweptClaim(
                claim: Subjects.PrimeFieldSquareRootExact,
                domain: PrimeFieldRoot,
                lawId: "prime-field.sqrt-descent-and-refusal",
                tier: Tier.Default,
                width: 1
            )
        ),
        Case(
            id: "prime-field.is-prime-vs-sieve-and-witness-ladder",
            run: () => Laws.Claim(
                claim: Subjects.PrimeFieldIsPrimeAgainstSieveAndWitnesses,
                lawId: "prime-field.is-prime-vs-sieve-and-witness-ladder"
            )
        ),
        Case(
            id: "prime-field.is-prime-vs-witness-oracle",
            run: () => Laws.SweptClaim(
                claim: Subjects.PrimeFieldIsPrimeMatchesWitnessOracle,
                domain: PrimeFieldPrimality,
                lawId: "prime-field.is-prime-vs-witness-oracle",
                tier: Tier.Default,
                width: 1
            )
        ),

        // The exhaustive scale the two cases above only sample. The Baillie-PSW sweep visits every 32-bit value; it
        // runs in full because it was MEASURED at five to six minutes rather than assumed too expensive. Its oracle is
        // a segmented sieve of Eratosthenes written in the claims file — deliberately not a second Puck.Maths
        // primality kernel, which would let one shared defect green both sides.
        Case(
            id: "prime-field.montgomery-chains-exhaustive",
            run: () => Laws.Claim(
                claim: PrimalityScaleClaims.MontgomeryChainsSurface,
                lawId: "prime-field.montgomery-chains-exhaustive"
            )
        ),
        Case(
            id: "prime-field.baillie-psw-exhaustive",
            run: () => Laws.Claim(
                claim: PrimalityScaleClaims.BailliePswSurface,
                lawId: "prime-field.baillie-psw-exhaustive"
            )
        ),
        Case(
            id: "prime-field.strong-round-vs-oracle",
            run: () => Laws.SweptClaim(
                claim: Subjects.PrimeFieldStrongRoundMatchesOracle,
                domain: PrimeFieldPrimality,
                lawId: "prime-field.strong-round-vs-oracle",
                tier: Tier.Default,
                width: 1
            )
        ),
        Case(
            id: "prime-field.lucas-vs-companion-matrix",
            run: () => Laws.SweptClaim(
                claim: Subjects.PrimeFieldLucasMatchesCompanionMatrix,
                domain: PrimeFieldLucas,
                lawId: "prime-field.lucas-vs-companion-matrix",
                tier: Tier.Default,
                width: 1
            )
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
        Case(
            id: "prime-field.pseudoprime-populations",
            run: () => Laws.Claim(
                claim: Subjects.PrimeFieldPseudoprimePopulations,
                lawId: "prime-field.pseudoprime-populations"
            )
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
        Case(
            id: "extension-field.inverse-vs-oracle",
            run: () => Laws.SweptClaim(
                claim: Subjects.ExtensionInverseExact,
                domain: ExtensionFieldInverse,
                lawId: "extension-field.inverse-vs-oracle",
                tier: Tier.Default,
                width: 2
            )
        ),
        // Deliberately NOT citing ExtensionFrobenius, ExtensionNorm or ExtensionFromBase: legs 2 and 3 are ABOUT
        // conjugation and the norm, but they reach neither member. The claim body calls only Pow, Multiply, One, Zero
        // and the Element surface, and Pow is square-and-multiply over Multiply alone, so a wrong Frobenius, Norm or
        // FromBase cannot move this case — the campaign's probe broke all three at once and it stayed green. They
        // answer to extension-field.norm-trace-frobenius-vs-oracle, which reads them directly.
        Case(
            id: "extension-field.pow-vs-oracle",
            run: () => Laws.SweptClaim(
                claim: Subjects.ExtensionPowExact,
                domain: ExtensionFieldPower,
                lawId: "extension-field.pow-vs-oracle",
                tier: Tier.Default,
                width: 2
            )
        ),
        Case(
            id: "extension-field.batch-inverse-vs-oracle",
            run: () => Laws.Claim(
                claim: Subjects.ExtensionBatchInverseExact,
                lawId: "extension-field.batch-inverse-vs-oracle"
            )
        ),
        // ExtensionMultiply and ExtensionOne are cited because the field-invariant sweep below closes each inverse
        // through them — Multiply(element, Inverse(element)) == One over every accepted generator at five primes — so
        // this case would fail if either were wrong. Neither was cited before that sweep existed.
        Case(
            id: "extension-field.construction-and-refusals",
            run: () => Laws.Claim(
                claim: Subjects.ExtensionConstructionAndRefusals,
                lawId: "extension-field.construction-and-refusals"
            )
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
        Case(
            id: "smoke.prime-field-pow-vs-modpow",
            run: () => Laws.SweptClaim(
                claim: Subjects.PrimeFieldPowMatchesModularPowerSmoke,
                domain: SmokeDomain,
                lawId: "smoke.prime-field-pow-vs-modpow",
                tier: Tier.Smoke,
                width: 1
            )
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
        Case(
            id: "sampling.direction-number-refusal-ladder",
            run: () => Laws.Claim(
                claim: Subjects.DigitalNetDirectionNumberRefusals,
                lawId: "sampling.direction-number-refusal-ladder"
            )
        ),
        Case(
            id: "sampling.alias-refusal-and-fixed-twins",
            run: () => Laws.Claim(
                claim: Subjects.AliasTableRefusalsAndFixedTwins,
                lawId: "sampling.alias-refusal-and-fixed-twins"
            )
        ),
        // The cone table is the wing's one same-machine-replay type, and its contract was written about the double
        // pair it discards rather than the float pair it stores. This case states the surviving property.
        Case(
            id: "sampling.cone-table-stored-norm-and-uniqueness",
            run: () => Laws.Claim(
                claim: Subjects.ConeDirectionTableContract,
                lawId: "sampling.cone-table-stored-norm-and-uniqueness"
            )
        ),
        Case(
            id: "sampling.pcg-reference-vector-and-state",
            run: () => Laws.Claim(
                claim: Subjects.PcgReferenceVectorAndState,
                lawId: "sampling.pcg-reference-vector-and-state"
            )
        ),
        Case(
            id: "sampling.digital-net-identities-and-net-property",
            run: () => Laws.Claim(
                claim: Subjects.DigitalNetSampleAndShuffleIdentities,
                lawId: "sampling.digital-net-identities-and-net-property"
            )
        ),
        Case(
            id: "sampling.field-noise-bounds-and-gradient",
            run: () => Laws.Claim(
                claim: Subjects.FieldNoiseBoundsAndTwins,
                lawId: "sampling.field-noise-bounds-and-gradient"
            )
        ),
        Case(
            id: "sampling.pcg3d-lattice-noise-reference-and-corners",
            run: () => Laws.Claim(
                claim: Subjects.Pcg3dLatticeNoiseReferenceAndCorners,
                lawId: "sampling.pcg3d-lattice-noise-reference-and-corners"
            )
        ),
        Case(
            id: "sampling.normal-quantile-ladder",
            run: () => Laws.Claim(
                claim: Subjects.NormalQuantileLadderAndRefusals,
                lawId: "sampling.normal-quantile-ladder"
            )
        ),
        Case(
            id: "sampling.low-discrepancy-recurrence",
            run: () => Laws.Claim(
                claim: Subjects.LowDiscrepancyRecurrence,
                lawId: "sampling.low-discrepancy-recurrence"
            )
        ),
        Case(
            id: "sampling.secure-random-intervals",
            run: () => Laws.Claim(
                claim: Subjects.SecureRandomContracts,
                lawId: "sampling.secure-random-intervals"
            )
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
        Case(
            id: "deep.presented-complement-all-signatures",
            run: () => Laws.Claim(
                claim: OracleClaims.ComplementCliffordSignaturesDeep,
                lawId: "deep.presented-complement-all-signatures"
            )
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
        Case(
            id: "deep.presented-tangle-sweep",
            run: () => Laws.Claim(
                claim: Subjects.TangleDeepSweep,
                lawId: "deep.presented-tangle-sweep"
            )
        ),
        // The narrow width is FINITE, so at Deep the sampling comes off entirely: every raw the type can hold is rendered,
        // parsed back, projected and complemented, and multiplied and divided against a committed divisor band.
        Case(
            id: "deep.unit-fraction16-exhaustive",
            run: () => Laws.Claim(
                claim: Subjects.UnitFraction16Exhaustive,
                lawId: "deep.unit-fraction16-exhaustive"
            )
        ),
        Case(
            id: "deep.unit-fraction32-mul-vs-oracle",
            run: () => Laws.ScalarBinaryMatchesOracle(
                domain: UnitFraction32Domain,
                lawId: "deep.unit-fraction32-mul-vs-oracle",
                oracle: Subjects.UnitFraction32MultiplyOracle,
                subject: Subjects.UnitFraction32Multiply,
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
                oracle: Subjects.UnitFraction32DivideUnorderedOracle,
                subject: Subjects.UnitFraction32DivideUnordered,
                tier: Tier.Deep
            )
        ),
        Case(
            id: "deep.unit-fraction32-text-vs-oracle",
            run: () => Laws.SweptClaim(
                claim: Subjects.UnitFraction32TextMatchesOracle,
                domain: UnitFraction32Domain,
                lawId: "deep.unit-fraction32-text-vs-oracle",
                tier: Tier.Deep,
                width: 1
            )
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
        Case(
            id: "deep.fixed-transcendental-envelope",
            run: () => Laws.SweptClaim(
                claim: Subjects.FixedTranscendentalDeepSweep,
                domain: ScalarTranscendental,
                lawId: "deep.fixed-transcendental-envelope",
                tier: Tier.Deep,
                width: 1
            )
        ),
        Case(
            id: "deep.fixed-text-round-trip",
            run: () => Laws.SweptClaim(
                claim: Subjects.FixedTextRoundTrip,
                domain: ScalarText,
                lawId: "deep.fixed-text-round-trip",
                tier: Tier.Deep,
                width: 1
            )
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
        Case(
            id: "deep.unsigned-scalar-fraction-sweep",
            run: () => Laws.Claim(
                claim: Subjects.UnsignedFractionSweep,
                lawId: "deep.unsigned-scalar-fraction-sweep"
            )
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
        Case(
            id: "deep.vector-normalize-vs-ideal-and-staged",
            run: () => Laws.SweptClaim(
                claim: Subjects.VectorNormalizeMatchesOracles,
                domain: VectorDirection,
                lawId: "deep.vector-normalize-vs-ideal-and-staged",
                tier: Tier.Deep,
                width: 3
            )
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
        Case(
            id: "deep.position-delta-vs-oracle",
            run: () => Laws.SweptClaim(
                claim: Subjects.PositionDeltaExact,
                domain: PositionDelta,
                lawId: "deep.position-delta-vs-oracle",
                tier: Tier.Deep,
                width: 6
            )
        ),
        Case(
            id: "deep.position-translate-vs-oracle",
            run: () => Laws.SweptClaim(
                claim: Subjects.PositionTranslateExact,
                domain: PositionTranslate,
                lawId: "deep.position-translate-vs-oracle",
                tier: Tier.Deep,
                width: 6
            )
        ),
        Case(
            id: "deep.rigid-transform-point-vs-oracle",
            run: () => Laws.SweptClaim(
                claim: Subjects.RigidTransformPointExact,
                domain: RigidPoint,
                lawId: "deep.rigid-transform-point-vs-oracle",
                tier: Tier.Deep,
                width: 8
            )
        ),
        Case(
            id: "deep.rate-schedule-vs-ledger",
            run: () => Laws.SweptClaim(
                claim: Subjects.RateScheduleVsLedger,
                domain: Rate,
                lawId: "deep.rate-schedule-vs-ledger",
                tier: Tier.Deep,
                width: 4
            )
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
        Case(
            id: "deep.polynomial-divrem-vs-monomial-oracle",
            run: () => Laws.SweptClaim(
                claim: Subjects.BinaryPolynomialDivRemVsOracle,
                domain: BinaryPolynomialDivision,
                lawId: "deep.polynomial-divrem-vs-monomial-oracle",
                tier: Tier.Deep,
                width: 2
            )
        ),
        Case(
            id: "deep.polynomial-gcd-vs-binary-descent-oracle",
            run: () => Laws.SweptClaim(
                claim: Subjects.BinaryPolynomialGcdVsOracle,
                domain: BinaryPolynomialGcd,
                lawId: "deep.polynomial-gcd-vs-binary-descent-oracle",
                tier: Tier.Deep,
                width: 2
            )
        ),
        Case(
            id: "deep.polynomial-irreducible-census-and-trial-division",
            run: () => Laws.Claim(
                lawId: "deep.polynomial-irreducible-census-and-trial-division",
                claim: () => Subjects.BinaryPolynomialIrreducibility(
                    censusDegree: 16,
                    trialDegree: 12
                )
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
        Case(
            id: "deep.polynomial-factor-odd-cycle-vs-cyclotomic-cosets",
            run: () => Laws.Claim(
                claim: Subjects.BinaryPolynomialFactorOddCycleExhaustive,
                lawId: "deep.polynomial-factor-odd-cycle-vs-cyclotomic-cosets"
            )
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
        Case(
            id: "deep.binary-field-irreducible-census",
            run: () => Laws.Claim(
                lawId: "deep.binary-field-irreducible-census",
                claim: () => Subjects.BinaryFieldIrreducibility(
                    censusDegree: 16,
                    trialDegree: 12
                )
            )
        ),
        Case(
            id: "deep.binary-field-degree8-exhaustive",
            run: () => Laws.Claim(
                claim: Subjects.BinaryFieldDegree8Exhaustive,
                lawId: "deep.binary-field-degree8-exhaustive"
            )
        ),
        // prime-field.create-and-refusals has NO Deep mirror by design: its statement is a fixed refusal ladder rather
        // than a sweep, and it already runs the full fifteen-rung modulus ladder at Default. A "stronger" version
        // would only be a longer list of the same shape.
        Case(
            id: "deep.prime-field-arithmetic-vs-oracle",
            run: () => Laws.SweptClaim(
                claim: Subjects.PrimeFieldArithmeticExactDeep,
                domain: PrimeFieldBand,
                lawId: "deep.prime-field-arithmetic-vs-oracle",
                tier: Tier.Deep,
                width: 1
            )
        ),
        Case(
            id: "deep.prime-field-pow-vs-modpow",
            run: () => Laws.SweptClaim(
                claim: Subjects.PrimeFieldPowMatchesModularPowerDeep,
                domain: PrimeFieldChain,
                lawId: "deep.prime-field-pow-vs-modpow",
                tier: Tier.Deep,
                width: 1
            )
        ),
        Case(
            id: "deep.prime-field-inverse-and-batch",
            run: () => Laws.Claim(
                claim: Subjects.PrimeFieldInverseAndBatchDeep,
                lawId: "deep.prime-field-inverse-and-batch"
            )
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
        Case(
            id: "deep.prime-field-strong-round-vs-oracle",
            run: () => Laws.SweptClaim(
                claim: Subjects.PrimeFieldStrongRoundMatchesOracle,
                domain: PrimeFieldPrimality,
                lawId: "deep.prime-field-strong-round-vs-oracle",
                tier: Tier.Deep,
                width: 1
            )
        ),
        Case(
            id: "deep.prime-field-lucas-vs-companion-matrix",
            run: () => Laws.SweptClaim(
                claim: Subjects.PrimeFieldLucasMatchesCompanionMatrixDeep,
                domain: PrimeFieldLucas,
                lawId: "deep.prime-field-lucas-vs-companion-matrix",
                tier: Tier.Deep,
                width: 1
            )
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
        Case(
            id: "sampling.pcg-transcribed-reference-and-decorrelation",
            run: () => Laws.Claim(
                claim: SamplingClaims.PcgTranscribedReferenceAndDecorrelationSurface,
                lawId: "sampling.pcg-transcribed-reference-and-decorrelation"
            )
        ),
    ];
    private static LawCase[] Log2GaussianAliasCases() => [
        // ---- log2 / gaussian / alias table ----
        Case(
            id: "sampling.gaussian-moments-cdf-tail",
            run: () => Laws.Claim(
                claim: SamplingClaims.GaussianMomentsCdfTailSurface,
                lawId: "sampling.gaussian-moments-cdf-tail"
            )
        ),
        Case(
            id: "sampling.shuffle-permutation-uniformity",
            run: () => Laws.Claim(
                claim: SamplingClaims.ShuffleUniformitySurface,
                lawId: "sampling.shuffle-permutation-uniformity"
            )
        ),
        Case(
            id: "sampling.alias-table-frequency-distribution",
            run: () => Laws.Claim(
                claim: SamplingClaims.AliasTableFrequencyDistributionSurface,
                lawId: "sampling.alias-table-frequency-distribution"
            )
        ),

        // The full-volume counterparts of the three above, plus the measured star discrepancy nothing else in the tree
        // carries. Each gates at the TIGHTER of its published threshold and eight standard errors derived from the
        // sample count in the same run, so the volume buys real tightening rather than a restated bound. They sit at
        // Deep, not Exhaustive, because the four together measure
        // 2.5 seconds: a cheap case parked behind an opt-in tier loses its everyday coverage, and it would be the
        // WEAKER reduced-volume sibling left guarding Deep. Each shares its sibling's seed and multiplies its draw
        // count, so the sibling's samples are a prefix of these under strictly looser thresholds.
        Case(
            id: "sampling.gaussian-moments-cdf-tail-at-scale",
            run: () => Laws.Claim(
                claim: SamplingDistributionClaims.GaussianMomentsCdfTailAtScaleSurface,
                lawId: "sampling.gaussian-moments-cdf-tail-at-scale"
            )
        ),
        Case(
            id: "sampling.shuffle-permutation-uniformity-at-scale",
            run: () => Laws.Claim(
                claim: SamplingDistributionClaims.ShuffleUniformityAtScaleSurface,
                lawId: "sampling.shuffle-permutation-uniformity-at-scale"
            )
        ),
        Case(
            id: "sampling.alias-table-frequency-at-scale",
            run: () => Laws.Claim(
                claim: SamplingDistributionClaims.AliasTableFrequencyAtScaleSurface,
                lawId: "sampling.alias-table-frequency-at-scale"
            )
        ),
        Case(
            id: "sampling.certified-low-discrepancy-measured-across-scales",
            run: () => Laws.Claim(
                claim: SamplingDistributionClaims.CertifiedLowDiscrepancyMeasuredAcrossScalesSurface,
                lawId: "sampling.certified-low-discrepancy-measured-across-scales"
            )
        ),
    ];
    private static LawCase[] FieldNoiseCases() => [
        // ---- field noise + low discrepancy ----
        Case(
            id: "sampling.field-noise-periodicity-canary-and-distribution",
            run: () => Laws.Claim(
                claim: SamplingClaims.FieldNoisePeriodicityAndDistributionSurface,
                lawId: "sampling.field-noise-periodicity-canary-and-distribution"
            )
        ),
    ];
    private static LawCase[] CertifiedLowDiscrepancyCases() => [
        // ---- CertifiedLowDiscrepancy ----
        Case(
            id: "sampling.certified-low-discrepancy-bound-and-teeth",
            run: () => Laws.Claim(
                claim: SamplingClaims.CertifiedLowDiscrepancyBoundTeethAndGapSurface,
                lawId: "sampling.certified-low-discrepancy-bound-and-teeth"
            )
        ),
    ];
    private static LawCase[] SymmetryLatticeCases() => [
        // ---- SymmetryLattice (the exact E8 root system CyclicRotation is the heartbeat of) ----
        // Deep rather than Exhaustive: 240 x 240 reflection pairs is milliseconds, not a full-carrier sweep. Exhaustive
        // is opt-in and rarely run, so parking a cheap case there would cost the statement its everyday coverage
        // without buying breadth.
        Case(
            id: "integer.symmetry-lattice-exact-structure",
            run: () => Laws.Claim(
                claim: LatticeClaims.SymmetryLatticeExactStructureSurface,
                lawId: "integer.symmetry-lattice-exact-structure"
            )
        ),
    ];
    private static LawCase[] HilbertCurveCases() => [
        // ---- HilbertCurve (locality-preserving space-filling curve) ----
        // Deep rather than Exhaustive for the same reason: orders one through nine is a sub-second sweep.
        Case(
            id: "integer.hilbert-curve-bijection-and-locality",
            run: () => Laws.Claim(
                claim: LatticeClaims.HilbertCurveExhaustiveBijectionSurface,
                lawId: "integer.hilbert-curve-bijection-and-locality"
            )
        ),

        // The case above proves the curve is a bijection ON its domain; these two say what happens off it and who owns
        // the lattice's shared metadata. Both were previously unstated, and both failed silently rather than loudly.
        Case(
            id: "integer.hilbert-curve-refuses-outside-its-domain",
            run: () => Laws.Claim(
                claim: LatticeClaims.HilbertCurveRefusesOutsideItsDomain,
                lawId: "integer.hilbert-curve-refuses-outside-its-domain"
            )
        ),
        Case(
            id: "integer.ray-cycle-factors-are-not-writable-by-consumers",
            run: () => Laws.Claim(
                claim: LatticeClaims.RayCycleFactorsAreNotWritableByConsumers,
                lawId: "integer.ray-cycle-factors-are-not-writable-by-consumers"
            )
        ),
        Case(
            id: "integer.hilbert-curve-high-order-round-trip",
            run: () => Laws.Claim(
                claim: LatticeClaims.HilbertCurveHighOrderRoundTripSurface,
                lawId: "integer.hilbert-curve-high-order-round-trip"
            )
        ),
    ];
    private static LawCase[] HexagonalCoordinateCases() => [
        // ---- HexagonalCoordinate (exact Eisenstein-integer hex grid) ----
        Case(
            id: "vector.hexagonal-coordinate-ring-and-rotation",
            run: () => Laws.Claim(
                claim: LatticeClaims.HexagonalCoordinateAlgebraicStructureSurface,
                lawId: "vector.hexagonal-coordinate-ring-and-rotation"
            )
        ),
        Case(
            id: "vector.hexagonal-coordinate-length-matches-graph-distance",
            run: () => Laws.Claim(
                claim: LatticeClaims.HexagonalCoordinateLengthMatchesGraphDistanceSurface,
                lawId: "vector.hexagonal-coordinate-length-matches-graph-distance"
            )
        ),
        Case(
            id: "vector.hexagonal-coordinate-round-is-nearest-cell",
            run: () => Laws.Claim(
                claim: LatticeClaims.HexagonalCoordinateRoundIsNearestCellSurface,
                lawId: "vector.hexagonal-coordinate-round-is-nearest-cell"
            )
        ),
    ];
    private static LawCase[] ScalarSpecificationCases() => [
        // ---- scalar specification oracles ----
        Case(
            id: "scalar.jacobi-symbol-fixed-width-vs-exact-descent",
            run: () => Laws.Claim(
                claim: ScalarFieldClaims.JacobiSymbolFixedWidthVsExactDescentSurface,
                lawId: "scalar.jacobi-symbol-fixed-width-vs-exact-descent"
            )
        ),
        Case(
            id: "scalar.binary-integer-wide-carrier-vs-oracle",
            run: () => Laws.Claim(
                claim: ScalarFieldClaims.BinaryIntegerWideCarrierSurface,
                lawId: "scalar.binary-integer-wide-carrier-vs-oracle"
            )
        ),

        // The two conversion seams where the cross-machine promise was resting on the host rather than on code: a NaN
        // whose integer conversion the CLI does not specify, and a rendering that read the ambient culture. Both are
        // contract statements, so the suite now enforces the boundary the README draws instead of describing it.
        Case(
            id: "scalar.conversion-seams-do-not-depend-on-the-host",
            run: () => Laws.Claim(
                claim: ScalarFieldClaims.ConversionSeamsDoNotDependOnTheHost,
                lawId: "scalar.conversion-seams-do-not-depend-on-the-host"
            )
        ),
    ];
    private static LawCase[] BinaryFieldCrcCases() => [
        // ---- binary field: the suite's only CRC statement ----
        Case(
            id: "algebra.binary-polynomial-crc32-published-vector",
            run: () => Laws.Claim(
                claim: ScalarFieldClaims.BinaryPolynomialCrc32PublishedVectorSurface,
                lawId: "algebra.binary-polynomial-crc32-published-vector"
            )
        ),
    ];
    private static LawCase[] MetallicQuasicrystalAccessCases() => [
        // ---- MetallicQuasicrystal random access ----
        Case(
            id: "quasicrystal.metallic-random-access-vs-streamed-word",
            run: () => Laws.Claim(
                claim: QuasicrystalClaims.MetallicRandomAccessMatchesStreamedWord,
                lawId: "quasicrystal.metallic-random-access-vs-streamed-word"
            )
        ),
    ];
    private static LawCase[] ModularTransformCases() => [
        // ---- ModularTransform + ContinuedFraction ----
        Case(
            id: "quasicrystal.modular-transform-classes-and-cusp-action",
            run: () => Laws.Claim(
                claim: QuasicrystalClaims.ModularTransformClassesAndCuspAction,
                lawId: "quasicrystal.modular-transform-classes-and-cusp-action"
            )
        ),
        Case(
            id: "quasicrystal.gauss-reduction-into-fundamental-domain",
            run: () => Laws.Claim(
                claim: QuasicrystalClaims.GaussReductionEntersFundamentalDomain,
                lawId: "quasicrystal.gauss-reduction-into-fundamental-domain"
            )
        ),

        // The DECISION rather than the reduction, at the signed carrier's corners. The case above sweeps operands at or
        // below 24, where no width matters; the definiteness test needs 129 signed bits at the extremes and so both
        // admitted an indefinite form and refused a positive-definite one. Its oracle forms the discriminant in
        // BigInteger, sharing nothing with a production check that now compares unsigned magnitudes and never forms the
        // difference at all — which is the point, since the old oracle recomputed the subject's own expression.
        Case(
            id: "quasicrystal.gauss-reduction-definiteness-across-the-carrier",
            run: () => Laws.Claim(
                claim: QuasicrystalClaims.GaussReductionDefinitenessAcrossTheCarrier,
                lawId: "quasicrystal.gauss-reduction-definiteness-across-the-carrier"
            )
        ),
        Case(
            id: "quasicrystal.continued-fraction-periods-and-full-width",
            run: () => Laws.Claim(
                claim: QuasicrystalClaims.ContinuedFractionPeriodsAndFullWidthRegressions,
                lawId: "quasicrystal.continued-fraction-periods-and-full-width"
            )
        ),
    ];
    private static LawCase[] QuadraticInflationCases() => [
        // ---- QuadraticInflation + MetallicQuasicrystal ----
        Case(
            id: "quasicrystal.inflation-invariants-and-polynomial-tails",
            run: () => Laws.Claim(
                claim: QuasicrystalClaims.QuadraticInflationInvariantsAndPolynomialTails,
                lawId: "quasicrystal.inflation-invariants-and-polynomial-tails"
            )
        ),
        Case(
            id: "quasicrystal.metallic-reproduces-golden-silver-fixed-point",
            run: () => Laws.Claim(
                claim: QuasicrystalClaims.MetallicReproducesGoldenSilverAndIsAFixedPoint,
                lawId: "quasicrystal.metallic-reproduces-golden-silver-fixed-point"
            )
        ),
    ];
    private static LawCase[] QuadraticQuasicrystalCases() => [
        // ---- QuadraticQuasicrystal (the general chain) ----
        Case(
            id: "quasicrystal.general-chain-is-sturmian-and-tile-length-consistent",
            run: () => Laws.Claim(
                claim: QuasicrystalClaims.GeneralQuasicrystalIsSturmianAndTileLengthConsistent,
                lawId: "quasicrystal.general-chain-is-sturmian-and-tile-length-consistent"
            )
        ),
        Case(
            id: "quasicrystal.width-and-period-regressions",
            run: () => Laws.Claim(
                claim: QuasicrystalClaims.QuadraticQuasicrystalWidthAndPeriodRegressions,
                lawId: "quasicrystal.width-and-period-regressions"
            )
        ),
    ];
    private static LawCase[] QuadraticQuasicrystalChainCases() => [
        // ---- QuadraticQuasicrystal.Chain random access ----
        Case(
            id: "quasicrystal.chain-single-term-matches-metallic-and-new-periods",
            run: () => Laws.Claim(
                claim: QuasicrystalClaims.ChainSingleTermMatchesMetallicAndNewPeriodsWalk,
                lawId: "quasicrystal.chain-single-term-matches-metallic-and-new-periods"
            )
        ),
    ];
    private static LawCase[] QuaternionDualStatCases() => [
        // ---- quaternion / dual ----
        // The three ladder cases below declare Leg.Structural rather than Leg.PublishedConstant. Their literals were
        // computed offline in double from the closed form, then cross-checked against the shipped kernel to confirm
        // the tolerance before being copied in — so the band was set by observing the subject. That is a regression
        // pin, not classical evidence, and condition (C) forbids calling it independent.
        Case(
            id: "quaternion.from-axis-angle-ladder-transcription",
            run: () => Laws.Claim(
                claim: GeometryClaims.QuaternionFromAxisAngleLadderSurface,
                lawId: "quaternion.from-axis-angle-ladder-transcription"
            )
        ),
        Case(
            id: "quaternion.exp-log-ladder-transcription",
            run: () => Laws.Claim(
                claim: GeometryClaims.QuaternionExpLogSurface,
                lawId: "quaternion.exp-log-ladder-transcription"
            )
        ),
        Case(
            id: "quaternion.slerp-ladder-transcription",
            run: () => Laws.Claim(
                claim: GeometryClaims.QuaternionSlerpSurface,
                lawId: "quaternion.slerp-ladder-transcription"
            )
        ),
        Case(
            id: "quaternion.algebraic-sanity-and-fromto-poles",
            run: () => Laws.Claim(
                claim: GeometryClaims.QuaternionAlgebraicSanitySurface,
                lawId: "quaternion.algebraic-sanity-and-fromto-poles"
            )
        ),
        Case(
            id: "quaternion.from-to-full-width-alignment",
            run: () => Laws.Claim(
                claim: GeometryClaims.QuaternionFromToAlignmentSurface,
                lawId: "quaternion.from-to-full-width-alignment"
            )
        ),
        Case(
            id: "dual.chain-rule-ladder-and-exact-spot-checks",
            run: () => Laws.Claim(
                claim: GeometryClaims.DualDerivativeSurface,
                lawId: "dual.chain-rule-ladder-and-exact-spot-checks"
            )
        ),
    ];
    private static LawCase[] Vector2WedgeDotCases() => [
        // ---- vector2 wedge/dot ----
        Case(
            id: "quaternion.hamilton-product-dot-inverse-full-width",
            run: () => Laws.Claim(
                claim: GeometryClaims.QuaternionHamiltonProductDotInverseSurface,
                lawId: "quaternion.hamilton-product-dot-inverse-full-width"
            )
        ),
        Case(
            id: "quaternion.rotate-schedule-transcription-full-width",
            run: () => Laws.Claim(
                claim: GeometryClaims.QuaternionRotateScheduleTranscriptionSurface,
                lawId: "quaternion.rotate-schedule-transcription-full-width"
            )
        ),
        Case(
            id: "vector.plane-full-width-oracle-and-identities",
            run: () => Laws.Claim(
                claim: GeometryClaims.Vector2FullWidthOracleAndIdentitiesSurface,
                lawId: "vector.plane-full-width-oracle-and-identities"
            )
        ),
        Case(
            id: "vector.space-full-width-oracle-and-length-policy",
            run: () => Laws.Claim(
                claim: GeometryClaims.Vector3DotCrossOracleSurface,
                lawId: "vector.space-full-width-oracle-and-length-policy"
            )
        ),
    ];
    private static LawCase[] ComplexRigidStatCases() => [
        // ---- complex / rigid transform ----
        Case(
            id: "complex.division-multiply-full-width-oracle",
            run: () => Laws.Claim(
                claim: GeometryClaims.ComplexDivisionMultiplyFullWidthOracleSurface,
                lawId: "complex.division-multiply-full-width-oracle"
            )
        ),
        Case(
            id: "complex.from-to-full-width-alignment-and-scale-safety",
            run: () => Laws.Claim(
                claim: GeometryClaims.ComplexFromToAndScaleSafetySurface,
                lawId: "complex.from-to-full-width-alignment-and-scale-safety"
            )
        ),
        Case(
            id: "quaternion.normalize-full-width-oracle-and-four-square-carry",
            run: () => Laws.Claim(
                claim: GeometryClaims.NormalizeFullWidthOracleSurface,
                lawId: "quaternion.normalize-full-width-oracle-and-four-square-carry"
            )
        ),
        Case(
            id: "rigid.round-trip-ladder-self-consistency",
            run: () => Laws.Claim(
                claim: GeometryClaims.RigidTransformRoundTripSurface,
                lawId: "rigid.round-trip-ladder-self-consistency"
            )
        ),
    ];
    private static LawCase[] PresentedAlgebraSurfaceCases() => [
        // ---- the presented algebra: kernels, modules, diagrams and the zeta seam ----
        Case(
            id: "presented.clifford-signature-ladder-vs-geometric",
            run: () => Laws.Claim(
                claim: PresentedKernelClaims.CliffordSignatureLadderMatchesGeometricAlgebra,
                lawId: "presented.clifford-signature-ladder-vs-geometric"
            )
        ),
        Case(
            id: "presented.octonion-twist-cocycle-count",
            run: () => Laws.Claim(
                claim: PresentedKernelClaims.OctonionTwistCocycleCountMatchesDoublingAssociatorSupport,
                lawId: "presented.octonion-twist-cocycle-count"
            )
        ),
        Case(
            id: "presented.binary-field-wide-degrees-twin",
            run: () => Laws.Claim(
                claim: PresentedKernelClaims.WideBinaryFieldTwinsShippedKernel,
                lawId: "presented.binary-field-wide-degrees-twin"
            )
        ),
        Case(
            id: "presented.sedenion-pair-zero-divisor-count",
            run: () => Laws.Claim(
                claim: PresentedKernelClaims.SedenionPairSumZeroDivisorCount,
                lawId: "presented.sedenion-pair-zero-divisor-count"
            )
        ),
        Case(
            id: "presented.path-algebra-argument-validation",
            run: () => Laws.Claim(
                claim: PresentedKernelClaims.PathAlgebraArgumentValidationRefusesByShape,
                lawId: "presented.path-algebra-argument-validation"
            )
        ),
        Case(
            id: "presented.live-associator-vs-doubling-tower",
            run: () => Laws.Claim(
                claim: PresentedModuleClaims.LiveAssociatorMatchesDoublingTower,
                lawId: "presented.live-associator-vs-doubling-tower"
            )
        ),
        Case(
            id: "presented.sedenion-quadruple-bracketing-exhaustive",
            run: () => Laws.Claim(
                claim: PresentedModuleClaims.SedenionQuadrupleBracketingsExhaustive,
                lawId: "presented.sedenion-quadruple-bracketing-exhaustive"
            )
        ),
        Case(
            id: "presented.braiding-self-consistent-eight-instances",
            run: () => Laws.Claim(
                claim: PresentedDiagramClaims.BraidingCertificateSelfConsistentAtEightInstances,
                lawId: "presented.braiding-self-consistent-eight-instances"
            )
        ),
        Case(
            id: "presented.quantum-torus-vs-skew-pairing",
            run: () => Laws.Claim(
                claim: PresentedDiagramClaims.QuantumTorusChargeMatchesSkewPairing,
                lawId: "presented.quantum-torus-vs-skew-pairing"
            )
        ),
        Case(
            id: "presented.functor-twins-transfer-varied-length",
            run: () => Laws.Claim(
                claim: PresentedDiagramClaims.FunctorTwinsTransferAtVariedLength,
                lawId: "presented.functor-twins-transfer-varied-length"
            )
        ),
        Case(
            id: "presented.unit-interval-power-of-two-envelope-boundary",
            run: () => Laws.Claim(
                claim: PresentedZetaClaims.UnitIntervalPowerOfTwoEnvelopeBoundary,
                lawId: "presented.unit-interval-power-of-two-envelope-boundary"
            )
        ),
        Case(
            id: "presented.unit-interval-fused-competing-terms-vs-oracle",
            run: () => Laws.Claim(
                claim: PresentedZetaClaims.UnitIntervalFusedCompetingTermsVsOracle,
                lawId: "presented.unit-interval-fused-competing-terms-vs-oracle"
            )
        ),
    ];
    private static LawCase[] StageSweepCases() => [
        // ---- the partitioner, digital-net, binary-field and fixed-point stage sweeps ----
        Case(
            id: "core.monotonic-partitioner-full-domain-sweep",
            run: () => Laws.Claim(
                claim: MonotonicPartitionerClaims.RoutingIsDeterministicMonotonicAndUniformSurface,
                lawId: "core.monotonic-partitioner-full-domain-sweep"
            )
        ),
        Case(
            id: "core.monotonic-partitioner-metrics-vs-reference-walk",
            run: () => Laws.Claim(
                claim: MonotonicPartitionerClaims.MetricsMatchReferenceChainWalkSurface,
                lawId: "core.monotonic-partitioner-metrics-vs-reference-walk"
            )
        ),
        Case(
            id: "core.monotonic-partitioner-guid-protocol-pin",
            run: () => Laws.Claim(
                claim: MonotonicPartitionerClaims.GuidRoutesThroughTrailingEntropyProtocolSurface,
                lawId: "core.monotonic-partitioner-guid-protocol-pin"
            )
        ),
        Case(
            id: "core.monotonic-partitioner-bucket-count-refusals",
            run: () => Laws.Claim(
                claim: MonotonicPartitionerClaims.BucketCountOutOfRangeRefusesSurface,
                lawId: "core.monotonic-partitioner-bucket-count-refusals"
            )
        ),
        Case(
            id: "core.binary-integer-signed-extremes-and-refusals",
            run: () => Laws.Claim(
                claim: WorldCoordClaims.BinaryIntegerSignedExtremesAndRefusalsSurface,
                lawId: "core.binary-integer-signed-extremes-and-refusals"
            )
        ),
        Case(
            id: "sampling.digital-net-property-through-order-fourteen",
            run: () => Laws.Claim(
                claim: DigitalNetClaims.NetPropertyThroughOrderFourteenSurface,
                lawId: "sampling.digital-net-property-through-order-fourteen"
            )
        ),
        Case(
            id: "sampling.digital-net-shifted-and-shuffled-blocks-are-nets",
            run: () => Laws.Claim(
                claim: DigitalNetClaims.ShiftedAndShuffledBlocksAreNetsSurface,
                lawId: "sampling.digital-net-shifted-and-shuffled-blocks-are-nets"
            )
        ),
        Case(
            id: "sampling.digital-net-radical-inverse-full-range",
            run: () => Laws.Claim(
                claim: DigitalNetClaims.RadicalInverseFullRangeSurface,
                lawId: "sampling.digital-net-radical-inverse-full-range"
            )
        ),
        Case(
            id: "sampling.cone-table-build-purity-and-quantized-coverage",
            run: () => Laws.Claim(
                claim: DigitalNetClaims.ConeTableBuildPurityAndQuantizedCoverageSurface,
                lawId: "sampling.cone-table-build-purity-and-quantized-coverage"
            )
        ),
        Case(
            id: "binary-field.narrow-degree-inverse-vs-oracle",
            run: () => Laws.Claim(
                claim: BinaryFieldRegionClaims.NarrowDegreeInverseSurface,
                lawId: "binary-field.narrow-degree-inverse-vs-oracle"
            )
        ),
        Case(
            id: "binary-field.narrow-degree-regions-vs-oracle",
            run: () => Laws.Claim(
                claim: BinaryFieldRegionClaims.NarrowDegreeRegionsSurface,
                lawId: "binary-field.narrow-degree-regions-vs-oracle"
            )
        ),
        Case(
            id: "binary-field.region-tiers-vs-scalar-rung",
            run: () => Laws.Claim(
                claim: BinaryFieldRegionClaims.RegionTiersVsScalarRungSurface,
                lawId: "binary-field.region-tiers-vs-scalar-rung"
            )
        ),
        Case(
            id: "binary-field.wide-region-tiers-vs-scalar-rung",
            run: () => Laws.Claim(
                claim: BinaryFieldRegionClaims.WideRegionTiersVsScalarRungSurface,
                lawId: "binary-field.wide-region-tiers-vs-scalar-rung"
            )
        ),
        Case(
            id: "binary-field.region-lengths-vs-scalar-rung",
            run: () => Laws.Claim(
                claim: BinaryFieldRegionClaims.RegionLengthsVsScalarRungSurface,
                lawId: "binary-field.region-lengths-vs-scalar-rung"
            )
        ),
        Case(
            id: "reed-solomon.generator-roots-vs-oracle",
            run: () => Laws.Claim(
                claim: ReedSolomonClaims.GeneratorRootsSurface,
                lawId: "reed-solomon.generator-roots-vs-oracle"
            )
        ),
        Case(
            id: "reed-solomon.published-remainder",
            run: () => Laws.Claim(
                claim: ReedSolomonClaims.PublishedRemainderSurface,
                lawId: "reed-solomon.published-remainder"
            )
        ),
        Case(
            id: "reed-solomon.codeword-syndromes-vanish",
            run: () => Laws.Claim(
                claim: ReedSolomonClaims.CodewordSyndromesSurface,
                lawId: "reed-solomon.codeword-syndromes-vanish"
            )
        ),
        Case(
            id: "reed-solomon.refusals-and-wide-carrier",
            run: () => Laws.Claim(
                claim: ReedSolomonClaims.SurfaceRefusalsAndWideCarrierSurface,
                lawId: "reed-solomon.refusals-and-wide-carrier"
            )
        ),
        Case(
            id: "vector.move-toward-boundaries-and-segment",
            run: () => Laws.Claim(
                claim: MoveTowardAndEmitterClaims.MoveTowardSurface,
                lawId: "vector.move-toward-boundaries-and-segment"
            )
        ),
        Case(
            id: "scalar.move-toward-boundaries-and-segment",
            run: () => Laws.Claim(
                claim: MoveTowardAndEmitterClaims.ScalarMoveTowardSurface,
                lawId: "scalar.move-toward-boundaries-and-segment"
            )
        ),
        Case(
            id: "core.rust-port-emitters-are-pure-and-live",
            run: () => Laws.Claim(
                claim: MoveTowardAndEmitterClaims.RustPortEmitterSurface,
                lawId: "core.rust-port-emitters-are-pure-and-live"
            )
        ),
        Case(
            id: "algebra.angular-frequency-exact-and-vs-double",
            run: () => Laws.Claim(
                claim: AngularFrequencyAndRationalClaims.AngularFrequencySurface,
                lawId: "algebra.angular-frequency-exact-and-vs-double"
            )
        ),
        Case(
            id: "algebra.rational-field-axioms",
            run: () => Laws.Claim(
                claim: AngularFrequencyAndRationalClaims.RationalAlgebraSurface,
                lawId: "algebra.rational-field-axioms"
            )
        ),
        Case(
            id: "core.layer-sequence-walker-and-bounded-horizon",
            run: () => Laws.Claim(
                claim: FixedPointContractClaims.LayerSequenceWalkerAndBoundedHorizonSurface,
                lawId: "core.layer-sequence-walker-and-bounded-horizon"
            )
        ),
        Case(
            id: "core.bitwise-pair-signed-narrow-and-wide-carriers",
            run: () => Laws.Claim(
                claim: FixedPointContractClaims.BitwisePairSignedNarrowAndWideCarriersSurface,
                lawId: "core.bitwise-pair-signed-narrow-and-wide-carriers"
            )
        ),
        Case(
            id: "sampling.field-noise-wide-position-alias-and-rebase",
            run: () => Laws.Claim(
                claim: FixedPointContractClaims.FieldNoiseWidePositionAliasAndRebaseSurface,
                lawId: "sampling.field-noise-wide-position-alias-and-rebase"
            )
        ),
        Case(
            id: "core.unsigned-square-root-uint128-carrier-boundary",
            run: () => Laws.Claim(
                claim: FixedPointContractClaims.UnsignedSquareRootUInt128CarrierBoundarySurface,
                lawId: "core.unsigned-square-root-uint128-carrier-boundary"
            )
        ),
        Case(
            id: "core.fixed-tick-conversion-rounds-up-against-rational-arithmetic",
            run: () => Laws.Claim(
                claim: FixedPointContractClaims.FixedTickConversionRoundsUpAgainstRationalArithmetic,
                lawId: "core.fixed-tick-conversion-rounds-up-against-rational-arithmetic"
            )
        ),
        Case(
            id: "core.round-rational-scales-ties-and-refuses",
            run: () => Laws.Claim(
                claim: FixedPointContractClaims.TryRoundRationalScalesTiesAndRefuses,
                lawId: "core.round-rational-scales-ties-and-refuses"
            )
        ),
        Case(
            id: "core.fixed-tick-conversion-exact-refuses-inexact-decimals",
            run: () => Laws.Claim(
                claim: FixedPointContractClaims.TryDurationEngineTicksExactAgainstDecimalBits,
                lawId: "core.fixed-tick-conversion-exact-refuses-inexact-decimals"
            )
        ),
        Case(
            id: "scalar.cyclic-rotation-plane-count-matches-coxeter-conjugacy",
            run: () => Laws.Claim(
                claim: Subjects.CyclicRotationPlaneCountIsCoxeterConjugacyPairCount,
                lawId: "scalar.cyclic-rotation-plane-count-matches-coxeter-conjugacy"
            )
        ),
        Case(
            id: "sampling.field-noise-sample-vs-exact-oracle",
            run: () => Laws.Claim(
                claim: FieldNoiseOracleClaims.FieldNoiseSampleMatchesExactOracle,
                lawId: "sampling.field-noise-sample-vs-exact-oracle"
            )
        ),
    ];
    private static LawCase[] PresentedStructureCases() => [
        // ---- the presented structure surface and the quadratic-integer wing ----
        Case(
            id: "presented.clifford-conformal-cells-vs-oracle",
            run: () => Laws.Claim(
                claim: PresentedStructureClaims.ConformalCliffordCellsSurface,
                lawId: "presented.clifford-conformal-cells-vs-oracle"
            )
        ),
        Case(
            id: "presented.sedenion-basis-vs-doubling-tower",
            run: () => Laws.Claim(
                claim: PresentedStructureClaims.SedenionBasisVsDoublingTowerSurface,
                lawId: "presented.sedenion-basis-vs-doubling-tower"
            )
        ),
        Case(
            id: "presented.quiver-counting-star-vs-walk-oracle",
            run: () => Laws.Claim(
                claim: PresentedStructureClaims.QuiverCountingStarVsWalkOracleSurface,
                lawId: "presented.quiver-counting-star-vs-walk-oracle"
            )
        ),
        Case(
            id: "presented.divisibility-cubed-divisor-count",
            run: () => Laws.Claim(
                claim: PresentedStructureClaims.DirichletDivisorCubeSurface,
                lawId: "presented.divisibility-cubed-divisor-count"
            )
        ),
        Case(
            id: "presented.duality-weighted-equivalence-vs-enumeration",
            run: () => Laws.Claim(
                claim: PresentedStructureClaims.WeightedDualityEquivalenceSurface,
                lawId: "presented.duality-weighted-equivalence-vs-enumeration"
            )
        ),
        Case(
            id: "presented.complement-wedge-and-incidence-beyond-euclidean",
            run: () => Laws.Claim(
                claim: PresentedStructureClaims.NonMetricComplementBeyondEuclideanSurface,
                lawId: "presented.complement-wedge-and-incidence-beyond-euclidean"
            )
        ),
        Case(
            id: "presented.transfer-functor-vs-legacy-copies",
            run: () => Laws.Claim(
                claim: PresentedStructureClaims.TransferFunctorLegacyCopiesSurface,
                lawId: "presented.transfer-functor-vs-legacy-copies"
            )
        ),
        Case(
            id: "presented.motor-sandwich-vs-geometric-algebra",
            run: () => Laws.Claim(
                claim: PresentedStructureClaims.MotorSandwichVsGeometricAlgebraSurface,
                lawId: "presented.motor-sandwich-vs-geometric-algebra"
            )
        ),
        Case(
            id: "presented.shuffle-near-cap-basis",
            run: () => Laws.Claim(
                claim: PresentedStructureClaims.ShuffleNearCapBasisSurface,
                lawId: "presented.shuffle-near-cap-basis"
            )
        ),
        Case(
            id: "presented.homology-torus-free-rank-two",
            run: () => Laws.Claim(
                claim: PresentedStructureClaims.HomologyTorusFreeRankTwoSurface,
                lawId: "presented.homology-torus-free-rank-two"
            )
        ),
        Case(
            id: "quadratic-integer.class-number-one-worlds-factor-prime-canonical",
            run: () => Laws.Claim(
                claim: QuadraticIntegerClaims.ClassNumberOneWorldsFactorSurface,
                lawId: "quadratic-integer.class-number-one-worlds-factor-prime-canonical"
            )
        ),
        Case(
            id: "quadratic-integer.golden-unit-and-splitting-vs-jacobi",
            run: () => Laws.Claim(
                claim: QuadraticIntegerClaims.GoldenUnitAndSplittingSurface,
                lawId: "quadratic-integer.golden-unit-and-splitting-vs-jacobi"
            )
        ),
        Case(
            id: "quadratic-integer.sum-of-two-squares-and-class-group-witness",
            run: () => Laws.Claim(
                claim: QuadraticIntegerClaims.SumOfTwoSquaresAndWitnessSurface,
                lawId: "quadratic-integer.sum-of-two-squares-and-class-group-witness"
            )
        ),
        Case(
            id: "quadratic-integer.factorization-is-deterministic",
            run: () => Laws.Claim(
                claim: QuadraticIntegerClaims.FactorizationDeterminismSurface,
                lawId: "quadratic-integer.factorization-is-deterministic"
            )
        ),
        Case(
            id: "quadratic-integer.fast-tier-routing-vs-independent-reference",
            run: () => Laws.Claim(
                claim: QuadraticIntegerClaims.FastTierRoutingSurface,
                lawId: "quadratic-integer.fast-tier-routing-vs-independent-reference"
            )
        ),
        Case(
            id: "quadratic-integer.real-order-fundamental-unit-vs-retired-scan",
            run: () => Laws.Claim(
                claim: QuadraticIntegerClaims.RealOrderFundamentalUnitVsRetiredScanSurface,
                lawId: "quadratic-integer.real-order-fundamental-unit-vs-retired-scan"
            )
        ),
        Case(
            id: "quadratic-integer.landmine-and-descriptor-invariance",
            run: () => Laws.Claim(
                claim: QuadraticIntegerClaims.LandmineAndDescriptorInvarianceSurface,
                lawId: "quadratic-integer.landmine-and-descriptor-invariance"
            )
        ),
        Case(
            id: "quadratic-integer.pell-delegation-vs-retired-convergent-loop",
            run: () => Laws.Claim(
                claim: QuadraticIntegerClaims.PellDelegationVsRetiredConvergentLoopSurface,
                lawId: "quadratic-integer.pell-delegation-vs-retired-convergent-loop"
            )
        ),
        Case(
            id: "quadratic-integer.audit-hang-completes-forced-sign",
            run: () => Laws.Claim(
                claim: QuadraticIntegerClaims.AuditHangCompletesForcedSignSurface,
                lawId: "quadratic-integer.audit-hang-completes-forced-sign"
            )
        ),
        Case(
            id: "quadratic-integer.real-order-prime-norm-existence-vs-retired-orbit-box",
            run: () => Laws.Claim(
                claim: QuadraticIntegerClaims.RealOrderPrimeNormExistenceVsRetiredOrbitBoxSurface,
                lawId: "quadratic-integer.real-order-prime-norm-existence-vs-retired-orbit-box"
            )
        ),
        Case(
            id: "quadratic-integer.real-order-factorization-beyond-orbit-box",
            run: () => Laws.Claim(
                claim: QuadraticIntegerClaims.RealOrderFactorizationBeyondOrbitBoxSurface,
                lawId: "quadratic-integer.real-order-factorization-beyond-orbit-box"
            )
        ),
        Case(
            id: "algebra.quadratic-surd-twin-lane",
            run: () => Laws.Claim(
                claim: DoublingTowerClaims.QuadraticSurdTwinLaneSurface,
                lawId: "algebra.quadratic-surd-twin-lane"
            )
        ),
        Case(
            id: "algebra.quadratic-twin-linear-ops-full-range",
            run: () => Laws.Claim(
                claim: DoublingTowerClaims.QuadraticTwinLinearOpsFullRangeSurface,
                lawId: "algebra.quadratic-twin-linear-ops-full-range"
            )
        ),
        Case(
            id: "algebra.doubling-floor1-matches-fixed-complex",
            run: () => Laws.Claim(
                claim: DoublingTowerClaims.DoublingFloor1MatchesFixedComplexSurface,
                lawId: "algebra.doubling-floor1-matches-fixed-complex"
            )
        ),
        Case(
            id: "algebra.doubling-floor2-matches-fixed-quaternion",
            run: () => Laws.Claim(
                claim: DoublingTowerClaims.DoublingFloor2MatchesFixedQuaternionSurface,
                lawId: "algebra.doubling-floor2-matches-fixed-quaternion"
            )
        ),
        Case(
            id: "algebra.doubling-floor2-commutator-witness",
            run: () => Laws.Claim(
                claim: DoublingTowerClaims.DoublingFloor2CommutatorWitnessSurface,
                lawId: "algebra.doubling-floor2-commutator-witness"
            )
        ),
        Case(
            id: "algebra.doubling-floor3-octonion-norm-vs-oracle",
            run: () => Laws.Claim(
                claim: DoublingTowerClaims.DoublingFloor3OctonionNormVsOracleSurface,
                lawId: "algebra.doubling-floor3-octonion-norm-vs-oracle"
            )
        ),
        Case(
            id: "presented.clifford-planar-complex-twin",
            run: () => Laws.TwinBinary(
                domain: CliffordPlanarComplex,
                first: GeometricAlgebraClaims.GeometricPlanarComplexSubject,
                lawId: "presented.clifford-planar-complex-twin",
                second: GeometricAlgebraClaims.FixedComplexLanes,
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
                second: GeometricAlgebraClaims.FixedSplitLanes,
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
                second: GeometricAlgebraClaims.FixedDualLanes,
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
        Case(
            id: "presented.clifford-motor-rigid-transform-twin",
            run: () => Laws.SweptClaim(
                claim: GeometricAlgebraClaims.GeometricMotorRigidTransformSurface,
                domain: CliffordMotor,
                lawId: "presented.clifford-motor-rigid-transform-twin",
                tier: Tier.Default,
                width: 10
            )
        ),
        Case(
            id: "presented.clifford-reverse-anti-automorphism",
            run: () => Laws.SweptClaim(
                claim: GeometricAlgebraClaims.GeometricReverseSurface,
                domain: CliffordReverse,
                lawId: "presented.clifford-reverse-anti-automorphism",
                tier: Tier.Default,
                width: 16
            )
        ),
        Case(
            id: "presented.clifford-multivector-decomposition",
            run: () => Laws.SweptClaim(
                claim: GeometricAlgebraClaims.GeometricMultivectorDecompositionSurface,
                domain: CliffordMultivector,
                lawId: "presented.clifford-multivector-decomposition",
                tier: Tier.Default,
                width: 16
            )
        ),
        Case(
            id: "algebra.monogenic-degree2-and-degree3-match-independent-reference",
            run: () => Laws.SweptClaim(
                claim: GeometricAlgebraClaims.MonogenicExactSurface,
                domain: MonogenicExact,
                lawId: "algebra.monogenic-degree2-and-degree3-match-independent-reference",
                tier: Tier.Default,
                width: 8
            )
        ),
        Case(
            id: "algebra.monogenic-plastic-ratio-recurrence",
            run: () => Laws.Claim(
                claim: GeometricAlgebraClaims.MonogenicPlasticRatioSurface,
                lawId: "algebra.monogenic-plastic-ratio-recurrence"
            )
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
        Case(
            id: "meet.associative",
            run: () => Laws.SweptClaim(
                claim: MeetClaims.MeetIsAssociative,
                domain: MeetAssociative,
                lawId: "meet.associative",
                tier: Tier.Default,
                width: 3
            )
        ),
        Case(
            id: "meet.attenuation-never-widens",
            run: () => Laws.SweptClaim(
                claim: MeetClaims.MeetNeverWidens,
                domain: MeetMonotonicity,
                lawId: "meet.attenuation-never-widens",
                tier: Tier.Default,
                width: 3
            )
        ),
        Case(
            id: "meet.bottom-absorbing",
            run: () => Laws.SweptClaim(
                claim: MeetClaims.BottomAbsorbs,
                domain: MeetBottomAbsorption,
                lawId: "meet.bottom-absorbing",
                tier: Tier.Default,
                width: 2
            )
        ),
        Case(
            id: "meet.commutative",
            run: () => Laws.SweptClaim(
                claim: MeetClaims.MeetIsCommutative,
                domain: MeetCommutative,
                lawId: "meet.commutative",
                tier: Tier.Default,
                width: 2
            )
        ),
        Case(
            id: "meet.idempotent",
            run: () => Laws.SweptClaim(
                claim: MeetClaims.MeetIsIdempotent,
                domain: MeetIdempotent,
                lawId: "meet.idempotent",
                tier: Tier.Default,
                width: 2
            )
        ),
        Case(
            id: "meet.order-agrees-with-meet",
            run: () => Laws.SweptClaim(
                claim: MeetClaims.OrderAgreesWithMeet,
                domain: MeetOrderCoherence,
                lawId: "meet.order-agrees-with-meet",
                tier: Tier.Default,
                width: 2
            )
        ),
        Case(
            id: "meet.product-composes-componentwise",
            run: () => Laws.SweptClaim(
                claim: MeetClaims.ProductComposesComponentwise,
                domain: MeetProductComposition,
                lawId: "meet.product-composes-componentwise",
                tier: Tier.Default,
                width: 3
            )
        ),
        Case(
            id: "meet.top-identity",
            run: () => Laws.SweptClaim(
                claim: MeetClaims.TopIsIdentity,
                domain: MeetTopIdentity,
                lawId: "meet.top-identity",
                tier: Tier.Default,
                width: 2
            )
        ),

    ];
    private static LawCase[] NttCases() => [
        // ---- the exact number-theoretic transform over PrimeField64 ----
        Case(
            id: "ntt.prime-and-primitive-root",
            run: () => Laws.Claim(
                claim: NttClaims.PrimeAndPrimitiveRoot,
                lawId: "ntt.prime-and-primitive-root"
            )
        ),
        Case(
            id: "ntt.round-trip-exact",
            run: () => Laws.Claim(
                claim: NttClaims.RoundTripExact,
                lawId: "ntt.round-trip-exact"
            )
        ),
        Case(
            id: "ntt.linearity-exact",
            run: () => Laws.Claim(
                claim: NttClaims.LinearityExact,
                lawId: "ntt.linearity-exact"
            )
        ),
        Case(
            id: "ntt.convolution-vs-oracle",
            run: () => Laws.Claim(
                claim: NttClaims.ConvolutionVsOracle,
                lawId: "ntt.convolution-vs-oracle"
            )
        ),
        Case(
            id: "ntt.length-refusals",
            run: () => Laws.Claim(
                claim: NttClaims.LengthRefusals,
                lawId: "ntt.length-refusals"
            )
        ),

    ];
    private static LawCase[] FftCases() => [
        // ---- the fixed-point FFT over FixedComplex ----
        Case(
            id: "fft.impulse-dc-nyquist-exact",
            run: () => Laws.Claim(
                claim: FftClaims.ImpulseDcNyquistExact,
                lawId: "fft.impulse-dc-nyquist-exact"
            )
        ),
        Case(
            id: "fft.round-trip-bound",
            run: () => Laws.Claim(
                claim: FftClaims.RoundTripBound,
                lawId: "fft.round-trip-bound"
            )
        ),
        Case(
            id: "fft.round-trip-bound-deep",
            run: () => Laws.Claim(
                claim: FftClaims.RoundTripBoundDeepMirror,
                lawId: "fft.round-trip-bound-deep"
            )
        ),
        Case(
            id: "fft.linearity-bound",
            run: () => Laws.Claim(
                claim: FftClaims.LinearityBound,
                lawId: "fft.linearity-bound"
            )
        ),
        Case(
            id: "fft.linearity-bound-deep",
            run: () => Laws.Claim(
                claim: FftClaims.LinearityBoundDeepMirror,
                lawId: "fft.linearity-bound-deep"
            )
        ),
        Case(
            id: "fft.parseval-bound",
            run: () => Laws.Claim(
                claim: FftClaims.ParsevalBound,
                lawId: "fft.parseval-bound"
            )
        ),
        Case(
            id: "fft.parseval-bound-deep",
            run: () => Laws.Claim(
                claim: FftClaims.ParsevalBoundDeepMirror,
                lawId: "fft.parseval-bound-deep"
            )
        ),
        Case(
            id: "fft.self-referential-bit-identity",
            run: () => Laws.Claim(
                claim: FftClaims.SelfReferentialBitIdentity,
                lawId: "fft.self-referential-bit-identity"
            )
        ),
        Case(
            id: "fft.radix2-vs-direct-sum",
            run: () => Laws.Claim(
                claim: FftClaims.Radix2VsDirectSum,
                lawId: "fft.radix2-vs-direct-sum"
            )
        ),
        Case(
            id: "fft.real-wrappers-are-faithful-embeddings",
            run: () => Laws.Claim(
                claim: FftClaims.RealWrappersAreFaithfulEmbeddings,
                lawId: "fft.real-wrappers-are-faithful-embeddings"
            )
        ),
        Case(
            id: "fft.length-refusals",
            run: () => Laws.Claim(
                claim: FftClaims.LengthRefusals,
                lawId: "fft.length-refusals"
            )
        ),
    ];
}
