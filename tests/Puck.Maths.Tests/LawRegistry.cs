using System.Reflection;

namespace Puck.Maths.Tests;

/// <summary>One declared law case: a stable id, its tier, the public members it exercises (for the coverage manifest),
/// the legs it stands on (for the leg ledger), and the action that runs it. Facts are generated from this registry, and
/// both the coverage manifest and the leg ledger read these same declarations — so both are derived mechanically from
/// the law instantiations, not by hand.</summary>
/// <param name="Id">The stable case id (also the test display name).</param>
/// <param name="Tier">The execution tier.</param>
/// <param name="Members">The public members this case covers.</param>
/// <param name="Legs">What this case's statements stand on. Required: a case declaring no leg does not compile.</param>
/// <param name="Run">The action that runs the case.</param>
internal sealed record LawCase(string Id, Tier Tier, IReadOnlyList<CoverRef> Members, IReadOnlyList<Leg> Legs, Action Run);
/// <summary>
/// The seed law instantiations — the fixed-point algebra cluster proved end-to-end. Each combinator from
/// <see cref="Laws"/> is instantiated per subject here, once, as a declaration. This is the only place subjects, oracles,
/// domains, and covered members meet. Every case here is a law <see cref="LawTests"/> executes as a theory row, so a
/// member the coverage module credits is always credited to a case that runs and asserts; timing work has no
/// declaration here (<see cref="BenchTests"/> owns the bench outright).
/// </summary>
internal static class LawRegistry {
    /// <summary>Gets the Puck.Maths assembly a declared member's type name is resolved against.</summary>
    private static readonly Assembly MathsAssembly = typeof(FixedQ4816).Assembly;
    private static readonly Domain Complex = new(
        Key: "complex",
        Block: 512,
        EdgeFraction: 0.4,
        NeighborhoodFraction: 0.3
    );
    private static readonly Domain Split = new(
        Key: "split",
        Block: 512,
        EdgeFraction: 0.4,
        NeighborhoodFraction: 0.3
    );
    private static readonly Domain Dual = new(
        Key: "dual",
        Block: 512,
        EdgeFraction: 0.4,
        NeighborhoodFraction: 0.3
    );
    private static readonly Domain CliffordPlanarComplex = new(
        Key: "clifford-planar-complex",
        Block: 512,
        EdgeFraction: 0.4,
        NeighborhoodFraction: 0.3
    );
    private static readonly Domain CliffordPlanarSplit = new(
        Key: "clifford-planar-split",
        Block: 512,
        EdgeFraction: 0.4,
        NeighborhoodFraction: 0.3
    );
    private static readonly Domain CliffordPlanarDual = new(
        Key: "clifford-planar-dual",
        Block: 512,
        EdgeFraction: 0.4,
        NeighborhoodFraction: 0.3
    );
    private static readonly Domain CliffordQuaternionEven = new(
        Key: "clifford-quaternion-even",
        Block: 512,
        EdgeFraction: 0.4,
        NeighborhoodFraction: 0.3
    );
    private static readonly Domain CliffordMotor = new(
        Key: "clifford-motor",
        Block: 512,
        EdgeFraction: 0.4,
        NeighborhoodFraction: 0.3
    );
    private static readonly Domain CliffordReverse = new(
        Key: "clifford-reverse",
        Block: 512,
        EdgeFraction: 0.4,
        NeighborhoodFraction: 0.3
    );
    private static readonly Domain CliffordMultivector = new(
        Key: "clifford-multivector",
        Block: 512,
        EdgeFraction: 0.4,
        NeighborhoodFraction: 0.3
    );
    private static readonly Domain MonogenicExact = new(
        Key: "monogenic-exact",
        Block: 512,
        EdgeFraction: 0.4,
        NeighborhoodFraction: 0.3
    );
    private static readonly Domain MonogenicFusion = new(
        Key: "monogenic-fusion",
        Block: 512,
        EdgeFraction: 0.4,
        NeighborhoodFraction: 0.3
    );
    private static readonly Domain Fractional = new(
        Key: "algebra-fractional",
        Block: 512,
        EdgeFraction: 0.4,
        NeighborhoodFraction: 0.3
    );
    private static readonly Domain Mobius = new(
        Key: "mobius",
        Block: 512,
        EdgeFraction: 0.4,
        NeighborhoodFraction: 0.3
    );
    private static readonly Domain Scalar = new(
        Key: "scalar",
        Block: 512,
        EdgeFraction: 0.4,
        NeighborhoodFraction: 0.3
    );
    private static readonly Domain ScalarDivision = new(
        Key: "scalar-division",
        Block: 512,
        EdgeFraction: 0.4,
        NeighborhoodFraction: 0.3
    );
    // The transcendental band. A LOWER edge fraction than the house default on purpose: the shared edge set is mostly
    // extremes, and every transcendental subject folds its operand onto a band, so an edge-heavy mixture would spend
    // most of its draws on a handful of folded images instead of sweeping the 128-interval tables.
    private static readonly Domain ScalarTranscendental = new(
        Key: "scalar-transcendental",
        Block: 512,
        EdgeFraction: 0.25,
        NeighborhoodFraction: 0.25
    );
    private static readonly Domain ScalarText = new(
        Key: "scalar-text",
        Block: 512,
        EdgeFraction: 0.4,
        NeighborhoodFraction: 0.3
    );
    // The contribution fold consumes more independent raw values than the scalar binary combinators expose, so its
    // claims take vector pairs and map them to valid configurations inside the claim. Each sampled statement owns a
    // frontier key; all stay full-width because Int128 totality at the signed-carrier edges is part of the contract.
    private static readonly Domain ContributionFoldFormula = new(
        Key: "contribution-fold-formula",
        Block: 512,
        EdgeFraction: 0.4,
        NeighborhoodFraction: 0.3
    );
    private static readonly Domain ContributionFoldNoPool = new(
        Key: "contribution-fold-no-pool",
        Block: 512,
        EdgeFraction: 0.4,
        NeighborhoodFraction: 0.3
    );
    private static readonly Domain ContributionFoldOrder = new(
        Key: "contribution-fold-order",
        Block: 512,
        EdgeFraction: 0.4,
        NeighborhoodFraction: 0.3
    );
    private static readonly Domain ContributionFoldAnalog = new(
        Key: "contribution-fold-analog",
        Block: 512,
        EdgeFraction: 0.4,
        NeighborhoodFraction: 0.3
    );
    private static readonly Domain ContributionFoldQuantization = new(
        Key: "contribution-fold-quantization",
        Block: 512,
        EdgeFraction: 0.4,
        NeighborhoodFraction: 0.3
    );
    private static readonly Domain Sublattice = new(
        Block: 256,
        EdgeFraction: 0.3,
        Key: "sublattice",
        NeighborhoodFraction: 0.3,
        SublatticeShift: 16
    );
    private static readonly Domain SmokeDomain = new(
        Key: "smoke",
        Block: 64,
        EdgeFraction: 0.5,
        NeighborhoodFraction: 0.3
    );
    private static readonly Domain Presented = new(
        Key: "presented",
        Block: 512,
        EdgeFraction: 0.4,
        NeighborhoodFraction: 0.3
    );
    private static readonly Domain ClosedUnit = new(
        Key: "closed-unit",
        Block: 512,
        EdgeFraction: 0.4,
        NeighborhoodFraction: 0.3
    );
    private static readonly Domain UnitFraction16Domain = new(
        Key: "unit-fraction16",
        Block: 512,
        EdgeFraction: 0.4,
        NeighborhoodFraction: 0.3
    );
    private static readonly Domain UnitFraction32Domain = new(
        Key: "unit-fraction32",
        Block: 512,
        EdgeFraction: 0.4,
        NeighborhoodFraction: 0.3
    );
    // The unsigned Q48.16 band. NO sublattice: every law over it either rounds once against an exact oracle or is exact
    // by construction, and the operand fold is a plain bit reinterpretation, so the whole sixty-four-bit word is legal.
    private static readonly Domain UnsignedScalar = new(
        Key: "unsigned-scalar",
        Block: 512,
        EdgeFraction: 0.4,
        NeighborhoodFraction: 0.3
    );
    // The signed Q16.48 band (FixedQ1648) — sixteen integer bits, forty-eight fraction bits, a range/resolution lean
    // opposite FixedQ4816's own. Same shared EdgeRaws battery as every other signed sixty-four-bit carrier; its own
    // peer-conversion boundary (the sixteen-bit integer range) is swept separately by its own fixed ladder, not by
    // this domain.
    private static readonly Domain Q1648Scalar = new(
        Key: "q1648-scalar",
        Block: 512,
        EdgeFraction: 0.4,
        NeighborhoodFraction: 0.3
    );
    private static readonly Domain Q1648ScalarDivision = new(
        Key: "q1648-scalar-division",
        Block: 512,
        EdgeFraction: 0.4,
        NeighborhoodFraction: 0.3
    );
    // The signed Q32.32 band (FixedQ3232) — an even thirty-two/thirty-two split, the balanced point between
    // FixedQ4816's and FixedQ1648's opposite leans. Same shared EdgeRaws battery; its own peer-conversion boundary
    // (the thirty-two-bit integer range) is swept separately by its own fixed ladder, not by this domain.
    private static readonly Domain Q3232Scalar = new(
        Key: "q3232-scalar",
        Block: 512,
        EdgeFraction: 0.4,
        NeighborhoodFraction: 0.3
    );
    private static readonly Domain Q3232ScalarDivision = new(
        Key: "q3232-scalar-division",
        Block: 512,
        EdgeFraction: 0.4,
        NeighborhoodFraction: 0.3
    );
    // The attenuation bands — one key per sampled meet statement, the contribution-fold discipline, so distinct
    // statements never re-sweep one another's ground. All full-width and NO sublattice: the carriers are total on the
    // plain bit reinterpretation of a lane, so every committed edge raw is legal, and the battery's 0 and −1 land
    // exactly on Bottom and Top.
    private static readonly Domain MeetAssociative = new(
        Key: "meet-associative",
        Block: 512,
        EdgeFraction: 0.4,
        NeighborhoodFraction: 0.3
    );
    private static readonly Domain MeetBottomAbsorption = new(
        Key: "meet-bottom-absorption",
        Block: 512,
        EdgeFraction: 0.4,
        NeighborhoodFraction: 0.3
    );
    private static readonly Domain MeetCommutative = new(
        Key: "meet-commutative",
        Block: 512,
        EdgeFraction: 0.4,
        NeighborhoodFraction: 0.3
    );
    private static readonly Domain MeetIdempotent = new(
        Key: "meet-idempotent",
        Block: 512,
        EdgeFraction: 0.4,
        NeighborhoodFraction: 0.3
    );
    private static readonly Domain MeetMonotonicity = new(
        Key: "meet-monotonicity",
        Block: 512,
        EdgeFraction: 0.4,
        NeighborhoodFraction: 0.3
    );
    private static readonly Domain MeetOrderCoherence = new(
        Key: "meet-order-coherence",
        Block: 512,
        EdgeFraction: 0.4,
        NeighborhoodFraction: 0.3
    );
    private static readonly Domain MeetProductComposition = new(
        Key: "meet-product-composition",
        Block: 512,
        EdgeFraction: 0.4,
        NeighborhoodFraction: 0.3
    );
    private static readonly Domain MeetTopIdentity = new(
        Key: "meet-top-identity",
        Block: 512,
        EdgeFraction: 0.4,
        NeighborhoodFraction: 0.3
    );
    // The hypercomplex bands. Each construction gets its own key so the frontier advances independently and two laws
    // over the same type never re-sweep one another's ground. Every narrow/wide gate in the family sits at 2¹⁷, 2²⁹,
    // 2³⁰, 2³¹, 2⁴⁰, 2⁴² or 2⁴⁵, and the committed edge set carries raws strictly on both sides of each, so every case
    // below straddles its own gate.
    private static readonly Domain ComplexDivide = new(
        Key: "complex-divide",
        Block: 512,
        EdgeFraction: 0.4,
        NeighborhoodFraction: 0.3
    );
    private static readonly Domain ComplexDirection = new(
        Key: "complex-direction",
        Block: 512,
        EdgeFraction: 0.4,
        NeighborhoodFraction: 0.3
    );
    private static readonly Domain ComplexRotate = new(
        Key: "complex-rotate",
        Block: 512,
        EdgeFraction: 0.4,
        NeighborhoodFraction: 0.3
    );
    private static readonly Domain SplitDivide = new(
        Key: "split-divide",
        Block: 512,
        EdgeFraction: 0.4,
        NeighborhoodFraction: 0.3
    );
    private static readonly Domain SplitTransform = new(
        Key: "split-transform",
        Block: 512,
        EdgeFraction: 0.4,
        NeighborhoodFraction: 0.3
    );
    private static readonly Domain DualDivide = new(
        Key: "dual-divide",
        Block: 512,
        EdgeFraction: 0.4,
        NeighborhoodFraction: 0.3
    );
    private static readonly Domain DualGeneric = new(
        Key: "dual-generic",
        Block: 512,
        EdgeFraction: 0.4,
        NeighborhoodFraction: 0.3
    );
    private static readonly Domain DualQuaternion = new(
        Key: "dual-quaternion",
        Block: 512,
        EdgeFraction: 0.4,
        NeighborhoodFraction: 0.3
    );
    private static readonly Domain Quaternion = new(
        Key: "quaternion",
        Block: 512,
        EdgeFraction: 0.4,
        NeighborhoodFraction: 0.3
    );
    private static readonly Domain QuaternionDirection = new(
        Key: "quaternion-direction",
        Block: 512,
        EdgeFraction: 0.4,
        NeighborhoodFraction: 0.3
    );
    private static readonly Domain QuaternionRotate = new(
        Key: "quaternion-rotate",
        Block: 512,
        EdgeFraction: 0.4,
        NeighborhoodFraction: 0.3
    );
    private static readonly Domain QuaternionSublattice = new(
        Block: 256,
        EdgeFraction: 0.3,
        Key: "quaternion-sublattice",
        NeighborhoodFraction: 0.3,
        SublatticeShift: 16
    );
    // The vector bands. Each is the full signed range at the DOMAIN level; where a law needs a narrower operand it
    // folds inside the subject AND the oracle, the established Subjects.ClosedUnitRaw pattern, rather than shrinking
    // the sampler. The norm band is a separate key from the product band on purpose: the norms' refusal boundary is a
    // different region of the space from the products' rounding boundary, so the two sweep independent progressive
    // ground. Only the lattice band folds at the domain level, because its whole point is that nothing rounds.
    private static readonly Domain Vector = new(
        Key: "vector",
        Block: 512,
        EdgeFraction: 0.4,
        NeighborhoodFraction: 0.3
    );
    private static readonly Domain VectorNorm = new(
        Key: "vector-norm",
        Block: 512,
        EdgeFraction: 0.4,
        NeighborhoodFraction: 0.3
    );
    private static readonly Domain VectorNarrow = new(
        Key: "vector-narrow",
        Block: 512,
        EdgeFraction: 0.4,
        NeighborhoodFraction: 0.3
    );
    private static readonly Domain VectorDirection = new(
        Key: "vector-direction",
        Block: 512,
        EdgeFraction: 0.3,
        NeighborhoodFraction: 0.3
    );
    private static readonly Domain VectorLattice = new(
        Block: 512,
        EdgeFraction: 0.3,
        Key: "vector-lattice",
        NeighborhoodFraction: 0.3,
        SublatticeShift: 16
    );
    private static readonly Domain VectorOrthonormalBasis = new(
        Key: "vector-orthonormal-basis",
        Block: 512,
        EdgeFraction: 0.3,
        NeighborhoodFraction: 0.3
    );
    // The kinematics bands. All full-range and NO sublattice anywhere: the position and rate statements are exact
    // integer arithmetic that needs the whole raw space (the cell-index extremes ARE the interesting operands, and the
    // refusals are part of the contract), and the rigid transform's inexact statements are pinned by hand-derived
    // ladders rather than by lattices. Each construction gets its own key so two laws over one type never re-sweep each
    // other's ground. Every gate this family branches on has committed edge raws strictly on both sides: the carry
    // shift's wrap at long.MaxValue, TryTranslate's overflow into the Int128 canonicalizer, TryDelta's conservative 2²⁶
    // gate at ±2⁴⁷ cells, the dual-quaternion product's 2²⁹ and 2¹⁷/2⁴² gates at ±2³¹ and ±2⁴⁷, the rotation sandwich's
    // 2¹⁷/2⁴⁰ gate at ±65536 and ±2⁴⁷, and the normalizer's band at ±2⁴⁷.
    private static readonly Domain Position = new(
        Key: "position",
        Block: 512,
        EdgeFraction: 0.4,
        NeighborhoodFraction: 0.3
    );
    private static readonly Domain PositionDelta = new(
        Key: "position-delta",
        Block: 512,
        EdgeFraction: 0.4,
        NeighborhoodFraction: 0.3
    );
    private static readonly Domain PositionTranslate = new(
        Key: "position-translate",
        Block: 512,
        EdgeFraction: 0.4,
        NeighborhoodFraction: 0.3
    );
    private static readonly Domain Rigid = new(
        Key: "rigid",
        Block: 512,
        EdgeFraction: 0.4,
        NeighborhoodFraction: 0.3
    );
    private static readonly Domain RigidDirection = new(
        Key: "rigid-direction",
        Block: 512,
        EdgeFraction: 0.4,
        NeighborhoodFraction: 0.3
    );
    private static readonly Domain RigidPoint = new(
        Key: "rigid-point",
        Block: 512,
        EdgeFraction: 0.4,
        NeighborhoodFraction: 0.3
    );
    private static readonly Domain Rate = new(
        Key: "rate",
        Block: 512,
        EdgeFraction: 0.4,
        NeighborhoodFraction: 0.3
    );
    // The symmetric-solve bands, one key per arity so a solve law and its invert sibling never re-sweep each other's
    // ground. ALL FOUR domains are consumed through SymmetricSolveClaims.FoldModerate — Solve's own claim bodies
    // fold their operands exactly like Invert's; there is no unfolded domain anywhere in this family (corrected from
    // an earlier, inaccurate "no fold anywhere" version of this comment). The fold keeps every group operand's own
    // magnitude well below its family's target-plus-one, so the shared preconditioning shift stays non-negative (a
    // lossless left shift, or none) on every draw, and the subject's ratio is provably exact there — checked
    // against THREE independent references over this same ground: the adjugate oracle (solveN/invertN-vs-oracle),
    // the fraction-free Bareiss oracle (solveN/invertN-vs-bareiss, which shares no cofactor or determinant
    // transcription with either the subject or the adjugate oracle — see Oracles.TryBareissEliminate), and the
    // exact K·x/K·K⁻¹ residual laws (a necessary bound only, NOT a substitute for the two oracle comparisons — see
    // SymmetricSolveClaims.Solve2ResidualWithinEnvelope's own remarks for why a small residual cannot prove small
    // component error). The large-magnitude corner where a lossy right-shift rounds before any cancellation — where
    // Solve's ratio is only approximately preserved and Invert can refuse outright — is NOT swept by these domains;
    // it has its own dedicated laws instead (symmetric-solve.solve3-extreme-magnitude-agrees,
    // symmetric-solve.invert-large-magnitude-envelope-refuses, symmetric-solve.lossy-rank-one-singular-refuses,
    // symmetric-solve.lossless-boundary-is-exact).
    //
    // Invert's smaller EdgeFraction (0.25 vs Solve's 0.4) and smaller Block (256 vs 512) are NOT a verified
    // differential choice. The stated motivation — scalar-transcendental's "an edge-heavy mixture would spend most
    // of its draws on values the fold collapses together" — applies to FoldModerate identically for Solve and
    // Invert: FoldModerate(long.MaxValue) is -1 for both, and every other shared committed edge collapses the same
    // way regardless of which family consumes it. No verified reason for Invert alone to sample less has been
    // found; this is an unexplained sampling choice inherited from an earlier tuning pass, left as-is here rather
    // than invented a justification for.
    private static readonly Domain SymmetricSolve2 = new(
        Key: "symmetric-solve2",
        Block: 512,
        EdgeFraction: 0.4,
        NeighborhoodFraction: 0.3
    );
    private static readonly Domain SymmetricSolve3 = new(
        Key: "symmetric-solve3",
        Block: 512,
        EdgeFraction: 0.4,
        NeighborhoodFraction: 0.3
    );
    private static readonly Domain SymmetricInvert2 = new(
        Key: "symmetric-invert2",
        Block: 256,
        EdgeFraction: 0.25,
        NeighborhoodFraction: 0.3
    );
    private static readonly Domain SymmetricInvert3 = new(
        Key: "symmetric-invert3",
        Block: 256,
        EdgeFraction: 0.25,
        NeighborhoodFraction: 0.3
    );
    // The apply bands. NO fold anywhere, at the domain or in the claim: a matrix-times-vector component is a sum of at
    // most three raw products, bounded by 3·2^126, so the sign-plus-UInt128 accumulator is exact over the whole signed
    // range and there is no preconditioning envelope to stay inside — the reason Solve's and Invert's claims fold and
    // Apply's do not.
    private static readonly Domain SymmetricApply2 = new(
        Key: "symmetric-apply2",
        Block: 512,
        EdgeFraction: 0.4,
        NeighborhoodFraction: 0.3
    );
    private static readonly Domain SymmetricApply3 = new(
        Key: "symmetric-apply3",
        Block: 512,
        EdgeFraction: 0.4,
        NeighborhoodFraction: 0.3
    );
    // The mixed-scale and directed-rounding bands. Both are full-width at the domain and fold only the FRACTION BIT
    // COUNTS inside the claim (onto [0, 64]), because the counts are the operand whose extremes would otherwise put
    // the oracle's own power-of-two denominator past any width; the shift-count corners those folds exclude are pinned
    // by their own hand-derived claim instead. The directed band folds its value operands onto the non-negative half
    // by one logical shift, which preserves each committed edge raw's bit pattern rather than collapsing the battery.
    private static readonly Domain MixedScale = new(
        Key: "mixed-scale",
        Block: 512,
        EdgeFraction: 0.4,
        NeighborhoodFraction: 0.3
    );
    private static readonly Domain MixedScaleTriple = new(
        Key: "mixed-scale-triple",
        Block: 512,
        EdgeFraction: 0.4,
        NeighborhoodFraction: 0.3
    );
    private static readonly Domain DirectedRoot = new(
        Key: "directed-root",
        Block: 512,
        EdgeFraction: 0.4,
        NeighborhoodFraction: 0.3
    );
    private static readonly Domain DirectedProduct = new(
        Key: "directed-product",
        Block: 512,
        EdgeFraction: 0.4,
        NeighborhoodFraction: 0.3
    );
    private static readonly Domain DirectedQuotient = new(
        Key: "directed-quotient",
        Block: 512,
        EdgeFraction: 0.4,
        NeighborhoodFraction: 0.3
    );
    private static readonly Domain DirectedProductSum = new(
        Key: "directed-product-sum",
        Block: 512,
        EdgeFraction: 0.4,
        NeighborhoodFraction: 0.3
    );
    private static readonly Domain DirectedMagnitude = new(
        Key: "directed-magnitude",
        Block: 512,
        EdgeFraction: 0.4,
        NeighborhoodFraction: 0.3
    );
    // The mass-property bands, one key per shape family so a sphere law and a capsule law never re-sweep each other's
    // ground. The blocks are HALF the house 512: every draw here forms several BigInteger products of a few hundred
    // bits on both the subject's and the oracle's side, which is the cost, and the Default tier's random batch already
    // dominates the sweep.
    private static readonly Domain MassVolume = new(
        Key: "mass-volume",
        Block: 256,
        EdgeFraction: 0.4,
        NeighborhoodFraction: 0.3
    );
    private static readonly Domain MassSphere = new(
        Key: "mass-sphere",
        Block: 256,
        EdgeFraction: 0.4,
        NeighborhoodFraction: 0.3
    );
    private static readonly Domain MassBox = new(
        Key: "mass-box",
        Block: 256,
        EdgeFraction: 0.4,
        NeighborhoodFraction: 0.3
    );
    private static readonly Domain MassCylinder = new(
        Key: "mass-cylinder",
        Block: 256,
        EdgeFraction: 0.4,
        NeighborhoodFraction: 0.3
    );
    private static readonly Domain MassCapsule = new(
        Key: "mass-capsule",
        Block: 256,
        EdgeFraction: 0.4,
        NeighborhoodFraction: 0.3
    );
    private static readonly Domain MassParallelAxis = new(
        Key: "mass-parallel-axis",
        Block: 256,
        EdgeFraction: 0.4,
        NeighborhoodFraction: 0.3
    );
    private static readonly Domain MassCompound = new(
        Key: "mass-compound",
        Block: 256,
        EdgeFraction: 0.4,
        NeighborhoodFraction: 0.3
    );
    // The GF(2)[t] bands. Operands are raw ulong coefficient bit-vectors — the domain's signed raw reinterpreted
    // unchanged by Subjects.UnsignedRaw — so the committed edge set lands on exactly the seams this ring branches on:
    // 0 (the zero polynomial, degree −1), 1 (the constant one), −1 (t⁶³ + … + 1, maximum weight), long.MinValue (the
    // bare monomial t⁶³), long.MaxValue (dense through t⁶²), ±2⁴⁷, ±2³¹, ±65536, ±32768, ±256 and their off-by-ones.
    // Every degree the truncation seam cares about — 0, 1, 8, 15, 16, 31, 47, 62, 63 — appears in that battery. The
    // blocks are 256 rather than the house 512: the frontier stream is the cheapest part of every case here and the
    // BigInteger oracles are the cost. The ring key is shared by the additive, product and shift statements on
    // purpose — one edge polynomial goes through addition, multiplication and both shifts inside one sweep — while
    // division and the greatest common divisor take their own keys, because the divisor band and the
    // planted-common-factor band are ground the ring statements do not cross.
    private static readonly Domain BinaryPolynomialRing = new(
        Key: "binary-polynomial",
        Block: 256,
        EdgeFraction: 0.4,
        NeighborhoodFraction: 0.3
    );
    private static readonly Domain BinaryPolynomialDivision = new(
        Key: "binary-polynomial-division",
        Block: 256,
        EdgeFraction: 0.4,
        NeighborhoodFraction: 0.3
    );
    private static readonly Domain BinaryPolynomialGcd = new(
        Key: "binary-polynomial-gcd",
        Block: 256,
        EdgeFraction: 0.4,
        NeighborhoodFraction: 0.3
    );
    // The GF(2^k) bands. Every operand is folded to a legal packed element of the field under test INSIDE the claim
    // body — the established Subjects.ClosedUnitRaw pattern — because the domains' raw space is the signed Q48.16
    // carrier and a binary field's is a packed bit vector; the fold is applied identically in the subject and in the
    // oracle, so every sampled operand reaches a defined comparison. Each case takes its own key so two laws over one
    // family never re-sweep each other's ground. The blocks are HALF the house 512 deliberately: every case here runs
    // five carriers per iteration and the Default tier's 256-draw random batch already dominates the sweep, so a
    // larger frontier block would buy volume at the tier with the least budget. Note what the edge battery does at
    // these keys: Domains.Vectors' single-lane phase clears every other lane, so a 128-bit element assembled from two
    // lanes carries a zero half there and a modulus derived from a cleared lane lands on degree 1, tail 1 — GF(2),
    // which is a real instantiation rather than a degenerate one, and is named as an ENVELOPE where it matters.
    private static readonly Domain BinaryFieldDomain = new(
        Key: "binary-field",
        Block: 256,
        EdgeFraction: 0.4,
        NeighborhoodFraction: 0.3
    );
    private static readonly Domain BinaryFieldAxioms = new(
        Key: "binary-field-axioms",
        Block: 256,
        EdgeFraction: 0.4,
        NeighborhoodFraction: 0.3
    );
    private static readonly Domain BinaryFieldGroup = new(
        Key: "binary-field-group",
        Block: 256,
        EdgeFraction: 0.4,
        NeighborhoodFraction: 0.3
    );
    // The odd-characteristic bands. NO SublatticeShift anywhere: nothing in this family rounds, so there is no
    // rounding-free band to fold onto, and the WHOLE sixty-four-bit word is a legal operand — reinterpreted as a ulong
    // it is a field element before reduction, and a primality candidate is any ulong at all. Every operand is folded
    // inside the subject AND the reference alike (the established Subjects.ClosedUnitRaw pattern), never by shrinking
    // the sampler. Blocks are 256 rather than the house 512 because every case here iterates its whole modulus ladder
    // per operand, so breadth comes from the ladder and a larger block would buy volume at the tier with the least
    // budget. EdgeFraction stays at the house 0.4: read as a ulong the committed edge set supplies 0, 1, 2^64 − 1,
    // 2^63, 2^63 ± 1, 2^31 ± 1, 2^47 ± 1 and the two's-complement images of every negative raw — exactly the carrier
    // corners a modulus fold and a primality candidate both want, so the mixture is ideal here rather than degenerate.
    // Two keys are SHARED on purpose. The root band is shared by the character law and the square-root law, which is
    // what lets prime-field.sqrt-descent-and-refusal say that the character TrySqrt decides for itself is the same
    // operand stream LegendreCharacter is pinned on rather than a sweep that happens to overlap; the primality band is
    // shared by the three differential laws, so "each half is load-bearing" is a statement about ONE candidate stream.
    private static readonly Domain PrimeFieldBand = new(
        Key: "prime-field",
        Block: 256,
        EdgeFraction: 0.4,
        NeighborhoodFraction: 0.3
    );
    private static readonly Domain PrimeFieldChain = new(
        Key: "prime-field-chain",
        Block: 256,
        EdgeFraction: 0.4,
        NeighborhoodFraction: 0.3
    );
    private static readonly Domain PrimeFieldRoot = new(
        Key: "prime-field-root",
        Block: 256,
        EdgeFraction: 0.4,
        NeighborhoodFraction: 0.3
    );
    private static readonly Domain PrimeFieldPrimality = new(
        Key: "prime-field-primality",
        Block: 256,
        EdgeFraction: 0.4,
        NeighborhoodFraction: 0.3
    );
    private static readonly Domain PrimeFieldLucas = new(
        Key: "prime-field-lucas",
        Block: 256,
        EdgeFraction: 0.4,
        NeighborhoodFraction: 0.3
    );
    // The odd-characteristic EXTENSION bands. Same shape as the prime-field ones and for the same reasons — no
    // SublatticeShift anywhere because nothing here rounds, and the operand fold is C#'s OWN remainder of the
    // reinterpreted word by the ladder's modulus (Subjects.ExtensionResidue, no Puck.Maths call), applied inside the
    // subject AND the reference alike, so the whole sixty-four-bit word is a legal operand. The edge fraction sits BELOW
    // the house default, which is the one place these differ from the prime-field keys: this family's swept cases run
    // p = 7, where that fold collapses the twenty-four committed edge raws onto seven residues, so an edge-heavy mixture
    // would spend its draws on a handful of images. Blocks are 256 rather than the house 512 for budget — every case
    // iterates its whole ladder per operand, so breadth comes from the ladder. Each statement takes its own key so two
    // laws over this type never re-sweep one another's ground, and each Deep mirror REUSES its Default sibling's key so
    // the pair advances one counter together.
    private static readonly Domain ExtensionField = new(
        Key: "extension-field",
        Block: 256,
        EdgeFraction: 0.3,
        NeighborhoodFraction: 0.25
    );
    private static readonly Domain ExtensionFieldNorm = new(
        Key: "extension-field-norm",
        Block: 256,
        EdgeFraction: 0.3,
        NeighborhoodFraction: 0.25
    );
    private static readonly Domain ExtensionFieldInverse = new(
        Key: "extension-field-inverse",
        Block: 256,
        EdgeFraction: 0.3,
        NeighborhoodFraction: 0.25
    );
    private static readonly Domain ExtensionFieldPower = new(
        Key: "extension-field-power",
        Block: 256,
        EdgeFraction: 0.3,
        NeighborhoodFraction: 0.25
    );
    private static readonly Domain ExtensionFieldProduct = new(
        Key: "extension-field-product",
        Block: 256,
        EdgeFraction: 0.3,
        NeighborhoodFraction: 0.25
    );

    /// <summary>Gets every declared law case, across every tier.</summary>
    public static IReadOnlyList<LawCase> All { get; } = Build();
    /// <summary>Gets the case lookup by id.</summary>
    public static IReadOnlyDictionary<string, LawCase> ById { get; } = All.ToDictionary(
        keySelector: lawCase => lawCase.Id,
        comparer: StringComparer.Ordinal
    );

    // Relation coefficients as raw Q16 longs.
    private const long ComplexQ = -65536L;   // (0, −1) → FixedComplex
    private const long HalfQ = 32768L;        // (0, ½)  → a fractional relation, the fused fractional lane
    private const long OneRaw = 65536L;
    private const long SplitQ = 65536L;       // (0, +1) → FixedSplit

    private static IReadOnlyList<LawCase> Build() =>
    [
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
            id: "core.quadratic-surd-field-and-conversion",
            run: () => Laws.Claim(
                claim: CoreSurfaceClaims.QuadraticSurdSurface,
                lawId: "core.quadratic-surd-field-and-conversion"
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
            id: "q1648.peer-conversion-vs-fixedq4816",
            run: () => Laws.Claim(
                claim: Subjects.Q1648PeerConversionExact,
                lawId: "q1648.peer-conversion-vs-fixedq4816"
            )
        ),

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
        // ---- The statistical, lattice and presented-kernel statements, grouped by the surface each interrogates ----
        // ---- Pcg32XshRr vs the reference implementation ----
        Case(
            id: "sampling.pcg-transcribed-reference-and-decorrelation",
            run: () => Laws.Claim(
                claim: SamplingClaims.PcgTranscribedReferenceAndDecorrelationSurface,
                lawId: "sampling.pcg-transcribed-reference-and-decorrelation"
            )
        ),
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
        // ---- field noise + low discrepancy ----
        Case(
            id: "sampling.field-noise-periodicity-canary-and-distribution",
            run: () => Laws.Claim(
                claim: SamplingClaims.FieldNoisePeriodicityAndDistributionSurface,
                lawId: "sampling.field-noise-periodicity-canary-and-distribution"
            )
        ),
        // ---- CertifiedLowDiscrepancy ----
        Case(
            id: "sampling.certified-low-discrepancy-bound-and-teeth",
            run: () => Laws.Claim(
                claim: SamplingClaims.CertifiedLowDiscrepancyBoundTeethAndGapSurface,
                lawId: "sampling.certified-low-discrepancy-bound-and-teeth"
            )
        ),
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
        // ---- binary field: the suite's only CRC statement ----
        Case(
            id: "algebra.binary-polynomial-crc32-published-vector",
            run: () => Laws.Claim(
                claim: ScalarFieldClaims.BinaryPolynomialCrc32PublishedVectorSurface,
                lawId: "algebra.binary-polynomial-crc32-published-vector"
            )
        ),
        // ---- MetallicQuasicrystal random access ----
        Case(
            id: "quasicrystal.metallic-random-access-vs-streamed-word",
            run: () => Laws.Claim(
                claim: QuasicrystalClaims.MetallicRandomAccessMatchesStreamedWord,
                lawId: "quasicrystal.metallic-random-access-vs-streamed-word"
            )
        ),
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
        // ---- QuadraticQuasicrystal.Chain random access ----
        Case(
            id: "quasicrystal.chain-single-term-matches-metallic-and-new-periods",
            run: () => Laws.Claim(
                claim: QuasicrystalClaims.ChainSingleTermMatchesMetallicAndNewPeriodsWalk,
                lawId: "quasicrystal.chain-single-term-matches-metallic-and-new-periods"
            )
        ),
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
            id: "core.rust-port-emitters-are-pure-and-live",
            run: () => Laws.Claim(
                claim: MoveTowardAndEmitterClaims.RustPortEmitterSurface,
                lawId: "core.rust-port-emitters-are-pure-and-live"
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
    /// <summary>Builds a declared case: looks up the id's authored declaration in <see cref="LawDeclarations.All"/>
    /// for the tier, covered members and legs, and pairs it with the run delegate given here — the one part of a case
    /// that genuinely binds to code rather than being describable as data.</summary>
    /// <param name="id">The law id, matched against <see cref="LawDeclarations.All"/>.</param>
    /// <param name="run">The action that runs the case.</param>
    /// <returns>The assembled case.</returns>
    /// <exception cref="InvalidOperationException">The id has no declaration, its tier token does not parse, or a
    /// declared member's type does not resolve in the Puck.Maths assembly.</exception>
    private static LawCase Case(string id, Action run) {
        var declaration = (LawDeclarations.All.TryGetValue(
            key: id,
            value: out var found
        )
            ? found
            : throw new InvalidOperationException(message: $"law id '{id}' has no declaration under tests/Puck.Maths.Tests/laws/.")
        );

        return new(
            Id: id,
            Tier: ParseTier(
                id: id,
                token: declaration.Tier
            ),
            Members: [.. declaration.Members.Select(selector: member => ResolveMember(
                    id: id,
                    member: member
                ))],
            Legs: [.. declaration.Legs.Select(selector: leg => leg.ToLeg())],
            Run: run
        );
    }
    /// <summary>Parses a declared tier token.</summary>
    /// <param name="id">The owning law id, named in the exception if parsing fails.</param>
    /// <param name="token">The tier token, for example <c>"Deep"</c>.</param>
    /// <returns>The parsed tier.</returns>
    /// <exception cref="InvalidOperationException"><paramref name="token"/> names no <see cref="Tier"/> member.</exception>
    private static Tier ParseTier(string id, string token) =>
        (Enum.TryParse<Tier>(
            ignoreCase: false,
            result: out var parsed,
            value: token
        )
            ? parsed
            : throw new InvalidOperationException(message: $"law '{id}' declares tier '{token}', which is not a recognized Tier.")
        );
    /// <summary>Resolves one declared member reference to a <see cref="CoverRef"/> by looking up its declaring type in
    /// the Puck.Maths assembly.</summary>
    /// <param name="id">The owning law id, named in the exception if resolution fails.</param>
    /// <param name="member">The declared member reference.</param>
    /// <returns>The resolved cover reference.</returns>
    /// <remarks>A constructed-generic reference carries the assembly version that was current when its row was
    /// authored; resolution binds any <c>Puck.Maths</c> reference to the loaded assembly regardless of that stamped
    /// version, so a version change cannot orphan the authored declarations.</remarks>
    /// <exception cref="InvalidOperationException"><paramref name="member"/>'s type does not resolve.</exception>
    private static CoverRef ResolveMember(string id, MemberRef member) {
        var type = (Type.GetType(
            typeName: member.Type,
            assemblyResolver: static name => {
                if (name.Name == MathsAssembly.GetName().Name) { return MathsAssembly; }

                try { return Assembly.Load(assemblyRef: name); } catch { return null; }
            },
            typeResolver: static (assembly, name, ignoreCase) => (assembly ?? MathsAssembly).GetType(
                ignoreCase: ignoreCase,
                name: name,
                throwOnError: false
            ),
            throwOnError: false
        ) ?? throw new InvalidOperationException(message: $"law '{id}' names a member of type '{member.Type}', which does not resolve in the Puck.Maths assembly."));

        return new(
            Type: type,
            Name: member.Name
        );
    }
}
