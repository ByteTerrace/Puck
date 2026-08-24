using System.Reflection;

namespace Puck.Maths.Tests;

/// <summary>
/// The seed law instantiations — the fixed-point algebra cluster proved end-to-end. Each combinator from
/// <see cref="Laws"/> is instantiated per subject here, once, as a declaration. This is the only place subjects, oracles,
/// domains, and covered members meet. Every case here is a law <see cref="LawTests"/> executes as a theory row, so a
/// member the coverage module credits is always credited to a case that runs and asserts; timing work has no
/// declaration here (<see cref="BenchTests"/> owns the bench outright).
/// </summary>
internal static partial class LawRegistry {
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
        .. SmokeCases(),
        .. RootCoreCases(),
        .. FixedQ4816Cases(),
        .. FixedQ1648Cases(),
        .. FixedQ3232Cases(),
        .. ContributionFoldCases(),
        .. UFixedQ4816Cases(),
        .. UnitInterval32Cases(),
        .. UnitFractionCases(),
        .. ComplexRelationCases(),
        .. SplitRelationCases(),
        .. DualRelationCases(),
        .. ComplexRestCases(),
        .. SplitRestCases(),
        .. DualRestCases(),
        .. QuaternionCases(),
        .. FractionalRelationCases(),
        .. MobiusCases(),
        .. IntegerDivisionCases(),
        .. PresentedChargedAlgebraCases(),
        .. Phase3CoherenceCases(),
        .. Phase3GroupRegimeCases(),
        .. Phase4BoundaryCases(),
        .. Phase4BraidingCases(),
        .. Phase4MorphismsCases(),
        .. Phase4GraphZetaCases(),
        .. Phase4SecondProductCases(),
        .. Phase4KnotStateSumCases(),
        .. Phase3SecondKernelCases(),
        .. UnitIntervalMaterialFamilyCases(),
        .. Phase2ModulesCases(),
        .. ContinuedFractionLensCases(),
        .. FixedVectorCases(),
        .. FixedPositionCases(),
        .. FixedRigidTransformCases(),
        .. RateAccumulatorCases(),
        .. SymmetricSolveCases(),
        .. MixedScaleCases(),
        .. DirectedRoundingCases(),
        .. MassPropertiesCases(),
        .. BinaryPolynomialRingCases(),
        .. BinaryFieldQuotientCases(),
        .. PrimeFieldCases(),
        .. ExtensionFieldCases(),
        .. SamplingRefusalCases(),
        .. DeepEdgeCrossCases(),
        .. PcgReferenceCases(),
        .. Log2GaussianAliasCases(),
        .. FieldNoiseCases(),
        .. CertifiedLowDiscrepancyCases(),
        .. SymmetryLatticeCases(),
        .. HilbertCurveCases(),
        .. HexagonalCoordinateCases(),
        .. ScalarSpecificationCases(),
        .. BinaryFieldCrcCases(),
        .. MetallicQuasicrystalAccessCases(),
        .. ModularTransformCases(),
        .. QuadraticInflationCases(),
        .. QuadraticQuasicrystalCases(),
        .. QuadraticQuasicrystalChainCases(),
        .. QuaternionDualStatCases(),
        .. Vector2WedgeDotCases(),
        .. ComplexRigidStatCases(),
        .. PresentedAlgebraSurfaceCases(),
        .. StageSweepCases(),
        .. PresentedStructureCases(),
        .. MeetCases(),
        .. NttCases(),
        .. FftCases(),
        .. DynamicsCases(),
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
