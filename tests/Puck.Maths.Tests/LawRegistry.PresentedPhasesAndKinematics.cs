namespace Puck.Maths.Tests;

internal static partial class LawRegistry {
    private static LawCase[] PresentedChargedAlgebraCases() => [
        // ---- the presented charged algebra: one kernel, many presentations ----
        //
        // Every case below drives PresentedAlgebra.Multiply and differs from every other ONLY by the presentation value
        // and the material type argument. The twins are against the hand-written kernels the derived form reproduces;
        // the oracle laws are against BigInteger reference arithmetic that shares nothing with either.

        Case(
            id: "presented.interpreted-equals-compiled",
            run: () => {
                Laws.Claim(
                    claim: Subjects.InterpretedEqualsCompiled,
                    lawId: "presented.interpreted-equals-compiled"
                );
                Laws.Claim(
                    claim: OracleOwnershipClaims.PresentationOwnsAdmittedMemory,
                    lawId: "presented.interpreted-equals-compiled"
                );
            }
        ),

        Case(
            id: "presented.clifford-twin-geometric-3-0-0",
            run: () => Laws.VectorTwin(
                lawId: "presented.clifford-twin-geometric-3-0-0",
                domain: Presented,
                tier: Tier.Default,
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
            id: "presented.clifford-twin-geometric-3-0-1",
            run: () => Laws.VectorTwin(
                lawId: "presented.clifford-twin-geometric-3-0-1",
                domain: Presented,
                tier: Tier.Default,
                width: 16,
                first: Subjects.PresentedCliffordMultiply(
                    degenerateCount: 1,
                    negativeCount: 0,
                    positiveCount: 3
                ),
                second: Subjects.GeometricMultiply(
                    degenerateCount: 1,
                    negativeCount: 0,
                    positiveCount: 3
                ),
                witness: Subjects.CliffordProductOracle(
                    degenerateCount: 1,
                    negativeCount: 0,
                    positiveCount: 3
                )
            )
        ),

        Case(
            id: "presented.certification-scopes-associativity-not-confluence",
            run: () => Laws.Claim(
                claim: Subjects.CertificationScopesAssociativityNotConfluence,
                lawId: "presented.certification-scopes-associativity-not-confluence"
            )
        ),

        // The conformal (4,1,0) world has five generators and thirty-two blades, which the four-generator
        // GeometricAlgebra cannot reach at all: there is no twin here, so the charges answer to the bubble-sort oracle
        // and the algebra answers to its own associativity certificate.
        Case(
            id: "presented.clifford-charge-vs-oracle",
            run: () => {
                Laws.Claim(
                    claim: Subjects.CliffordChargesMatchOracle,
                    lawId: "presented.clifford-charge-vs-oracle"
                );
                Laws.VectorMatchesOracle(
                    lawId: "presented.clifford-charge-vs-oracle",
                    domain: Presented,
                    tier: Tier.Default,
                    width: 32,
                    subject: Subjects.PresentedCliffordMultiply(
                        degenerateCount: 0,
                        negativeCount: 1,
                        positiveCount: 4
                    ),
                    oracle: Subjects.CliffordProductOracle(
                        degenerateCount: 0,
                        negativeCount: 1,
                        positiveCount: 4
                    )
                );
            }
        ),

        Case(
            id: "presented.octonion-twin-doubling",
            run: () => Laws.VectorTwin(
                lawId: "presented.octonion-twin-doubling",
                domain: Presented,
                tier: Tier.Default,
                width: 8,
                first: Subjects.PresentedCayleyDicksonMultiply(floors: 3),
                second: Subjects.DoublingOctonionMultiply,
                witness: Subjects.CayleyDicksonProductOracle(floors: 3)
            )
        ),

        // The quasialgebra floor, on the exact sublattice where the associator measures coherence rather than rounding.
        Case(
            id: "presented.associator-twin-doubling",
            run: () => {
                Laws.Claim(
                    claim: Subjects.CayleyDicksonChargesAndCertificates,
                    lawId: "presented.associator-twin-doubling"
                );
                Laws.VectorTernaryTwin(
                    lawId: "presented.associator-twin-doubling",
                    domain: Sublattice,
                    tier: Tier.Default,
                    width: 8,
                    first: Subjects.PresentedCayleyDicksonAssociator(floors: 3),
                    second: Subjects.DoublingOctonionAssociator,
                    witness: null
                );
            }
        ),

    ];
    private static LawCase[] Phase3CoherenceCases() => [
        // ---- phase 3: coherence live ----
        //
        // The associator stops being only a readout and becomes a rule charge the normalizer APPLIES. The oracle is the
        // bracketing's own nested products, which re-associate nothing, so agreement is route-independence measured
        // rather than asserted; the certificate's quadruple identity is the same statement about the charges alone.
        Case(
            id: "presented.reassociation-route-coherent",
            run: () => Laws.Claim(
                claim: Subjects.ReassociationRouteCoherent,
                lawId: "presented.reassociation-route-coherent"
            )
        ),

        // The other half of the same change: a uniform charge of one leaves a term's brackets inert, which is what every
        // phase-1 and phase-2 gate pins and what a splice charge leaking into the uniform regime would break.
        Case(
            id: "presented.reassociation-brackets-inert",
            run: () => Laws.Claim(
                claim: Subjects.ReassociationBracketsInert,
                lawId: "presented.reassociation-brackets-inert"
            )
        ),

        // The canary and the twin the coherence slice owed. The canary pins an ABSOLUTE floor on how far a live charge
        // moves the flattener's answer, so a declaration that quietly stopped arriving fails without the case leaning on
        // the certificate's own nonassociative-triple count; the twin decides coherence a second way, by normalizing every
        // quadruple's five bracketings, so a mis-oriented pentagon inside Certify has something to disagree with. The
        // separating instance is a coherent 3-cocycle over a product that ASSOCIATES: coherence holds, faithfulness does
        // not, and the two are measured apart rather than described apart.
        Case(
            id: "presented.coherence-route-independence",
            run: () => Laws.Claim(
                claim: Subjects.CoherenceIsRouteIndependence,
                lawId: "presented.coherence-route-independence"
            )
        ),

    ];
    private static LawCase[] Phase3GroupRegimeCases() => [
        // ---- phase 3: the group regime ----
        //
        // The boundary map's group row, made an instance rather than a note: a reflection world enters as measured
        // lattice data, its order is pinned twice by constructions that share no step, and everything the row promises —
        // inverses under a unit witness per generator, orbit enumeration — is a bounded attempt with an honest refusal.
        Case(
            id: "presented.group-orders-exact",
            run: () => Laws.Claim(
                claim: Subjects.GroupOrdersExact,
                lawId: "presented.group-orders-exact"
            )
        ),

        // The world no enumeration reaches is gated by its ACTION instead: every relation the presentation declares
        // moves no node at all, and the word that reads the mirrors once is the lattice's own cycle, of the period the
        // rotation surface and the ray factorisation already carry.
        Case(
            id: "presented.reflection-action-lattice",
            run: () => Laws.Claim(
                claim: Subjects.ReflectionActionMatchesLattice,
                lawId: "presented.reflection-action-lattice"
            )
        ),

        // The twin the group slice owed: the presented PRODUCT is the lattice action. One compiled cell, one composite
        // permutation and one pair of reflections applied in sequence must name the same element, and the pinned power
        // ladder says the same thing about repeated multiplication. The oracle is SymmetryLattice.Reflect composed by
        // hand, which runs no step the algebra runs.
        Case(
            id: "presented.reflection-product-twins-action",
            run: () => Laws.Claim(
                claim: Subjects.ReflectionProductTwinsAction,
                lawId: "presented.reflection-product-twins-action"
            )
        ),

        // The refusals, and the pair that is the whole point of the row: inverses SURVIVE where enumeration refuses.
        Case(
            id: "presented.group-limits-refuse",
            run: () => {
                Laws.Claim(
                    claim: Subjects.GroupLimitsRefuse,
                    lawId: "presented.group-limits-refuse"
                );
                Laws.Claim(
                    claim: OracleClaims.PresentedGroupRequiresAssociativity,
                    lawId: "presented.group-limits-refuse"
                );
            }
        ),

        // The interval-poset instance: the incidence algebra is the quiver's shape at a sub-quiver, so mu is the
        // guarded star of the negated strict zeta and the Euler characteristic of a complex is the Möbius value of the
        // one interval spanning its bounded face order — answered to by an alternating cell count and by three
        // hand-computed numbers.
        Case(
            id: "presented.incidence-euler-mass",
            run: () => Laws.Claim(
                claim: Subjects.IncidenceEulerMass,
                lawId: "presented.incidence-euler-mass"
            )
        ),

        // The Dirichlet window IS this order's reduced incidence algebra, so the two mus agree interval for interval
        // through the interval type — and the two bases do NOT, which is what keeps the window a quotient rather than
        // a specialization.
        Case(
            id: "presented.incidence-mobius-vs-window",
            run: () => Laws.Claim(
                claim: Subjects.IncidenceMobiusMatchesWindow,
                lawId: "presented.incidence-mobius-vs-window"
            )
        ),

        // Stokes' identity is the adjunction, and the adjunction is one product read two ways: the boundary is the
        // incidence element multiplied on the left of a chain and the coboundary is the same element on the right of a
        // cochain, so the two bracketings of a pairing must agree. Exact over the integers, bit-identical over the
        // house scalar inside the carrier's headroom, and separated outside it.
        Case(
            id: "presented.stokes-adjunction",
            run: () => {
                Laws.Claim(
                    claim: Subjects.StokesAdjunction,
                    lawId: "presented.stokes-adjunction"
                );
                Laws.SweptClaim(
                    lawId: "presented.stokes-adjunction",
                    domain: Presented,
                    tier: Tier.Default,
                    width: 7,
                    claim: Subjects.StokesAdjunctionFixed()
                );
            }
        ),

        // The same adjunction at three more materials. It is the associativity of one product, so it cannot depend on
        // the carrier — which is exactly why one material proves less than it looks. The teeth are the non-degeneracy
        // count: the ordered basis pairs Stokes does not annihilate are precisely the declared incidences, so a
        // collapsed boundary fails here instead of passing on an identity between two zeros.
        Case(
            id: "presented.stokes-material-sweep",
            run: () => Laws.Claim(
                claim: Subjects.StokesMaterialSweep,
                lawId: "presented.stokes-material-sweep"
            )
        ),

        // The refusals: data that names no order and no complex is turned away at construction, and Möbius inversion
        // over a material with no signs is refused rather than approximated.
        Case(
            id: "presented.incidence-limits-refuse",
            run: () => Laws.Claim(
                claim: Subjects.IncidenceLimitsRefuse,
                lawId: "presented.incidence-limits-refuse"
            )
        ),

    ];
    private static LawCase[] Phase4BoundaryCases() => [
        // ---- phase 4: boundary composability, and co-arity greater than one ----
        //
        // Composability stopped being written out per entry and became one comparison over the generators' own
        // boundaries. The quiver and the interval poset exercise its colour half at arity one; the planar tangle
        // exercises its width half at a co-arity that is genuinely not one, and its cups and caps are the first
        // generators in the library whose two boundaries differ in length.

        // The derivation reproduces both endpoint tests cell for cell, predicted from the argument data rather than
        // from the entry, with the boundary comparison restated here and required to agree with the annihilations.
        Case(
            id: "presented.boundary-composition-unmoved",
            run: () => Laws.Claim(
                claim: Subjects.BoundaryCompositionUnmoved,
                lawId: "presented.boundary-composition-unmoved"
            )
        ),

        // The basis: block by block against the tabulated Catalan numbers AND against the ballot difference, which
        // reaches the same value without a Catalan recursion, so a mis-transcribed table fails beside a mis-enumeration.
        Case(
            id: "presented.tangle-basis-counts",
            run: () => Laws.Claim(
                claim: Subjects.TangleBasisCounts,
                lawId: "presented.tangle-basis-counts"
            )
        ),

        // The three algebraic relations, asserted on the DERIVED product at three materials. Nothing in the catalogue
        // entry mentions them, so a mis-traced arc or a mis-counted loop breaks one of the three.
        Case(
            id: "presented.tangle-relations-hold",
            run: () => Laws.Claim(
                claim: Subjects.TangleRelationsHold,
                lawId: "presented.tangle-relations-hold"
            )
        ),

        // The width cap is derived from the 512 normal forms a finite basis holds, so the width past the last admitted
        // one is refused rather than admitted and then found unusable. That the last admitted width is REACHED, at its
        // 377 diagrams, is asserted where a width-six presentation is already built: deep.presented-tangle-sweep. This
        // case costs one throw per refusal and builds nothing.
        Case(
            id: "presented.tangle-limits-refuse",
            run: () => Laws.Claim(
                claim: Subjects.TangleLimitsRefuse,
                lawId: "presented.tangle-limits-refuse"
            )
        ),

        // The canary: composing two diagrams must actually strand off closed loops, and those loops must actually be
        // charged. Every other statement in this slice holds just as well at a loop charge silently equal to one.
        Case(
            id: "presented.tangle-loop-charge-canary",
            run: () => Laws.Claim(
                claim: Subjects.TangleLoopChargeCanary,
                lawId: "presented.tangle-loop-charge-canary"
            )
        ),

    ];
    private static LawCase[] Phase4BraidingCases() => [
        // ---- phase 4: the braiding certificate ----
        //
        // The braiding is DERIVED from the compiled cells rather than declared beside them, which is strictly more than
        // the associator's flag reports: the commutation charge of an ordered pair is searched over the material's one,
        // its negation and — at a field material — the coefficient the two cells' own charges name, and it is issued
        // only after the two orderings were found to differ by it. So the reported braiding is the product's own.

        // The charges against two constructions that read no cell: the doubling recursion and the bubble-sort sign
        // oracle, and — at every floor the tower ships — the shipped nested tower multiplying both orderings out.
        Case(
            id: "presented.braiding-derived-vs-doubling",
            run: () => Laws.Claim(
                claim: Subjects.BraidingDerivedVsDoubling,
                lawId: "presented.braiding-derived-vs-doubling"
            )
        ),

        // Coherence of the braiding is a mathematical fact about the data, so it is witnessed rather than thrown: the
        // octonion floor fails the hexagons and carries the charges that disagree, and the degenerate Clifford
        // signature reports no braiding for the opposite reason — its annihilating pairs constrain no charge, so the
        // derivation never finishes and no identity is stated to fail. Both routes to a false flag are covered, and
        // the two are kept apart. The quantum torus is the instance that separates the two flags, since every
        // catalogue braiding is a sign and a sign is its own mirror.
        Case(
            id: "presented.braiding-hexagon-witnessed",
            run: () => Laws.Claim(
                claim: Subjects.BraidingHexagonWitnessed,
                lawId: "presented.braiding-hexagon-witnessed"
            )
        ),

        // The limit, and the pair that is the whole point of the row: the SAME presentation shape issues no charge at a
        // material that cannot name one half and issues it at a field material that can. A missing flag is not a
        // failure, and it is not the budget either.
        Case(
            id: "presented.braiding-limits-issue-no-flag",
            run: () => Laws.Claim(
                claim: Subjects.BraidingLimitsIssueNoFlag,
                lawId: "presented.braiding-limits-issue-no-flag"
            )
        ),

        // The canary: the derived charges must actually be nontrivial on more pairs than the measured floor, and each
        // of those pairs must re-multiply. A braiding that collapsed to the trivial one satisfies both hexagons, the
        // symmetric flag and every refusal case, so only a floor catches it.
        Case(
            id: "presented.braiding-nontrivial-canary",
            run: () => Laws.Claim(
                claim: Subjects.BraidingNontrivialCanary,
                lawId: "presented.braiding-nontrivial-canary"
            )
        ),

    ];
    private static LawCase[] Phase4MorphismsCases() => [
        // ---- phase 4: presentation morphisms and substitution systems ----
        //
        // A morphism is admitted by evaluating the source's own relations on the images, so the law's job is to prove
        // that the admission means what it says: the map really carries products to products and sums to sums, on
        // elements that are not basis elements and that the admission never examined.
        Case(
            id: "presented.functor-preserves-relations",
            run: () => {
                Laws.Claim(
                    claim: Subjects.FunctorPreservesRelations,
                    lawId: "presented.functor-preserves-relations"
                );
                Laws.Claim(
                    claim: OracleOwnershipClaims.FunctorRequiresOneMaterial,
                    lawId: "presented.functor-preserves-relations"
                );
            }
        ),

        Case(
            id: "presented.element-ownership-is-uniform",
            run: () => Laws.Claim(
                claim: OracleOwnershipClaims.ForeignElementsAreRejectedUniformly,
                lawId: "presented.element-ownership-is-uniform"
            )
        ),

        // The refusal, re-derived from the obstruction's own data: the named rule is folded through the images by hand
        // and must really fail, and the named basis pair — the annihilation a degree window states and no rule
        // carries — must really be one the images do not preserve.
        Case(
            id: "presented.functor-refuses-witness",
            run: () => Laws.Claim(
                claim: Subjects.FunctorRefusesWitness,
                lawId: "presented.functor-refuses-witness"
            )
        ),

        // A substitution system IS a morphism of free monoids, and its word must never be an element: the composed
        // letter images at √13 and √19 run 52 and 411 symbols, past what a mixed-radix key holds, so only MapWord
        // reaches them. The shipped quasicrystal streamer shares the period and the substitution recipe with the
        // subject, so the leg that stands outside both is the mechanical word of the same slope.
        Case(
            id: "presented.substitution-twins-quasicrystal",
            run: () => Laws.Claim(
                claim: Subjects.SubstitutionTwinsQuasicrystal,
                lawId: "presented.substitution-twins-quasicrystal"
            )
        ),

        // The abelianization against the inflation lens, with the orientation pinned: counting occurrences gives the
        // TRANSPOSE of the substitution matrix, which four of the six periods separate from the direct reading.
        Case(
            id: "presented.substitution-matrix-vs-inflation",
            run: () => Laws.Claim(
                claim: Subjects.SubstitutionMatrixVsInflation,
                lawId: "presented.substitution-matrix-vs-inflation"
            )
        ),

    ];
    private static LawCase[] Phase4GraphZetaCases() => [
        // ---- phase 4: the graph zeta ----
        //
        // det(I − tA) and its reciprocal, read out of the algebra's own trace and powers. Nothing new multiplies here:
        // the power sums are Trace of Power, the coefficients come out of a bounded loop over them, and the zeta is the
        // shipped guarded star of the negated augmentation part inside the jet presentation.

        // The recursion against an enumeration that shares no step with it: the oracle forms no power, takes no trace
        // and divides nowhere, while the subject does all three. The order-two case is a third route again, through a
        // continued-fraction period folded as convergent matrices.
        Case(
            id: "presented.zeta-charpoly-vs-minors",
            run: () => Laws.Claim(
                claim: Subjects.ZetaCharacteristicVsMinors,
                lawId: "presented.zeta-charpoly-vs-minors"
            )
        ),

        // The power sums ARE closed-walk counts, which is what makes the polynomial a graph invariant rather than a
        // matrix identity. Length zero is part of the statement: it is the order's worth of ones the recursion runs at.
        Case(
            id: "presented.zeta-traces-vs-walk-counts",
            run: () => Laws.Claim(
                claim: Subjects.ZetaTracesVsWalkCounts,
                lawId: "presented.zeta-traces-vs-walk-counts"
            )
        ),

        // The reciprocal, under a nilpotence certificate the star ISSUES rather than assumes, checked in both orders and
        // at degree bounds above, at and below the order — an inverse modulo t^(d+1) depends on nothing above that
        // degree, so truncating the polynomial does not truncate the statement.
        Case(
            id: "presented.zeta-reciprocal-round-trip",
            run: () => Laws.Claim(
                claim: Subjects.ZetaReciprocalRoundTrip,
                lawId: "presented.zeta-reciprocal-round-trip"
            )
        ),

        // The licence, measured on both sides: the recursion divides by every index up to the order, so a material that
        // certifies no inverses stops at index one and a field of characteristic p stops at p — and the same modulus
        // answers at the order below p. Over the house scalar nothing is offered at all, which is what exact-only means.
        Case(
            id: "presented.zeta-limits-refuse",
            run: () => Laws.Claim(
                claim: Subjects.ZetaLimitsRefuse,
                lawId: "presented.zeta-limits-refuse"
            )
        ),

    ];
    private static LawCase[] Phase4SecondProductCases() => [
        // ---- phase 4: the second product ----
        //
        // The shuffle and the quasi-shuffle are ONE catalogue entry: the generators are the words of a bounded length,
        // the cells are the interleavings with their multiplicities, and an empty letter product is the degenerate case
        // where no two heads collide. Nothing in the kernel changes — a second product is a second presentation.

        // Every cell against a brute enumeration that generates every step-kind sequence and TESTS it, where the entry
        // reads three shorter cells; and the certificate, which COMPUTES commutativity and associativity, following the
        // letter product to false wherever the letter product is not itself both.
        Case(
            id: "presented.shuffle-vs-enumeration",
            run: () => Laws.Claim(
                claim: Subjects.ShuffleMatchesEnumeration,
                lawId: "presented.shuffle-vs-enumeration"
            )
        ),

        // The binomial coefficients, read twice out of the same entry — as the multiplicity one letter's shuffle
        // carries, and as the number of words two different letters interleave into — against a Pascal's triangle built
        // by addition alone, which reaches them without a factorial, a product or a division.
        Case(
            id: "presented.shuffle-vs-binomial",
            run: () => Laws.Claim(
                claim: Subjects.ShuffleMatchesBinomials,
                lawId: "presented.shuffle-vs-binomial"
            )
        ),

        // The degenerate case, pinned from both sides: the default argument IS the empty letter product, no collision
        // term leaks into it, and a collision adds exactly the shortened terms while leaving the shuffle's own cell
        // untouched at the top length.
        Case(
            id: "presented.quasishuffle-degenerates-to-shuffle",
            run: () => Laws.Claim(
                claim: Subjects.QuasiShuffleDegeneratesToShuffle,
                lawId: "presented.quasishuffle-degenerates-to-shuffle"
            )
        ),

        // A word over one letter names an iterated sum, and multiplying two iterated sums merges their index sets — the
        // interleavings where no index coincides, the collisions where they do. So the identity holds for the
        // quasi-shuffle and FAILS for the shuffle, which is what makes the collision term load-bearing. The sequences
        // come from the antidifference of a different presentation entirely, and are pinned against Pascal first.
        Case(
            id: "presented.quasishuffle-vs-prefix-sums",
            run: () => Laws.Claim(
                claim: Subjects.QuasiShuffleMatchesPrefixSums,
                lawId: "presented.quasishuffle-vs-prefix-sums"
            )
        ),

        // The caps, which are the 512 normal forms a finite basis holds read at each argument, and the one refusal that
        // is a mathematical statement rather than a budget: a collision naming a letter the alphabet does not carry
        // names no element of this algebra, and the refusal says which ordered pair blocked. The refusals are throws
        // and cost nothing; the tuples BUILT here stay at a window of four or below, and the near-cap ones are left to
        // presented.shuffle-near-cap-basis, since each of those emits one rule per ordered pair of its 511 or 512 words
        // under the compiled basis this case reads.
        Case(
            id: "presented.shuffle-limits-refuse",
            run: () => Laws.Claim(
                claim: Subjects.ShuffleLimitsRefuse,
                lawId: "presented.shuffle-limits-refuse"
            )
        ),

        // The canary: the interleaving must actually split a product into several words and actually carry the
        // multiplicity each is reached with. A second product that quietly degenerated to concatenation satisfies every
        // flag, the degeneracy claim and every refusal above, and only a floor catches it.
        Case(
            id: "presented.shuffle-multiterm-canary",
            run: () => Laws.Claim(
                claim: Subjects.ShuffleMultiTermCanary,
                lawId: "presented.shuffle-multiterm-canary"
            )
        ),

    ];
    private static LawCase[] Phase4KnotStateSumCases() => [
        // ---- phase 4: knot state sums ----
        //
        // The last clause of the mandate, and it adds NO library member: a knot invariant here is a morphism out of the
        // free monoid on the crossing letters into the planar tangle algebra, a product with the cup and cap layers, and
        // a pairing at the empty diagram — every one of them shipped before this slice. The construction is the whole
        // phase's claim at its sharpest, so its gates are correspondingly hard.

        // The braid relations hold on the images although the free source imposed none of them, and the loop charge is
        // what makes them hold: at any other charge the crossing and its mirror stop composing to the identity.
        Case(
            id: "presented.braid-relation-holds",
            run: () => Laws.Claim(
                claim: Subjects.BraidRelationHolds,
                lawId: "presented.braid-relation-holds"
            )
        ),

        // The published bracket of the unknot, of both trefoil chiralities and of the figure-eight, carried as integer
        // Laurent coefficients and folded by Horner, answering over the rationals and over three prime fields — which is
        // what multi-point evaluation buys instead of a coefficient ring holding a formal variable.
        Case(
            id: "presented.state-sum-vs-tabulated",
            run: () => Laws.Claim(
                claim: Subjects.StateSumMatchesTabulated,
                lawId: "presented.state-sum-vs-tabulated"
            )
        ),

        // The second oracle, and the reason there are two: the enumeration builds each state's whole closed diagram as
        // one graph and counts its components, knowing nothing about knots, so it catches a mis-transcribed table where
        // the table catches a wrong construction. It runs out to eight crossings, where two-to-the-crossings still fits.
        Case(
            id: "presented.state-sum-vs-smoothing-enumeration",
            run: () => Laws.Claim(
                claim: Subjects.StateSumMatchesSmoothingEnumeration,
                lawId: "presented.state-sum-vs-smoothing-enumeration"
            )
        ),

        // The moves, all three, with the first one stated honestly: the second and third leave the value fixed and the
        // first multiplies it by minus the crossing charge cubed, so the readout is an invariant of the DIAGRAM.
        Case(
            id: "presented.state-sum-move-invariant",
            run: () => Laws.Claim(
                claim: Subjects.StateSumMoveInvariant,
                lawId: "presented.state-sum-move-invariant"
            )
        ),

        // What is refused and what is merely not claimed, kept apart: an odd plat and a plat past the width cap are
        // refused; the braid group's finite basis does not exist and every basis-dependent readout says so; its word
        // problem is a BUDGET, reported as one; and equal values are not equal knots, witnessed by a curl.
        Case(
            id: "presented.knot-limits-refuse",
            run: () => Laws.Claim(
                claim: Subjects.KnotLimitsRefuse,
                lawId: "presented.knot-limits-refuse"
            )
        ),

        // The strongest canary in the phase. An invariant collapsed to a constant satisfies every twin, every relation,
        // every refusal and every move claim above, because all of those hold just as well of a constant — only a floor
        // on how many declared pairs the values separate catches it, and only the two trefoils prove it sees chirality.
        Case(
            id: "presented.state-sum-separates-canary",
            run: () => Laws.Claim(
                claim: Subjects.StateSumSeparatesCanary,
                lawId: "presented.state-sum-separates-canary"
            )
        ),

    ];
    private static LawCase[] Phase3SecondKernelCases() => [
        // ---- phase 3: the declared second kernel (O1) ----
        //
        // Elementary-divisor reduction is not a convolution and cannot be a presentation, so it is carried openly as a
        // second kernel and made to prove itself: the triple IS the certificate. Every hand-checkable form and every
        // swept draw is re-multiplied here and answered to by the classical gcd-of-minors invariants, which run no step
        // the reduction runs.
        Case(
            id: "presented.smith-certificate-remultiplies",
            run: () => {
                Laws.Claim(
                    claim: Subjects.SmithKnownForms,
                    lawId: "presented.smith-certificate-remultiplies"
                );
                Laws.SweptClaim(
                    lawId: "presented.smith-certificate-remultiplies",
                    domain: Presented,
                    tier: Tier.Default,
                    width: 9,
                    claim: Subjects.SmithCertificateRemultiplies()
                );
            }
        ),

        // The bound, kept honest rather than asserted: a ten-by-ten matrix of single-digit entries drives intermediate
        // coefficients into the kilobits, the ceiling refuses that reduction where it is set low and answers the same
        // matrix where it is set high, and the smallest-pivot rule is MEASURED against a first-nonzero foil.
        Case(
            id: "presented.smith-growth-refuses",
            run: () => Laws.Claim(
                claim: Subjects.SmithGrowthBounded,
                lawId: "presented.smith-growth-refuses"
            )
        ),

        // The re-multiplication the second kernel owed at scale: the swept case is three-by-three and square, so square
        // orders through eight, both rectangular orientations and one wide draw are proved here, each re-multiplied and
        // inverted both ways in this file. The one matrix whose answer is a classical fact rather than a recomputation
        // is the reflection lattice's own Cartan matrix, built from the group slice's MEASURED bond diagram: its
        // determinant is one, so its elementary divisors are eight ones and nothing else.
        Case(
            id: "presented.smith-remultiplies-at-scale",
            run: () => Laws.Claim(
                claim: Subjects.SmithRemultipliesAtScale,
                lawId: "presented.smith-remultiplies-at-scale"
            )
        ),

        // The two consumers the obstruction promised. The elementary divisors ARE the integral torsion coefficients, so
        // the smallest complex carrying torsion is the oracle; and Betti numbers over a field material are the echelon
        // path already in the tree, so they needed no new code at all. The two disagree only where the torsion meets
        // the characteristic, which is measured with a mod-two sweep rather than described.
        // A dimension is a LABEL, and the graded tables are sized by the largest one rather than by the cell count, so
        // a one-cell complex labelled a billion asked for roughly 12 GB and int.MaxValue overflowed the top. Both
        // halves are stated: an oversized label is refused, and the widest grading the 84-cell cap allows is admitted
        // whole, so the bound is reachable rather than a wall.
        Case(
            id: "presented.cell-dimension-bounded-by-cells",
            run: () => Laws.Claim(
                claim: Subjects.CellDimensionBoundHolds,
                lawId: "presented.cell-dimension-bounded-by-cells"
            )
        ),

        Case(
            id: "presented.homology-torsion-and-betti",
            run: () => {
                Laws.Claim(
                    claim: Subjects.HomologyTorsionAndBetti,
                    lawId: "presented.homology-torsion-and-betti"
                );
                Laws.Claim(
                    claim: OracleClaims.NonChainHomologyRefuses,
                    lawId: "presented.homology-torsion-and-betti"
                );
            }
        ),

        Case(
            id: "presented.gf2-twins-binaryfield",
            run: () => {
                Laws.VectorTwin(
                    lawId: "presented.gf2-twins-binaryfield",
                    domain: Presented,
                    tier: Tier.Default,
                    width: 8,
                    first: Subjects.PresentedBinaryFieldMultiply(
                        degree: 8,
                        reductionTail: 0x1BUL
                    ),
                    second: Subjects.BinaryFieldMultiply8,
                    witness: null
                );
                Laws.VectorTwin(
                    lawId: "presented.gf2-twins-binaryfield",
                    domain: Presented,
                    tier: Tier.Default,
                    width: 16,
                    first: Subjects.PresentedBinaryFieldMultiply(
                        degree: 16,
                        reductionTail: 0x2BUL
                    ),
                    second: Subjects.BinaryFieldMultiply16,
                    witness: Subjects.BinaryFieldProductOracle(
                        degree: 16,
                        reductionTail: 0x2BUL
                    )
                );
                Laws.VectorMatchesOracle(
                    lawId: "presented.gf2-twins-binaryfield",
                    domain: Presented,
                    tier: Tier.Default,
                    width: 8,
                    subject: Subjects.PresentedBinaryFieldMultiply(
                        degree: 8,
                        reductionTail: 0x1BUL
                    ),
                    oracle: Subjects.BinaryFieldProductOracle(
                        degree: 8,
                        reductionTail: 0x1BUL
                    )
                );
            }
        ),

        Case(
            id: "presented.quadratic-twin-algebra-integer-lane",
            run: () => Laws.VectorTwin(
                lawId: "presented.quadratic-twin-algebra-integer-lane",
                domain: Presented,
                tier: Tier.Default,
                width: 2,
                first: Subjects.PresentedQuadraticMultiply(
                    pRaw: OneRaw,
                    qRaw: OneRaw
                ),
                second: Subjects.QuadraticMultiplyLanes(
                    pRaw: OneRaw,
                    qRaw: OneRaw
                ),
                witness: Subjects.QuadraticMultiplyLanesOracle(
                    pRaw: OneRaw,
                    qRaw: OneRaw
                )
            )
        ),
        Case(
            id: "presented.quadratic-twin-algebra-fractional-lane",
            run: () => Laws.VectorTwin(
                lawId: "presented.quadratic-twin-algebra-fractional-lane",
                domain: Presented,
                tier: Tier.Default,
                width: 2,
                first: Subjects.PresentedQuadraticMultiply(
                    pRaw: 0L,
                    qRaw: HalfQ
                ),
                second: Subjects.QuadraticMultiplyLanes(
                    pRaw: 0L,
                    qRaw: HalfQ
                ),
                witness: Subjects.QuadraticMultiplyLanesOracle(
                    pRaw: 0L,
                    qRaw: HalfQ
                )
            )
        ),

        Case(
            id: "presented.power-twins-companion",
            run: () => Laws.TwinPower(
                lawId: "presented.power-twins-companion",
                domain: Presented,
                tier: Tier.Default,
                first: Subjects.PresentedRootPower(),
                second: Subjects.CompanionRootPower,
                witness: Subjects.CompanionRootPowerOracle
            )
        ),

        // ONE quiver presentation at three materials — reachable, shortest, and how many — with no second kernel.
        Case(
            id: "presented.tropical-star-vs-oracle",
            run: () => Laws.VectorMatchesOracle(
                lawId: "presented.tropical-star-vs-oracle",
                domain: Presented,
                tier: Tier.Default,
                width: (Subjects.GraphOrder * Subjects.GraphOrder),
                subject: Subjects.PresentedTropicalStar(),
                oracle: Subjects.TropicalStarOracle
            )
        ),
        Case(
            id: "presented.counting-power-vs-oracle",
            run: () => Laws.VectorMatchesOracle(
                lawId: "presented.counting-power-vs-oracle",
                domain: Presented,
                tier: Tier.Default,
                width: (Subjects.GraphOrder * Subjects.GraphOrder),
                subject: Subjects.PresentedWalkCount(length: 5),
                oracle: Subjects.WalkCountOracle(length: 5)
            )
        ),

    ];
    private static LawCase[] UnitIntervalMaterialFamilyCases() => [
        // ---- the unit interval as a material family: the SAME quiver presentation at three more materials ----
        //
        // Three more questions about one graph — the most probable route, the widest bottleneck, and the route whose
        // steps' shortfalls from certainty still sum to under one — and not one line of new kernel between them. Each
        // oracle walks the graph a different way from the star: two enumerate simple paths and one runs a max-min triple
        // loop, so none of them forms a power at all.
        Case(
            id: "presented.most-likely-path-vs-oracle",
            run: () => Laws.VectorMatchesOracle(
                lawId: "presented.most-likely-path-vs-oracle",
                domain: Presented,
                tier: Tier.Default,
                width: (Subjects.GraphOrder * Subjects.GraphOrder),
                subject: Subjects.PresentedMostLikelyPathStar(),
                oracle: Subjects.MostLikelyPathStarOracle
            )
        ),
        Case(
            id: "presented.fuzzy-closure-vs-max-min",
            run: () => Laws.VectorMatchesOracle(
                lawId: "presented.fuzzy-closure-vs-max-min",
                domain: Presented,
                tier: Tier.Default,
                width: (Subjects.GraphOrder * Subjects.GraphOrder),
                subject: Subjects.PresentedFuzzyStar(),
                oracle: Subjects.FuzzyStarOracle
            )
        ),
        Case(
            id: "presented.bounded-sum-route-vs-oracle",
            run: () => Laws.VectorMatchesOracle(
                lawId: "presented.bounded-sum-route-vs-oracle",
                domain: Presented,
                tier: Tier.Default,
                width: (Subjects.GraphOrder * Subjects.GraphOrder),
                subject: Subjects.PresentedBoundedSumStar(),
                oracle: Subjects.BoundedSumStarOracle
            )
        ),

        // THE BOOLEAN SUBLATTICE TWIN. Confine the three materials to the two endpoints and all three collapse onto the
        // Boolean material exactly: the maximum is disjunction, and the rounded product, the minimum and the bounded sum
        // are all conjunction there. It is the statement that the family EXTENDS the Boolean answer rather than
        // approximating it, and it is what a rounding defect at an endpoint would break first.
        Case(
            id: "presented.unit-interval-boolean-sublattice",
            run: () => Laws.VectorMatchesOracle(
                lawId: "presented.unit-interval-boolean-sublattice",
                domain: Presented,
                tier: Tier.Default,
                width: (Subjects.GraphOrder * Subjects.GraphOrder),
                subject: Subjects.PresentedUnitIntervalBooleanSublattice(),
                oracle: Subjects.BooleanStarOracle
            )
        ),

        // THE POWER-OF-TWO TWIN — the log-domain isomorphism as a law on the subfamily where BOTH sides are exact. Arc
        // weights at exact powers of two make every most-likely-path product a shift and every negated logarithm an
        // integer, so the likelihood and the tropical distance must name the same cost at every pair, and therefore the
        // same decision. The envelope is stated at the subject: exponents zero through seven over at most three arcs, so
        // a total never reaches the 32 fraction bits at which the likelihood would underflow while the cost stayed
        // finite.
        Case(
            id: "presented.unit-interval-power-of-two-twin",
            run: () => Laws.VectorTwin(
                lawId: "presented.unit-interval-power-of-two-twin",
                domain: Presented,
                tier: Tier.Default,
                width: (Subjects.GraphOrder * Subjects.GraphOrder),
                first: Subjects.PresentedMostLikelyPathPowerOfTwo(),
                second: Subjects.PresentedTropicalPowerOfTwo(),
                witness: null
            )
        ),

        // The three semirings against arbitrary width rather than against the carrier they are built on. Every other
        // statement here quantifies over GRAPHS, where a material's pairwise product is reached only through the fused
        // fold and a quiet change to it can hide; this one names the product, the addition, both identities, the zero
        // test and distributivity at every swept raw pair. It also carries the suite's ONLY absolute statement of the
        // fused term's single rounding — three interior factors against the triple-product oracle — because every other
        // fused fold here charges its terms with one, where the one-rounding and two-rounding disciplines coincide.
        Case(
            id: "presented.unit-interval-semirings-vs-oracle",
            run: () => Laws.SweptClaim(
                claim: Subjects.UnitIntervalSemiringsExact,
                domain: ClosedUnit,
                lawId: "presented.unit-interval-semirings-vs-oracle",
                tier: Tier.Default,
                width: 2
            )
        ),

        // The star licence, proved rather than inherited: the SHIPPED idempotent certificate carries all three closures
        // with no new certificate code, on a graph where the counting material refuses forever.
        Case(
            id: "presented.unit-interval-star-licensing",
            run: () => Laws.Claim(
                claim: Subjects.UnitIntervalStarLicensing,
                lawId: "presented.unit-interval-star-licensing"
            )
        ),

        Case(
            id: "presented.material-contract-boundaries",
            run: () => Laws.Claim(
                claim: Subjects.OracleMaterialContractBoundaries,
                lawId: "presented.material-contract-boundaries"
            )
        ),

        Case(
            id: "presented.finite-basis-outcome-is-typed",
            run: () => Laws.Claim(
                claim: OracleClaims.FiniteBasisCapacityIsTyped,
                lawId: "presented.finite-basis-outcome-is-typed"
            )
        ),

        // The first complement beyond Boolean. The pattern lens's complement was a two-valued surface because only one
        // material carried a De Morgan involution; the fuzzy material carries the exact one minus x, so a complemented
        // pattern is GRADED — the same spans at the complementary weights — and the lens needed no new code to say so.
        Case(
            id: "presented.fuzzy-complement-lens",
            run: () => Laws.Claim(
                claim: Subjects.FuzzyComplementLens,
                lawId: "presented.fuzzy-complement-lens"
            )
        ),

        // The canary. Every other law here says two things agree; this one says the fused accumulate is load-bearing by
        // requiring it to DISAGREE with the per-term-rounding discipline on a floor of the swept operands. Measured over
        // five consecutive frontier windows the two diverge on 241 to 247 of the 504 cases — the single-lane edge
        // battery contributes none of them, every product there having one term — so the floor sits a quarter below the
        // observed minimum: strong enough to fail outright if the fused path were quietly rounding per term, loose
        // enough that a fresh operand window cannot trip it.
        Case(
            id: "presented.fused-vs-per-product-diverges",
            run: () => Laws.DivergenceCanary(
                lawId: "presented.fused-vs-per-product-diverges",
                domain: Presented,
                tier: Tier.Default,
                width: 8,
                fused: Subjects.PresentedCliffordMultiply(
                    degenerateCount: 0,
                    negativeCount: 0,
                    positiveCount: 3
                ),
                perProduct: Subjects.CliffordPerProductOracle(
                    degenerateCount: 0,
                    negativeCount: 0,
                    positiveCount: 3
                ),
                minimumDivergences: 180
            )
        ),

        // The unit interval's own canary, at the ONE material of the family whose product rounds. A term of a fused sum
        // is a charge times two coefficients, and the material takes that product exactly before rounding it once; the
        // alternative — round the pair, then round against the charge — is what a material without a three-factor
        // product is forced into, so the three-factor member earns its place only if the two DISAGREE. Measured over
        // five consecutive frontier windows they diverge on 57, 73, 77, 82 and 94 of the 466 swept cases, so the floor
        // sits at two thirds of the observed minimum: loose enough that a fresh operand window cannot trip it. What a
        // divergence canary CANNOT do is fail when the fold starts rounding twice — both of its sides would then be
        // two-rounding disciplines that still disagree with each other — so the absolute gate on that is the fused-term
        // leg of presented.unit-interval-semirings-vs-oracle, which states it in BigInteger. The rate
        // is well under half because 144 of the 466 cases are the single-lane edge battery, where the vector domain
        // zeroes every lane but one: one whole factor vector is then identically zero and both disciplines return zero,
        // annihilated rather than exact. The divergences come from the all-lane, random and frontier draws, which is
        // where interior operands live.
        Case(
            id: "presented.unit-interval-fused-vs-per-term-diverges",
            run: () => Laws.DivergenceCanary(
                domain: Presented,
                fused: Subjects.UnitIntervalFusedTerms,
                lawId: "presented.unit-interval-fused-vs-per-term-diverges",
                minimumDivergences: 38,
                perProduct: Subjects.UnitIntervalPerTermRounding,
                tier: Tier.Default,
                width: 6
            )
        ),

        // The material contract every fused kernel rests on, at every material in the set.
        Case(
            id: "presented.material-fused-identities",
            run: () => Laws.SweptClaim(
                claim: Subjects.MaterialFusedIdentities,
                domain: Presented,
                lawId: "presented.material-fused-identities",
                tier: Tier.Default,
                width: 6
            )
        ),

    ];
    private static LawCase[] Phase2ModulesCases() => [
        // ---- phase 2: modules by presentation morphism ----
        //
        // A module is a state, a step and a readout — the stepper framing, which is this object's module theory rather
        // than a second kernel. Every case below is the SAME product at another presentation, and every cross-check is
        // either a shipped kernel or a shared-nothing oracle.

        // The zero-allocation overload, at both a signature with a degenerate generator and one without.
        Case(
            id: "presented.multiply-into-twins-multiply",
            run: () => {
                Laws.VectorTwin(
                    lawId: "presented.multiply-into-twins-multiply",
                    domain: Presented,
                    tier: Tier.Default,
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
                );
                Laws.VectorTwin(
                    lawId: "presented.multiply-into-twins-multiply",
                    domain: Presented,
                    tier: Tier.Default,
                    width: 16,
                    first: Subjects.PresentedCliffordMultiplyInto(
                        degenerateCount: 1,
                        negativeCount: 0,
                        positiveCount: 3
                    ),
                    second: Subjects.PresentedCliffordMultiply(
                        degenerateCount: 1,
                        negativeCount: 0,
                        positiveCount: 3
                    ),
                    witness: null
                );
            }
        ),

        // Ledger row 15: the residual at the identity twist is a derivation, and on the jet presentation its unit
        // coefficient IS FixedDual's chain-rule lift, bit for bit over the whole raw range. The claim beside it
        // separates the three twists and proves the twisted Leibniz rule where no relation can break it.
        Case(
            id: "presented.jet-residual-twins-dual",
            run: () => {
                Laws.TwinBinary(
                    lawId: "presented.jet-residual-twins-dual",
                    domain: Dual,
                    tier: Tier.Default,
                    first: Subjects.PresentedJetResidual(),
                    second: Subjects.DualChainRuleLift,
                    witness: Subjects.JetResidualOracle
                );
                Laws.Claim(
                    claim: Subjects.ResidualTwistsSeparate,
                    lawId: "presented.jet-residual-twins-dual"
                );
            }
        ),

        // The pair-up theorem on an exact material: the behavior of a tensor is the termwise product of behaviors.
        Case(
            id: "presented.tensor-behavior-vs-product",
            run: () => Laws.VectorTwin(
                lawId: "presented.tensor-behavior-vs-product",
                domain: Presented,
                tier: Tier.Default,
                width: Subjects.TensorLaneWidth,
                first: Subjects.PresentedTensorBehavior(),
                second: Subjects.TensorBehaviorProductOracle(),
                witness: Subjects.ExactTensorBehavior()
            )
        ),

        // THE PAIR-UP CANARY. The construction survives every material and the THEOREM does not: a tensor's cells are
        // not products of already-rounded cells, so over the house scalar the two sides of the row above must actually
        // DISAGREE. Measured over five consecutive frontier windows they diverge on 266 to 268 of the 466 swept cases —
        // the single-lane edge battery contributes almost none of them, its behaviors being zero on both sides — so the
        // floor sits a third below the observed minimum: strong enough to fail outright if pairing ever started
        // commuting with rounding, loose enough that a fresh operand window cannot trip it.
        Case(
            id: "presented.pair-up-rounds-canary",
            run: () => {
                Laws.DivergenceCanary(
                    lawId: "presented.pair-up-rounds-canary",
                    domain: Presented,
                    tier: Tier.Default,
                    width: Subjects.TensorLaneWidth,
                    fused: Subjects.PresentedFixedTensorBehavior(),
                    perProduct: Subjects.FixedTensorBehaviorProductOracle(),
                    minimumDivergences: 180
                );
                Laws.SweptClaim(
                    lawId: "presented.pair-up-rounds-canary",
                    domain: Presented,
                    tier: Tier.Default,
                    width: Subjects.TensorLaneWidth,
                    claim: Subjects.FixedTensorBehaviorProductIsExact()
                );
            }
        ),

        // Ledger row 18: Dirichlet convolution IS the product at a divisibility window, so mu is the guarded star of
        // the negated strict zeta and mu ⋆ zeta is the unit. Cross-checked against the shipped factorization and
        // prime-counting kernels, which share nothing with any convolution.
        Case(
            id: "presented.mobius-star-round-trip",
            run: () => Laws.Claim(
                claim: Subjects.MobiusStarRoundTrip,
                lawId: "presented.mobius-star-round-trip"
            )
        ),

        // Ledger rows 16 and 17: derivative matching at a finite alphabet, weighted and Boolean, against a
        // shared-nothing backtracking oracle over a pattern TREE — a construction the subject does not have at all.
        Case(
            id: "presented.matcher-vs-backtracking-oracle",
            run: () => Laws.Claim(
                claim: Subjects.MatcherMatchesBacktrackingOracle,
                lawId: "presented.matcher-vs-backtracking-oracle"
            )
        ),

        // The weight a scaled pattern gives a span, read back out. Every other pattern statement quantifies over
        // ELEMENTS — the Leibniz rule, the matcher against its oracle — so a Scale that ignored its weight argument
        // would return perfectly valid elements and leave all of them green. This one names the value, at a counting
        // material where the scale multiplies and at a tropical one where it adds, so nothing about it can be faked.
        // Only the members it genuinely drives are credited; the rest of the pattern surface has its own creditors.
        Case(
            id: "presented.pattern-scale-weights",
            run: () => Laws.Claim(
                claim: Subjects.PatternScaleWeights,
                lawId: "presented.pattern-scale-weights"
            )
        ),

        // The declared second axis (O2): a predicate algebra supplies conjunction, complement and satisfiability, one
        // shared loop cuts the partition, and the kernel receives a letter count and a mask — never a predicate.
        Case(
            id: "presented.alphabet-refinement-partitions",
            run: () => Laws.Claim(
                claim: Subjects.AlphabetRefinementPartitions,
                lawId: "presented.alphabet-refinement-partitions"
            )
        ),

        Case(
            id: "presented.matcher-binds-alphabet-identity",
            run: () => Laws.Claim(
                claim: OracleClaims.MatcherRejectsDifferentAlphabetIdentity,
                lawId: "presented.matcher-binds-alphabet-identity"
            )
        ),

        // Ledger row 20: exact machine equivalence by pairing radical, decided against brute word enumeration to the
        // Myhill bound, and the quotient proved canonical — same behavior, minimal dimension, idempotent.
        Case(
            id: "presented.machine-equivalence-vs-enumeration",
            run: () => Laws.Claim(
                claim: Subjects.MachineEquivalenceMatchesEnumeration,
                lawId: "presented.machine-equivalence-vs-enumeration"
            )
        ),

        // Ledger row 21: a substochastic chain's powers neither vanish nor stabilize, so the iterative star refuses
        // forever and the resolvent answers in one solve. The proof is re-multiplication, not a truncation.
        Case(
            id: "presented.resolvent-remultiplies",
            run: () => Laws.Claim(
                claim: Subjects.ResolventRemultiplies,
                lawId: "presented.resolvent-remultiplies"
            )
        ),

        // Ledger row 22: the antidifference is the guarded star of the shift on degree-bounded jets, and it reproduces
        // the shipped exactly-inverted prefix sums place for place.
        Case(
            id: "presented.antidifference-vs-layer-sequence",
            run: () => Laws.Claim(
                claim: Subjects.AntidifferenceMatchesLayerSequence,
                lawId: "presented.antidifference-vs-layer-sequence"
            )
        ),

        // Ledger row 7: uniform prime-power fields. Degree two against the shipped extension field, above it against a
        // schoolbook polynomial oracle, since nothing in the tree constructs those fields at all.
        Case(
            id: "presented.monogenic-twins-prime-extension",
            run: () => Laws.Claim(
                claim: Subjects.PrimeExtensionTwinsMonogenic,
                lawId: "presented.monogenic-twins-prime-extension"
            )
        ),

        // Ledger row 10: the companion quiver's product IS the projective step, so a matrix step through the shared
        // kernel reproduces MobiusStep over the whole raw range and ProjectiveStep above degree two.
        Case(
            id: "presented.companion-quiver-twins-mobius",
            run: () => {
                Laws.MobiusMatchesOracle(
                    lawId: "presented.companion-quiver-twins-mobius",
                    domain: Mobius,
                    tier: Tier.Default,
                    subject: Subjects.PresentedCompanionMobius(
                        pRaw: OneRaw,
                        qRaw: OneRaw
                    ),
                    oracleNumerator: Subjects.MobiusNumeratorOracle(
                        pRaw: OneRaw,
                        qRaw: OneRaw
                    )
                );
                Laws.MobiusMatchesOracle(
                    lawId: "presented.companion-quiver-twins-mobius",
                    domain: Fractional,
                    tier: Tier.Default,
                    subject: Subjects.PresentedCompanionMobius(
                        pRaw: 0L,
                        qRaw: HalfQ
                    ),
                    oracleNumerator: Subjects.MobiusNumeratorOracle(
                        pRaw: 0L,
                        qRaw: HalfQ
                    )
                );
                Laws.Claim(
                    claim: Subjects.CompanionQuiverTwinsProjectiveStep,
                    lawId: "presented.companion-quiver-twins-mobius"
                );
            }
        ),

        // Ledger row 11: orientation is the top-grade coefficient of a triple join, and the join is non-metric — no
        // signature enters it — so an independent determinant is the whole cross-check.
        Case(
            id: "presented.orientation-twins-determinant",
            run: () => Laws.Claim(
                claim: Subjects.OrientationTwinsDeterminant,
                lawId: "presented.orientation-twins-determinant"
            )
        ),

        Case(
            id: "presented.complement-admission-proves-inverses",
            run: () => Laws.Claim(
                claim: OracleClaims.ComplementAdmissionRequiresMutualInverses,
                lawId: "presented.complement-admission-proves-inverses"
            )
        ),

        // Ledger row 19: a continued fraction is a word run through the codiscrete quiver on two objects, which IS the
        // two-by-two matrix algebra, so the convergent recurrence needs no transfer-matrix code of its own.
        Case(
            id: "presented.transfer-twins-convergents",
            run: () => Laws.Claim(
                claim: Subjects.TransferTwinsConvergents,
                lawId: "presented.transfer-twins-convergents"
            )
        ),

    ];
    private static LawCase[] ContinuedFractionLensCases() => [
        // ---- the continued-fraction lenses: one certificate, one tiling, two readings each ----

        // The equidistribution certificate is not a measured statistic but the largest partial quotient of the
        // generator's continued fraction, so the oracle is that fraction walked independently in BigInteger.
        Case(
            id: "certified.certificate-vs-partial-quotients",
            run: () => Laws.Claim(
                claim: Subjects.CertificateMatchesPartialQuotients,
                lawId: "certified.certificate-vs-partial-quotients"
            )
        ),

        // Ring-coordinate random access and the streamed substitution are two implementations of ONE tiling: the walk
        // inverts and steps by a tile vector, Contains equals the walked vertex set over a covered box, and the walk
        // word is a factor of the streamed word.
        Case(
            id: "quasicrystal.chain-walk-vs-streamed-word",
            run: () => Laws.Claim(
                claim: Subjects.ChainWalkMatchesStreamedWord,
                lawId: "quasicrystal.chain-walk-vs-streamed-word"
            )
        ),

        // Quantization certificates: the nearest grid value, the exact first index where a rounded slope's Beatty
        // floors betray the true ones, the Stern-Brocot minimal fraction behind it, and the convergents as the
        // closest-approach record indices.
        Case(
            id: "quantization.quantize-nearest-vs-cleared-comparison",
            run: () => Laws.Claim(
                claim: QuantizationClaims.QuantizeNearestMatchesIntegerOracle,
                lawId: "quantization.quantize-nearest-vs-cleared-comparison"
            )
        ),
        Case(
            id: "quantization.first-divergence-vs-brute-force",
            run: () => Laws.Claim(
                claim: QuantizationClaims.FirstDivergenceMatchesBruteForce,
                lawId: "quantization.first-divergence-vs-brute-force"
            )
        ),
        Case(
            id: "quantization.simplest-rational-minimality",
            run: () => Laws.Claim(
                claim: QuantizationClaims.SimplestRationalIsMinimalInInterval,
                lawId: "quantization.simplest-rational-minimality"
            )
        ),
        Case(
            id: "quantization.convergents-are-closest-approach-records",
            run: () => Laws.Claim(
                claim: QuantizationClaims.ConvergentsAreClosestApproachRecords,
                lawId: "quantization.convergents-are-closest-approach-records"
            )
        ),

        // Public automatic-sequence infrastructure and the first verified radical shadow-tower slice: canonical
        // positional/Ostrowski addressing, identical BigInteger/ulong reads, an opaque verifier boundary, and the
        // exact-affine family whose correction language is identically zero.
        Case(
            id: "core.automatic-sequence-and-radical-shadow-exactness",
            run: () => Laws.Claim(
                claim: AutomaticSequenceClaims.NumerationAutomatonAndRadicalTowerAreExact,
                lawId: "core.automatic-sequence-and-radical-shadow-exactness"
            )
        ),

    ];
    private static LawCase[] FixedVectorCases() => [
        // ---- the fixed-point vectors: FixedVector2 (the plane) and FixedVector3 (the space) ----
        //
        // All four fused kernels route through FixedQ4816.RoundProductSum after an OR-gated lane choice: below the gate
        // a plain long accumulator, above it an Int128 one. Both lanes implement the SAME contract — one ties-to-even
        // rounding of the exact product sum at shift sixteen, wrapped to the carrier — and that is proved rather than
        // assumed. Under the gate every product magnitude is below 2^(2k) and the long sum cannot overflow; above it
        // the Int128 sum CAN wrap, but a wrap moves the exact sum by k·2¹²⁸ and hence the rounded value by k·2¹¹²,
        // which is zero modulo 2⁶⁴ and cannot move tie parity. So the oracle is derived from the EXACT sum in every
        // case, and the two lanes are swept by two cases against the one reference.
        Case(
            id: "vector.plane-products-vs-oracle",
            run: () => Laws.BinaryMatchesOracle(
                domain: Vector,
                lawId: "vector.plane-products-vs-oracle",
                oracle: Subjects.PlaneProductsOracle,
                subject: Subjects.PlaneProducts,
                tier: Tier.Default
            )
        ),
        Case(
            id: "vector.space-products-vs-oracle",
            run: () => {
                Laws.VectorMatchesOracle(
                    domain: Vector,
                    lawId: "vector.space-products-vs-oracle",
                    oracle: Subjects.SpaceDotLanesOracle,
                    subject: Subjects.SpaceDotLanes,
                    tier: Tier.Default,
                    width: 3
                );
                Laws.VectorMatchesOracle(
                    domain: Vector,
                    lawId: "vector.space-products-vs-oracle",
                    oracle: Subjects.SpaceCrossLanesOracle,
                    subject: Subjects.SpaceCrossLanes,
                    tier: Tier.Default,
                    width: 3
                );
            }
        ),
        // The lane the full-range cases cannot reach: a sixty-four-bit draw lands below 2³¹ once in 2³² draws, so
        // without a fold the narrow branch would be pinned by the edge battery alone. Subject and oracle apply the
        // IDENTICAL fold, so every sampled operand reaches a defined comparison rather than being skipped
        // asymmetrically.
        Case(
            id: "vector.narrow-lane-vs-oracle",
            run: () => {
                Laws.BinaryMatchesOracle(
                    domain: VectorNarrow,
                    lawId: "vector.narrow-lane-vs-oracle",
                    oracle: Subjects.NarrowPlaneProductsOracle,
                    subject: Subjects.NarrowPlaneProducts,
                    tier: Tier.Default
                );
                Laws.VectorMatchesOracle(
                    domain: VectorNarrow,
                    lawId: "vector.narrow-lane-vs-oracle",
                    oracle: Subjects.NarrowSpaceDotLanesOracle,
                    subject: Subjects.NarrowSpaceDotLanes,
                    tier: Tier.Default,
                    width: 3
                );
                Laws.VectorMatchesOracle(
                    domain: VectorNarrow,
                    lawId: "vector.narrow-lane-vs-oracle",
                    oracle: Subjects.NarrowSpaceCrossLanesOracle,
                    subject: Subjects.NarrowSpaceCrossLanes,
                    tier: Tier.Default,
                    width: 3
                );
            }
        ),
        Case(
            id: "vector.componentwise-vs-oracle",
            run: () => Laws.SweptClaim(
                claim: Subjects.VectorComponentwiseMatchesOracle,
                domain: Vector,
                lawId: "vector.componentwise-vs-oracle",
                tier: Tier.Default,
                width: 4
            )
        ),
        // Each swept case is evaluated TWICE — once on the full-range operands, which mostly drive the refusal path,
        // and once on narrow-folded ones, which mostly drive the success path — so both branches of both norms are
        // covered at every draw rather than only where the sampler happens to land.
        Case(
            id: "vector.norm-vs-oracle",
            run: () => Laws.SweptClaim(
                claim: Subjects.VectorNormMatchesOracle,
                domain: VectorNorm,
                lawId: "vector.norm-vs-oracle",
                tier: Tier.Default,
                width: 3
            )
        ),
        // The family's cross-type seam: the plane embeds in the space EXACTLY. Doc-anchored — FixedVector2.Wedge's
        // remark calls itself the planar restriction of FixedVector3.Cross, and Cross's returns clause points back at
        // it. The two gates coincide (Wedge ORs four magnitudes at 2³¹, Cross ORs six at 2³¹ and the two embedded Z
        // lanes contribute zero), so the wedge embedding is exact at FULL range rather than on a sublattice; the dot
        // embedding crosses lanes (2³¹ against 2³⁰) and is still exact, which is a statement worth making on its own.
        Case(
            id: "vector.kinship-exact",
            run: () => {
                Laws.VectorTwin(
                    domain: Vector,
                    first: Subjects.PlaneWedgeAndDotLanes,
                    lawId: "vector.kinship-exact",
                    second: Subjects.SpaceEmbeddedWedgeAndDotLanes,
                    tier: Tier.Default,
                    width: 2,
                    witness: Subjects.PlaneWedgeAndDotOracleLanes
                );
                Laws.SweptClaim(
                    claim: Subjects.VectorPlaneInSpaceExact,
                    domain: Vector,
                    lawId: "vector.kinship-exact",
                    tier: Tier.Default,
                    width: 2
                );
            }
        ),
        // Every operand is m·2¹⁶ with |m| ≤ 4092, so every product, sum and difference below is EXACT in Q16 and the
        // identities are equalities of integers rather than approximations. The magnitude audit that makes them
        // unconditional: a cross lane stays under 2⁴¹, a nested cross and a scaled dot under 2⁵⁴, and the Jacobi sum
        // under 3·2⁵⁴ — all inside the signed carrier, so nothing wraps. Note the INNER cross runs on the narrow lane
        // and the OUTER one on the Int128 lane, so a single exact identity exercises both accumulators.
        Case(
            id: "vector.exact-algebra-on-the-sublattice",
            run: () => Laws.SweptClaim(
                claim: Subjects.VectorExactAlgebra,
                domain: VectorLattice,
                lawId: "vector.exact-algebra-on-the-sublattice",
                tier: Tier.Default,
                width: 6
            )
        ),
        Case(
            id: "vector.identity-and-negation",
            run: () => Laws.SweptClaim(
                claim: Subjects.VectorIdentityAndNegation,
                domain: Vector,
                lawId: "vector.identity-and-negation",
                tier: Tier.Default,
                width: 3
            )
        ),
        Case(
            id: "vector.construction-and-refusals",
            run: () => Laws.Claim(
                claim: Subjects.VectorConstructionAndRefusals,
                lawId: "vector.construction-and-refusals"
            )
        ),
        // Normalization is a THREE-STAGE pipeline: a common power-of-two precondition at leading bit forty-five, a
        // Q16-scaled nearest root as the one common denominator, and one ties-to-even ratio per component. Its distance
        // from the ideal single-rounding unit vector is PROVED rather than measured — the precondition perturbs the
        // exact ratio by at most 2⁻⁴⁶ where it is a left shift and at most 2⁻²⁸ where it is a rounding right shift, and
        // both are far below a half, so the two disciplines can part only at a ratio within 2⁻²⁸ of a half-integer and
        // then by exactly one raw.
        Case(
            id: "vector.normalize-vs-ideal-and-staged",
            run: () => Laws.SweptClaim(
                claim: Subjects.VectorNormalizeMatchesOracles,
                domain: VectorDirection,
                lawId: "vector.normalize-vs-ideal-and-staged",
                tier: Tier.Default,
                width: 3
            )
        ),
        Case(
            id: "vector.orthonormal-basis-is-orthogonal-and-deterministic",
            run: () => Laws.SweptClaim(
                claim: Subjects.VectorOrthonormalBasisIsOrthogonalAndDeterministic,
                domain: VectorOrthonormalBasis,
                lawId: "vector.orthonormal-basis-is-orthogonal-and-deterministic",
                tier: Tier.Default,
                width: 3
            )
        ),
        Case(
            id: "vector.orthonormal-basis-tracks-non-unit-magnitude",
            run: () => Laws.Claim(
                claim: Subjects.VectorOrthonormalBasisTracksNonUnitMagnitude,
                lawId: "vector.orthonormal-basis-tracks-non-unit-magnitude"
            )
        ),

        // The other member quaternion.exp-log-seam declared uncovered. Its DIRECTION output turned out to have indirect
        // coverage — transposing a lane reddens the exp/log seams — but its MAGNITUDE output had none, and that is the
        // half stated here as an exact identity rather than a tolerance: the returned raw magnitude IS the nearest
        // integer root of the exact BigInteger squared sum, which is stronger than any ULP bound a float reference
        // could set.
        Case(
            id: "vector.normalize-with-magnitude-full-unsigned-width",
            run: () => Laws.Claim(
                claim: TransformKernelClaims.NormalizeWithMagnitudeFullUnsignedWidthSurface,
                lawId: "vector.normalize-with-magnitude-full-unsigned-width"
            )
        ),
        Case(
            id: "vector.normalize-ideal-bound-full-width-sweep",
            run: () => Laws.Claim(
                claim: TransformKernelClaims.NormalizeIdealBoundWidthSweepSurface,
                lawId: "vector.normalize-ideal-bound-full-width-sweep"
            )
        ),
        Case(
            id: "vector.presentation-ladder",
            run: () => Laws.Claim(
                claim: Subjects.VectorPresentationMatchesLadder,
                lawId: "vector.presentation-ladder"
            )
        ),
        // The inbound seam, the mirror of the row above. Its own ladder, not a round trip through that one:
        // ToVector3 is lossy, so a round trip would pin only the rows that survive it and would silently stop
        // discriminating exactly where the narrowing is interesting.
        Case(
            id: "vector.adoption-ladder",
            run: () => Laws.Claim(
                claim: Subjects.VectorAdoptionMatchesLadder,
                lawId: "vector.adoption-ladder"
            )
        ),
        Case(
            id: "vector.record-print-is-components-only",
            run: () => Laws.Claim(
                claim: Subjects.VectorRecordPrintsComponentsOnly,
                lawId: "vector.record-print-is-components-only"
            )
        ),
        // The canary. A fused kernel that quietly rounded each product before summing would satisfy every algebraic
        // identity above, both antisymmetries and every identity-element statement; only a floor catches it. Run on the
        // narrow fold, where nothing saturates and every operand is strictly inside the accumulator's own bound, so a
        // divergence is the ROUNDING and never an overflow. Measured over five consecutive frontier windows the two
        // diverge on 100, 108, 113, 116 and 124 of the 437 swept cases, so the floor sits a quarter below the observed
        // minimum: strong enough to fail outright if the fused path were quietly rounding per term, loose enough that a
        // fresh operand window cannot trip it. The rate is well under half because 72 of the 437 cases are the
        // single-lane edge battery, where one whole operand vector is zero and both disciplines return zero, and
        // because the committed edge set is mostly powers of two whose narrow folds are small integers: their products
        // carry no low sixteen bits at all, so nothing rounds on either side. The divergences come from the all-lane,
        // random and frontier draws, which is where interior operands live.
        Case(
            id: "vector.fused-products-diverge-from-per-product",
            run: () => Laws.DivergenceCanary(
                domain: VectorNarrow,
                fused: Subjects.NarrowFusedProductLanes,
                lawId: "vector.fused-products-diverge-from-per-product",
                minimumDivergences: 75,
                perProduct: Subjects.NarrowPerProductLanes,
                tier: Tier.Default,
                width: 3
            )
        ),
        // The norm's own canary, on the same fold and for the same reason: one rounding of the exact sum of squares has
        // to be observably different from rounding each square first. Over thirty frontier windows the two disciplines
        // diverge on 86 to 121 of the 437 swept cases, median 112, so the floor again sits a quarter below the observed
        // minimum. Calibrate a divergence floor against a sample wide enough to reach the distribution's tail: a red
        // run does not advance the frontier, so a floor above the tail parks the sweep on the first window to hit it.
        Case(
            id: "vector.fused-norm-diverges-from-per-square",
            run: () => Laws.DivergenceCanary(
                domain: VectorNarrow,
                fused: Subjects.NarrowFusedSquaredNormLanes,
                lawId: "vector.fused-norm-diverges-from-per-square",
                minimumDivergences: 64,
                perProduct: Subjects.NarrowPerSquareLanes,
                tier: Tier.Default,
                width: 3
            )
        ),

    ];
    private static LawCase[] FixedPositionCases() => [
        // ---- FixedPosition: the hierarchical world coordinate, an EXACT type throughout ----
        Case(
            id: "position.canonical-vs-oracle",
            run: () => Laws.SweptClaim(
                claim: Subjects.PositionCanonicalExact,
                domain: Position,
                lawId: "position.canonical-vs-oracle",
                tier: Tier.Default,
                width: 4
            )
        ),
        Case(
            id: "position.delta-vs-oracle",
            run: () => Laws.SweptClaim(
                claim: Subjects.PositionDeltaExact,
                domain: PositionDelta,
                lawId: "position.delta-vs-oracle",
                tier: Tier.Default,
                width: 6
            )
        ),
        Case(
            id: "position.translate-vs-oracle",
            run: () => Laws.SweptClaim(
                claim: Subjects.PositionTranslateExact,
                domain: PositionTranslate,
                lawId: "position.translate-vs-oracle",
                tier: Tier.Default,
                width: 6
            )
        ),
        Case(
            id: "position.group-structure-exact",
            run: () => Laws.SweptClaim(
                claim: Subjects.PositionGroupStructureExact,
                domain: PositionTranslate,
                lawId: "position.group-structure-exact",
                tier: Tier.Default,
                width: 6
            )
        ),
        Case(
            id: "position.render-relative-ladder",
            run: () => Laws.Claim(
                claim: Subjects.PositionRenderRelativeLadder,
                lawId: "position.render-relative-ladder"
            )
        ),
        Case(
            id: "position.print-members-invariant-cells",
            run: () => Laws.Claim(
                claim: Subjects.PositionPrintsInvariantCells,
                lawId: "position.print-members-invariant-cells"
            )
        ),

    ];
    private static LawCase[] FixedRigidTransformCases() => [
        // ---- FixedRigidTransform: the unit dual quaternion ----
        Case(
            id: "rigid.compose-vs-oracle",
            run: () => Laws.VectorMatchesOracle(
                domain: Rigid,
                lawId: "rigid.compose-vs-oracle",
                oracle: Subjects.RigidComposeOracle,
                subject: Subjects.RigidComposeLanes,
                tier: Tier.Default,
                width: 8
            )
        ),
        Case(
            id: "rigid.identity-and-inverse-exact",
            run: () => Laws.SweptClaim(
                claim: Subjects.RigidIdentityAndInverseExact,
                domain: Rigid,
                lawId: "rigid.identity-and-inverse-exact",
                tier: Tier.Default,
                width: 8
            )
        ),
        Case(
            id: "rigid.translation-vs-oracle",
            run: () => Laws.SweptClaim(
                claim: Subjects.RigidTranslationExact,
                domain: Rigid,
                lawId: "rigid.translation-vs-oracle",
                tier: Tier.Default,
                width: 8
            )
        ),
        Case(
            id: "rigid.from-rotation-translation",
            run: () => Laws.SweptClaim(
                claim: Subjects.RigidFromRotationTranslation,
                domain: Rigid,
                lawId: "rigid.from-rotation-translation",
                tier: Tier.Default,
                width: 8
            )
        ),
        Case(
            id: "rigid.normalize-unit-constraints",
            run: () => Laws.SweptClaim(
                claim: Subjects.RigidNormalizeUnitConstraints,
                domain: RigidDirection,
                lawId: "rigid.normalize-unit-constraints",
                tier: Tier.Default,
                width: 8
            )
        ),
        Case(
            id: "rigid.from-dual-quaternion-refusals",
            run: () => Laws.Claim(
                claim: Subjects.RigidFromDualQuaternionRefusals,
                lawId: "rigid.from-dual-quaternion-refusals"
            )
        ),
        Case(
            id: "rigid.compose-normalized-twin",
            run: () => Laws.SweptClaim(
                claim: Subjects.RigidComposeNormalizedTwin,
                domain: Rigid,
                lawId: "rigid.compose-normalized-twin",
                tier: Tier.Default,
                width: 8
            )
        ),
        Case(
            id: "rigid.transform-point-vs-oracle",
            run: () => Laws.SweptClaim(
                claim: Subjects.RigidTransformPointExact,
                domain: RigidPoint,
                lawId: "rigid.transform-point-vs-oracle",
                tier: Tier.Default,
                width: 8
            )
        ),
        Case(
            id: "rigid.exp-log-seam",
            run: () => Laws.Claim(
                claim: Subjects.RigidExpLogSeam,
                lawId: "rigid.exp-log-seam"
            )
        ),
        Case(
            id: "rigid.sclerp-endpoints-and-screw",
            run: () => Laws.Claim(
                claim: Subjects.RigidScLerpEndpointsAndScrew,
                lawId: "rigid.sclerp-endpoints-and-screw"
            )
        ),

    ];
    private static LawCase[] RateAccumulatorCases() => [
        // ---- FixedRateAccumulator and FixedVector3RateAccumulator: an EXACT rational ledger ----
        Case(
            id: "rate.schedule-vs-ledger",
            run: () => Laws.SweptClaim(
                claim: Subjects.RateScheduleVsLedger,
                domain: Rate,
                lawId: "rate.schedule-vs-ledger",
                tier: Tier.Default,
                width: 4
            )
        ),
        Case(
            id: "rate.construction-and-refusals",
            run: () => Laws.Claim(
                claim: Subjects.RateConstructionAndRefusals,
                lawId: "rate.construction-and-refusals"
            )
        ),
        Case(
            id: "rate.unit-advance-exact",
            run: () => Laws.SweptClaim(
                claim: Subjects.RateUnitAdvanceExact,
                domain: Rate,
                lawId: "rate.unit-advance-exact",
                tier: Tier.Default,
                width: 2
            )
        ),
        Case(
            id: "rate.vector-axes-independent",
            run: () => Laws.SweptClaim(
                claim: Subjects.RateVectorAxesIndependent,
                domain: Rate,
                lawId: "rate.vector-axes-independent",
                tier: Tier.Default,
                width: 6
            )
        ),
        Case(
            id: "rate.vector-construction-and-refusals",
            run: () => Laws.Claim(
                claim: Subjects.RateVectorConstructionAndRefusals,
                lawId: "rate.vector-construction-and-refusals"
            )
        ),

    ];
    private static LawCase[] SymmetricSolveCases() => [
        // ---- FixedSymmetricSolve: scale-free 2×2/3×3 symmetric solve and invert (internal — see the type's own
        // remarks for the bit budget and the Invert-only refusal envelope) ----
        Case(
            id: "symmetric-solve.solve2-vs-oracle",
            run: () => Laws.SweptClaim(
                claim: SymmetricSolveClaims.Solve2VsOracle,
                domain: SymmetricSolve2,
                lawId: "symmetric-solve.solve2-vs-oracle",
                tier: Tier.Default,
                width: 5
            )
        ),
        Case(
            id: "symmetric-solve.solve3-vs-oracle",
            run: () => Laws.SweptClaim(
                claim: SymmetricSolveClaims.Solve3VsOracle,
                domain: SymmetricSolve3,
                lawId: "symmetric-solve.solve3-vs-oracle",
                tier: Tier.Default,
                width: 9
            )
        ),
        Case(
            id: "symmetric-solve.invert2-vs-oracle",
            run: () => Laws.SweptClaim(
                claim: SymmetricSolveClaims.Invert2VsOracle,
                domain: SymmetricInvert2,
                lawId: "symmetric-solve.invert2-vs-oracle",
                tier: Tier.Default,
                width: 3
            )
        ),
        Case(
            id: "symmetric-solve.invert3-vs-oracle",
            run: () => Laws.SweptClaim(
                claim: SymmetricSolveClaims.Invert3VsOracle,
                domain: SymmetricInvert3,
                lawId: "symmetric-solve.invert3-vs-oracle",
                tier: Tier.Default,
                width: 6
            )
        ),
        Case(
            id: "symmetric-solve.solve2-vs-bareiss",
            run: () => Laws.SweptClaim(
                claim: SymmetricSolveClaims.Solve2VsBareiss,
                domain: SymmetricSolve2,
                lawId: "symmetric-solve.solve2-vs-bareiss",
                tier: Tier.Default,
                width: 5
            )
        ),
        Case(
            id: "symmetric-solve.solve3-vs-bareiss",
            run: () => Laws.SweptClaim(
                claim: SymmetricSolveClaims.Solve3VsBareiss,
                domain: SymmetricSolve3,
                lawId: "symmetric-solve.solve3-vs-bareiss",
                tier: Tier.Default,
                width: 9
            )
        ),
        Case(
            id: "symmetric-solve.invert2-vs-bareiss",
            run: () => Laws.SweptClaim(
                claim: SymmetricSolveClaims.Invert2VsBareiss,
                domain: SymmetricInvert2,
                lawId: "symmetric-solve.invert2-vs-bareiss",
                tier: Tier.Default,
                width: 3
            )
        ),
        Case(
            id: "symmetric-solve.invert3-vs-bareiss",
            run: () => Laws.SweptClaim(
                claim: SymmetricSolveClaims.Invert3VsBareiss,
                domain: SymmetricInvert3,
                lawId: "symmetric-solve.invert3-vs-bareiss",
                tier: Tier.Default,
                width: 6
            )
        ),
        Case(
            id: "symmetric-solve.solve3-extreme-magnitude-agrees",
            run: () => Laws.Claim(
                claim: SymmetricSolveClaims.Solve3ExtremeMagnitudeAgrees,
                lawId: "symmetric-solve.solve3-extreme-magnitude-agrees"
            )
        ),
        Case(
            id: "symmetric-solve.singular-matrices-refuse",
            run: () => Laws.Claim(
                claim: SymmetricSolveClaims.SingularMatricesRefuse,
                lawId: "symmetric-solve.singular-matrices-refuse"
            )
        ),
        Case(
            id: "symmetric-solve.invert-large-magnitude-envelope-refuses",
            run: () => Laws.Claim(
                claim: SymmetricSolveClaims.InvertLargeMagnitudeEnvelopeRefuses,
                lawId: "symmetric-solve.invert-large-magnitude-envelope-refuses"
            )
        ),
        Case(
            id: "symmetric-solve.lossy-rank-one-singular-refuses",
            run: () => Laws.Claim(
                claim: SymmetricSolveClaims.LossyRankOneSingularRefuses,
                lawId: "symmetric-solve.lossy-rank-one-singular-refuses"
            )
        ),
        Case(
            id: "symmetric-solve.lossless-boundary-is-exact",
            run: () => Laws.Claim(
                claim: SymmetricSolveClaims.LosslessBoundaryIsExact,
                lawId: "symmetric-solve.lossless-boundary-is-exact"
            )
        ),
        Case(
            id: "symmetric-solve.divide-magnitude-rounded-full-width-agrees",
            run: () => Laws.Claim(
                claim: SymmetricSolveClaims.DivideMagnitudeRoundedFullWidthAgrees,
                lawId: "symmetric-solve.divide-magnitude-rounded-full-width-agrees"
            )
        ),
        Case(
            id: "symmetric-solve.refusal-leaves-no-stale-output",
            run: () => Laws.Claim(
                claim: SymmetricSolveClaims.RefusalLeavesNoStaleOutput,
                lawId: "symmetric-solve.refusal-leaves-no-stale-output"
            )
        ),
        Case(
            id: "symmetric-solve.solve2-residual-within-envelope",
            run: () => Laws.SweptClaim(
                claim: SymmetricSolveClaims.Solve2ResidualWithinEnvelope,
                domain: SymmetricSolve2,
                lawId: "symmetric-solve.solve2-residual-within-envelope",
                tier: Tier.Default,
                width: 5
            )
        ),
        Case(
            id: "symmetric-solve.solve3-residual-within-envelope",
            run: () => Laws.SweptClaim(
                claim: SymmetricSolveClaims.Solve3ResidualWithinEnvelope,
                domain: SymmetricSolve3,
                lawId: "symmetric-solve.solve3-residual-within-envelope",
                tier: Tier.Default,
                width: 9
            )
        ),
        Case(
            id: "symmetric-solve.invert2-residual-within-envelope",
            run: () => Laws.SweptClaim(
                claim: SymmetricSolveClaims.Invert2ResidualWithinEnvelope,
                domain: SymmetricInvert2,
                lawId: "symmetric-solve.invert2-residual-within-envelope",
                tier: Tier.Default,
                width: 3
            )
        ),
        Case(
            id: "symmetric-solve.invert3-residual-within-envelope",
            run: () => Laws.SweptClaim(
                claim: SymmetricSolveClaims.Invert3ResidualWithinEnvelope,
                domain: SymmetricInvert3,
                lawId: "symmetric-solve.invert3-residual-within-envelope",
                tier: Tier.Default,
                width: 6
            )
        ),
        Case(
            id: "symmetric-solve.solve3-non-diagonal-exact-value",
            run: () => Laws.Claim(
                claim: SymmetricSolveClaims.Solve3NonDiagonalExactValue,
                lawId: "symmetric-solve.solve3-non-diagonal-exact-value"
            )
        ),
        Case(
            id: "symmetric-solve.solve3-all-cofactors-exact-value",
            run: () => Laws.Claim(
                claim: SymmetricSolveClaims.Solve3AllCofactorsExactValue,
                lawId: "symmetric-solve.solve3-all-cofactors-exact-value"
            )
        ),
        Case(
            id: "symmetric-solve.apply2-vs-oracle",
            run: () => Laws.SweptClaim(
                claim: SymmetricSolveClaims.Apply2VsOracle,
                domain: SymmetricApply2,
                lawId: "symmetric-solve.apply2-vs-oracle",
                tier: Tier.Default,
                width: 5
            )
        ),
        Case(
            id: "symmetric-solve.apply3-vs-oracle",
            run: () => Laws.SweptClaim(
                claim: SymmetricSolveClaims.Apply3VsOracle,
                domain: SymmetricApply3,
                lawId: "symmetric-solve.apply3-vs-oracle",
                tier: Tier.Default,
                width: 9
            )
        ),
        Case(
            id: "symmetric-solve.apply-refusal-and-symmetry",
            run: () => Laws.Claim(
                claim: SymmetricSolveClaims.ApplyRefusalAndSymmetry,
                lawId: "symmetric-solve.apply-refusal-and-symmetry"
            )
        ),

    ];
    private static LawCase[] MixedScaleCases() => [
        // ---- FusedArithmetic: public refusing mixed-scale operations and their internal wrapping siblings ----
        Case(
            id: "mixed-scale.product-vs-oracle",
            run: () => Laws.SweptClaim(
                claim: MixedScaleClaims.ProductVsOracle,
                domain: MixedScale,
                lawId: "mixed-scale.product-vs-oracle",
                tier: Tier.Default,
                width: 4
            )
        ),
        Case(
            id: "mixed-scale.checked-product-matches-representability",
            run: () => Laws.SweptClaim(
                claim: MixedScaleClaims.CheckedProductMatchesRepresentability,
                domain: MixedScale,
                lawId: "mixed-scale.checked-product-matches-representability",
                tier: Tier.Default,
                width: 4
            )
        ),
        Case(
            id: "mixed-scale.triple-product-vs-oracle",
            run: () => Laws.SweptClaim(
                claim: MixedScaleClaims.TripleProductVsOracle,
                domain: MixedScaleTriple,
                lawId: "mixed-scale.triple-product-vs-oracle",
                tier: Tier.Default,
                width: 4
            )
        ),
        Case(
            id: "mixed-scale.dot-product-vs-oracle",
            run: () => Laws.SweptClaim(
                claim: MixedScaleClaims.DotProductVsOracle,
                domain: MixedScale,
                lawId: "mixed-scale.dot-product-vs-oracle",
                tier: Tier.Default,
                width: 5
            )
        ),
        Case(
            id: "mixed-scale.scaled-reciprocal-vs-oracle",
            run: () => Laws.SweptClaim(
                claim: MixedScaleClaims.ScaledReciprocalVsOracle,
                domain: MixedScale,
                lawId: "mixed-scale.scaled-reciprocal-vs-oracle",
                tier: Tier.Default,
                width: 2
            )
        ),
        Case(
            id: "mixed-scale.scaled-reciprocal-invalid-inputs-refuse",
            run: () => Laws.Claim(
                claim: MixedScaleClaims.ScaledReciprocalInvalidInputsRefuse,
                lawId: "mixed-scale.scaled-reciprocal-invalid-inputs-refuse"
            )
        ),
        Case(
            id: "mixed-scale.extreme-scale-counts-are-congruent",
            run: () => Laws.Claim(
                claim: MixedScaleClaims.ExtremeScaleCountsAreCongruent,
                lawId: "mixed-scale.extreme-scale-counts-are-congruent"
            )
        ),

    ];
    private static LawCase[] DirectedRoundingCases() => [
        // ---- FixedDirectedRounding: the conservative upper bounds (public — Puck.World.Data's first production caller) ----
        Case(
            id: "directed-rounding.ceiling-square-root-is-least-upper-bound",
            run: () => Laws.SweptClaim(
                claim: DirectedRoundingClaims.CeilingSquareRootIsLeastUpperBound,
                domain: DirectedRoot,
                lawId: "directed-rounding.ceiling-square-root-is-least-upper-bound",
                tier: Tier.Default,
                width: 4
            )
        ),
        Case(
            id: "directed-rounding.ceiling-product-is-least-upper-bound",
            run: () => Laws.SweptClaim(
                claim: DirectedRoundingClaims.CeilingProductIsLeastUpperBound,
                domain: DirectedProduct,
                lawId: "directed-rounding.ceiling-product-is-least-upper-bound",
                tier: Tier.Default,
                width: 4
            )
        ),
        Case(
            id: "directed-rounding.ceiling-quotient-is-least-upper-bound",
            run: () => Laws.SweptClaim(
                claim: DirectedRoundingClaims.CeilingQuotientIsLeastUpperBound,
                domain: DirectedQuotient,
                lawId: "directed-rounding.ceiling-quotient-is-least-upper-bound",
                tier: Tier.Default,
                width: 4
            )
        ),
        Case(
            id: "directed-rounding.ceiling-product-sum-is-least-upper-bound",
            run: () => Laws.SweptClaim(
                claim: DirectedRoundingClaims.CeilingProductSumIsLeastUpperBound,
                domain: DirectedProductSum,
                lawId: "directed-rounding.ceiling-product-sum-is-least-upper-bound",
                tier: Tier.Default,
                width: 4
            )
        ),
        Case(
            id: "directed-rounding.ceiling-magnitude-is-least-upper-bound",
            run: () => Laws.SweptClaim(
                claim: DirectedRoundingClaims.CeilingMagnitudeIsLeastUpperBound,
                domain: DirectedMagnitude,
                lawId: "directed-rounding.ceiling-magnitude-is-least-upper-bound",
                tier: Tier.Default,
                width: 4
            )
        ),
        Case(
            id: "directed-rounding.negative-operands-refuse",
            run: () => Laws.Claim(
                claim: DirectedRoundingClaims.NegativeOperandsRefuse,
                lawId: "directed-rounding.negative-operands-refuse"
            )
        ),

    ];
    private static LawCase[] MassPropertiesCases() => [
        // ---- FixedMassProperties: volumes, bodies, transfer, compound and the inversions (internal) ----
        Case(
            id: "mass-properties.volumes-vs-oracle",
            run: () => Laws.SweptClaim(
                claim: MassPropertyClaims.VolumesVsOracle,
                domain: MassVolume,
                lawId: "mass-properties.volumes-vs-oracle",
                tier: Tier.Default,
                width: 3
            )
        ),
        Case(
            id: "mass-properties.sphere-vs-oracle",
            run: () => Laws.SweptClaim(
                claim: MassPropertyClaims.SphereVsOracle,
                domain: MassSphere,
                lawId: "mass-properties.sphere-vs-oracle",
                tier: Tier.Default,
                width: 4
            )
        ),
        Case(
            id: "mass-properties.box-vs-oracle",
            run: () => Laws.SweptClaim(
                claim: MassPropertyClaims.BoxVsOracle,
                domain: MassBox,
                lawId: "mass-properties.box-vs-oracle",
                tier: Tier.Default,
                width: 4
            )
        ),
        Case(
            id: "mass-properties.cylinder-vs-oracle",
            run: () => Laws.SweptClaim(
                claim: MassPropertyClaims.CylinderVsOracle,
                domain: MassCylinder,
                lawId: "mass-properties.cylinder-vs-oracle",
                tier: Tier.Default,
                width: 4
            )
        ),
        Case(
            id: "mass-properties.capsule-vs-oracle",
            run: () => Laws.SweptClaim(
                claim: MassPropertyClaims.CapsuleVsOracle,
                domain: MassCapsule,
                lawId: "mass-properties.capsule-vs-oracle",
                tier: Tier.Default,
                width: 4
            )
        ),
        Case(
            id: "mass-properties.parallel-axis-vs-oracle",
            run: () => Laws.SweptClaim(
                claim: MassPropertyClaims.ParallelAxisVsOracle,
                domain: MassParallelAxis,
                lawId: "mass-properties.parallel-axis-vs-oracle",
                tier: Tier.Default,
                width: 10
            )
        ),
        Case(
            id: "mass-properties.compound-vs-oracle",
            run: () => Laws.SweptClaim(
                claim: MassPropertyClaims.CompoundVsOracle,
                domain: MassCompound,
                lawId: "mass-properties.compound-vs-oracle",
                tier: Tier.Default,
                width: 10
            )
        ),
        Case(
            id: "mass-properties.capsule-degenerates-to-sphere",
            run: () => Laws.Claim(
                claim: MassPropertyClaims.CapsuleDegeneratesToSphere,
                lawId: "mass-properties.capsule-degenerates-to-sphere"
            )
        ),
        Case(
            id: "mass-properties.inversion-refuses-below-resolution",
            run: () => Laws.Claim(
                claim: MassPropertyClaims.InversionRefusesBelowResolution,
                lawId: "mass-properties.inversion-refuses-below-resolution"
            )
        ),
        Case(
            id: "mass-properties.fraction-bit-count-bound",
            run: () => Laws.Claim(
                claim: MassPropertyClaims.FractionBitCountBoundIsPinned,
                lawId: "mass-properties.fraction-bit-count-bound"
            )
        ),
        Case(
            id: "mass-properties.pinned-pi-is-correctly-rounded",
            run: () => Laws.Claim(
                claim: MassPropertyClaims.PinnedPiIsCorrectlyRounded,
                lawId: "mass-properties.pinned-pi-is-correctly-rounded"
            )
        ),

    ];
}
