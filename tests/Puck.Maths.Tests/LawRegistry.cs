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

    // Relation coefficients as raw Q16 longs.
    private const long ComplexQ = -65536L;   // (0, −1) → FixedComplex
    private const long SplitQ = 65536L;       // (0, +1) → FixedSplit
    private const long HalfQ = 32768L;        // (0, ½)  → a fractional relation, the fused fractional lane
    private const long OneRaw = 65536L;

    private static readonly Domain Complex = new(Key: "complex", Block: 512, EdgeFraction: 0.4, NeighborhoodFraction: 0.3);
    private static readonly Domain Split = new(Key: "split", Block: 512, EdgeFraction: 0.4, NeighborhoodFraction: 0.3);
    private static readonly Domain Dual = new(Key: "dual", Block: 512, EdgeFraction: 0.4, NeighborhoodFraction: 0.3);
    private static readonly Domain CliffordPlanarComplex = new(Key: "clifford-planar-complex", Block: 512, EdgeFraction: 0.4, NeighborhoodFraction: 0.3);
    private static readonly Domain CliffordPlanarSplit = new(Key: "clifford-planar-split", Block: 512, EdgeFraction: 0.4, NeighborhoodFraction: 0.3);
    private static readonly Domain CliffordPlanarDual = new(Key: "clifford-planar-dual", Block: 512, EdgeFraction: 0.4, NeighborhoodFraction: 0.3);
    private static readonly Domain CliffordQuaternionEven = new(Key: "clifford-quaternion-even", Block: 512, EdgeFraction: 0.4, NeighborhoodFraction: 0.3);
    private static readonly Domain CliffordMotor = new(Key: "clifford-motor", Block: 512, EdgeFraction: 0.4, NeighborhoodFraction: 0.3);
    private static readonly Domain CliffordReverse = new(Key: "clifford-reverse", Block: 512, EdgeFraction: 0.4, NeighborhoodFraction: 0.3);
    private static readonly Domain CliffordMultivector = new(Key: "clifford-multivector", Block: 512, EdgeFraction: 0.4, NeighborhoodFraction: 0.3);
    private static readonly Domain MonogenicExact = new(Key: "monogenic-exact", Block: 512, EdgeFraction: 0.4, NeighborhoodFraction: 0.3);
    private static readonly Domain MonogenicFusion = new(Key: "monogenic-fusion", Block: 512, EdgeFraction: 0.4, NeighborhoodFraction: 0.3);
    private static readonly Domain Fractional = new(Key: "algebra-fractional", Block: 512, EdgeFraction: 0.4, NeighborhoodFraction: 0.3);
    private static readonly Domain Mobius = new(Key: "mobius", Block: 512, EdgeFraction: 0.4, NeighborhoodFraction: 0.3);
    private static readonly Domain Scalar = new(Key: "scalar", Block: 512, EdgeFraction: 0.4, NeighborhoodFraction: 0.3);
    private static readonly Domain ScalarDivision = new(Key: "scalar-division", Block: 512, EdgeFraction: 0.4, NeighborhoodFraction: 0.3);
    // The transcendental band. A LOWER edge fraction than the house default on purpose: the shared edge set is mostly
    // extremes, and every transcendental subject folds its operand onto a band, so an edge-heavy mixture would spend
    // most of its draws on a handful of folded images instead of sweeping the 128-interval tables.
    private static readonly Domain ScalarTranscendental = new(Key: "scalar-transcendental", Block: 512, EdgeFraction: 0.25, NeighborhoodFraction: 0.25);
    private static readonly Domain ScalarText = new(Key: "scalar-text", Block: 512, EdgeFraction: 0.4, NeighborhoodFraction: 0.3);
    // The contribution fold consumes more independent raw values than the scalar binary combinators expose, so its
    // claims take vector pairs and map them to valid configurations inside the claim. Each sampled statement owns a
    // frontier key; all stay full-width because Int128 totality at the signed-carrier edges is part of the contract.
    private static readonly Domain ContributionFoldFormula = new(Key: "contribution-fold-formula", Block: 512, EdgeFraction: 0.4, NeighborhoodFraction: 0.3);
    private static readonly Domain ContributionFoldNoPool = new(Key: "contribution-fold-no-pool", Block: 512, EdgeFraction: 0.4, NeighborhoodFraction: 0.3);
    private static readonly Domain ContributionFoldOrder = new(Key: "contribution-fold-order", Block: 512, EdgeFraction: 0.4, NeighborhoodFraction: 0.3);
    private static readonly Domain ContributionFoldAnalog = new(Key: "contribution-fold-analog", Block: 512, EdgeFraction: 0.4, NeighborhoodFraction: 0.3);
    private static readonly Domain ContributionFoldQuantization = new(Key: "contribution-fold-quantization", Block: 512, EdgeFraction: 0.4, NeighborhoodFraction: 0.3);
    private static readonly Domain Sublattice = new(Key: "sublattice", Block: 256, EdgeFraction: 0.3, NeighborhoodFraction: 0.3, SublatticeShift: 16);
    private static readonly Domain SmokeDomain = new(Key: "smoke", Block: 64, EdgeFraction: 0.5, NeighborhoodFraction: 0.3);
    private static readonly Domain Presented = new(Key: "presented", Block: 512, EdgeFraction: 0.4, NeighborhoodFraction: 0.3);
    private static readonly Domain ClosedUnit = new(Key: "closed-unit", Block: 512, EdgeFraction: 0.4, NeighborhoodFraction: 0.3);
    private static readonly Domain UnitFraction16Domain = new(Key: "unit-fraction16", Block: 512, EdgeFraction: 0.4, NeighborhoodFraction: 0.3);
    private static readonly Domain UnitFraction32Domain = new(Key: "unit-fraction32", Block: 512, EdgeFraction: 0.4, NeighborhoodFraction: 0.3);
    // The unsigned Q48.16 band. NO sublattice: every law over it either rounds once against an exact oracle or is exact
    // by construction, and the operand fold is a plain bit reinterpretation, so the whole sixty-four-bit word is legal.
    private static readonly Domain UnsignedScalar = new(Key: "unsigned-scalar", Block: 512, EdgeFraction: 0.4, NeighborhoodFraction: 0.3);
    // The signed Q16.48 band (FixedQ1648) — sixteen integer bits, forty-eight fraction bits, a range/resolution lean
    // opposite FixedQ4816's own. Same shared EdgeRaws battery as every other signed sixty-four-bit carrier; its own
    // peer-conversion boundary (the sixteen-bit integer range) is swept separately by its own fixed ladder, not by
    // this domain.
    private static readonly Domain Q1648Scalar = new(Key: "q1648-scalar", Block: 512, EdgeFraction: 0.4, NeighborhoodFraction: 0.3);
    private static readonly Domain Q1648ScalarDivision = new(Key: "q1648-scalar-division", Block: 512, EdgeFraction: 0.4, NeighborhoodFraction: 0.3);
    // The signed Q32.32 band (FixedQ3232) — an even thirty-two/thirty-two split, the balanced point between
    // FixedQ4816's and FixedQ1648's opposite leans. Same shared EdgeRaws battery; its own peer-conversion boundary
    // (the thirty-two-bit integer range) is swept separately by its own fixed ladder, not by this domain.
    private static readonly Domain Q3232Scalar = new(Key: "q3232-scalar", Block: 512, EdgeFraction: 0.4, NeighborhoodFraction: 0.3);
    private static readonly Domain Q3232ScalarDivision = new(Key: "q3232-scalar-division", Block: 512, EdgeFraction: 0.4, NeighborhoodFraction: 0.3);

    // The attenuation bands — one key per sampled meet statement, the contribution-fold discipline, so distinct
    // statements never re-sweep one another's ground. All full-width and NO sublattice: the carriers are total on the
    // plain bit reinterpretation of a lane, so every committed edge raw is legal, and the battery's 0 and −1 land
    // exactly on Bottom and Top.
    private static readonly Domain MeetAssociative = new(Key: "meet-associative", Block: 512, EdgeFraction: 0.4, NeighborhoodFraction: 0.3);
    private static readonly Domain MeetBottomAbsorption = new(Key: "meet-bottom-absorption", Block: 512, EdgeFraction: 0.4, NeighborhoodFraction: 0.3);
    private static readonly Domain MeetCommutative = new(Key: "meet-commutative", Block: 512, EdgeFraction: 0.4, NeighborhoodFraction: 0.3);
    private static readonly Domain MeetIdempotent = new(Key: "meet-idempotent", Block: 512, EdgeFraction: 0.4, NeighborhoodFraction: 0.3);
    private static readonly Domain MeetMonotonicity = new(Key: "meet-monotonicity", Block: 512, EdgeFraction: 0.4, NeighborhoodFraction: 0.3);
    private static readonly Domain MeetOrderCoherence = new(Key: "meet-order-coherence", Block: 512, EdgeFraction: 0.4, NeighborhoodFraction: 0.3);
    private static readonly Domain MeetProductComposition = new(Key: "meet-product-composition", Block: 512, EdgeFraction: 0.4, NeighborhoodFraction: 0.3);
    private static readonly Domain MeetTopIdentity = new(Key: "meet-top-identity", Block: 512, EdgeFraction: 0.4, NeighborhoodFraction: 0.3);

    // The hypercomplex bands. Each construction gets its own key so the frontier advances independently and two laws
    // over the same type never re-sweep one another's ground. Every narrow/wide gate in the family sits at 2¹⁷, 2²⁹,
    // 2³⁰, 2³¹, 2⁴⁰, 2⁴² or 2⁴⁵, and the committed edge set carries raws strictly on both sides of each, so every case
    // below straddles its own gate.
    private static readonly Domain ComplexDivide = new(Key: "complex-divide", Block: 512, EdgeFraction: 0.4, NeighborhoodFraction: 0.3);
    private static readonly Domain ComplexDirection = new(Key: "complex-direction", Block: 512, EdgeFraction: 0.4, NeighborhoodFraction: 0.3);
    private static readonly Domain ComplexRotate = new(Key: "complex-rotate", Block: 512, EdgeFraction: 0.4, NeighborhoodFraction: 0.3);
    private static readonly Domain SplitDivide = new(Key: "split-divide", Block: 512, EdgeFraction: 0.4, NeighborhoodFraction: 0.3);
    private static readonly Domain SplitTransform = new(Key: "split-transform", Block: 512, EdgeFraction: 0.4, NeighborhoodFraction: 0.3);
    private static readonly Domain DualDivide = new(Key: "dual-divide", Block: 512, EdgeFraction: 0.4, NeighborhoodFraction: 0.3);
    private static readonly Domain DualGeneric = new(Key: "dual-generic", Block: 512, EdgeFraction: 0.4, NeighborhoodFraction: 0.3);
    private static readonly Domain DualQuaternion = new(Key: "dual-quaternion", Block: 512, EdgeFraction: 0.4, NeighborhoodFraction: 0.3);
    private static readonly Domain Quaternion = new(Key: "quaternion", Block: 512, EdgeFraction: 0.4, NeighborhoodFraction: 0.3);
    private static readonly Domain QuaternionDirection = new(Key: "quaternion-direction", Block: 512, EdgeFraction: 0.4, NeighborhoodFraction: 0.3);
    private static readonly Domain QuaternionRotate = new(Key: "quaternion-rotate", Block: 512, EdgeFraction: 0.4, NeighborhoodFraction: 0.3);
    private static readonly Domain QuaternionSublattice = new(Key: "quaternion-sublattice", Block: 256, EdgeFraction: 0.3, NeighborhoodFraction: 0.3, SublatticeShift: 16);

    // The vector bands. Each is the full signed range at the DOMAIN level; where a law needs a narrower operand it
    // folds inside the subject AND the oracle, the established Subjects.ClosedUnitRaw pattern, rather than shrinking
    // the sampler. The norm band is a separate key from the product band on purpose: the norms' refusal boundary is a
    // different region of the space from the products' rounding boundary, so the two sweep independent progressive
    // ground. Only the lattice band folds at the domain level, because its whole point is that nothing rounds.
    private static readonly Domain Vector = new(Key: "vector", Block: 512, EdgeFraction: 0.4, NeighborhoodFraction: 0.3);
    private static readonly Domain VectorNorm = new(Key: "vector-norm", Block: 512, EdgeFraction: 0.4, NeighborhoodFraction: 0.3);
    private static readonly Domain VectorNarrow = new(Key: "vector-narrow", Block: 512, EdgeFraction: 0.4, NeighborhoodFraction: 0.3);
    private static readonly Domain VectorDirection = new(Key: "vector-direction", Block: 512, EdgeFraction: 0.3, NeighborhoodFraction: 0.3);
    private static readonly Domain VectorLattice = new(Key: "vector-lattice", Block: 512, EdgeFraction: 0.3, NeighborhoodFraction: 0.3, SublatticeShift: 16);

    // The kinematics bands. All full-range and NO sublattice anywhere: the position and rate statements are exact
    // integer arithmetic that needs the whole raw space (the cell-index extremes ARE the interesting operands, and the
    // refusals are part of the contract), and the rigid transform's inexact statements are pinned by hand-derived
    // ladders rather than by lattices. Each construction gets its own key so two laws over one type never re-sweep each
    // other's ground. Every gate this family branches on has committed edge raws strictly on both sides: the carry
    // shift's wrap at long.MaxValue, TryTranslate's overflow into the Int128 canonicalizer, TryDelta's conservative 2²⁶
    // gate at ±2⁴⁷ cells, the dual-quaternion product's 2²⁹ and 2¹⁷/2⁴² gates at ±2³¹ and ±2⁴⁷, the rotation sandwich's
    // 2¹⁷/2⁴⁰ gate at ±65536 and ±2⁴⁷, and the normalizer's band at ±2⁴⁷.
    private static readonly Domain Position = new(Key: "position", Block: 512, EdgeFraction: 0.4, NeighborhoodFraction: 0.3);
    private static readonly Domain PositionDelta = new(Key: "position-delta", Block: 512, EdgeFraction: 0.4, NeighborhoodFraction: 0.3);
    private static readonly Domain PositionTranslate = new(Key: "position-translate", Block: 512, EdgeFraction: 0.4, NeighborhoodFraction: 0.3);
    private static readonly Domain Rigid = new(Key: "rigid", Block: 512, EdgeFraction: 0.4, NeighborhoodFraction: 0.3);
    private static readonly Domain RigidDirection = new(Key: "rigid-direction", Block: 512, EdgeFraction: 0.4, NeighborhoodFraction: 0.3);
    private static readonly Domain RigidPoint = new(Key: "rigid-point", Block: 512, EdgeFraction: 0.4, NeighborhoodFraction: 0.3);
    private static readonly Domain Rate = new(Key: "rate", Block: 512, EdgeFraction: 0.4, NeighborhoodFraction: 0.3);

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
    private static readonly Domain SymmetricSolve2 = new(Key: "symmetric-solve2", Block: 512, EdgeFraction: 0.4, NeighborhoodFraction: 0.3);
    private static readonly Domain SymmetricSolve3 = new(Key: "symmetric-solve3", Block: 512, EdgeFraction: 0.4, NeighborhoodFraction: 0.3);
    private static readonly Domain SymmetricInvert2 = new(Key: "symmetric-invert2", Block: 256, EdgeFraction: 0.25, NeighborhoodFraction: 0.3);
    private static readonly Domain SymmetricInvert3 = new(Key: "symmetric-invert3", Block: 256, EdgeFraction: 0.25, NeighborhoodFraction: 0.3);

    // The apply bands. NO fold anywhere, at the domain or in the claim: a matrix-times-vector component is a sum of at
    // most three raw products, bounded by 3·2^126, so the sign-plus-UInt128 accumulator is exact over the whole signed
    // range and there is no preconditioning envelope to stay inside — the reason Solve's and Invert's claims fold and
    // Apply's do not.
    private static readonly Domain SymmetricApply2 = new(Key: "symmetric-apply2", Block: 512, EdgeFraction: 0.4, NeighborhoodFraction: 0.3);
    private static readonly Domain SymmetricApply3 = new(Key: "symmetric-apply3", Block: 512, EdgeFraction: 0.4, NeighborhoodFraction: 0.3);

    // The mixed-scale and directed-rounding bands. Both are full-width at the domain and fold only the FRACTION BIT
    // COUNTS inside the claim (onto [0, 64]), because the counts are the operand whose extremes would otherwise put
    // the oracle's own power-of-two denominator past any width; the shift-count corners those folds exclude are pinned
    // by their own hand-derived claim instead. The directed band folds its value operands onto the non-negative half
    // by one logical shift, which preserves each committed edge raw's bit pattern rather than collapsing the battery.
    private static readonly Domain MixedScale = new(Key: "mixed-scale", Block: 512, EdgeFraction: 0.4, NeighborhoodFraction: 0.3);
    private static readonly Domain MixedScaleTriple = new(Key: "mixed-scale-triple", Block: 512, EdgeFraction: 0.4, NeighborhoodFraction: 0.3);
    private static readonly Domain DirectedRoot = new(Key: "directed-root", Block: 512, EdgeFraction: 0.4, NeighborhoodFraction: 0.3);
    private static readonly Domain DirectedProduct = new(Key: "directed-product", Block: 512, EdgeFraction: 0.4, NeighborhoodFraction: 0.3);
    private static readonly Domain DirectedQuotient = new(Key: "directed-quotient", Block: 512, EdgeFraction: 0.4, NeighborhoodFraction: 0.3);
    private static readonly Domain DirectedProductSum = new(Key: "directed-product-sum", Block: 512, EdgeFraction: 0.4, NeighborhoodFraction: 0.3);
    private static readonly Domain DirectedMagnitude = new(Key: "directed-magnitude", Block: 512, EdgeFraction: 0.4, NeighborhoodFraction: 0.3);

    // The mass-property bands, one key per shape family so a sphere law and a capsule law never re-sweep each other's
    // ground. The blocks are HALF the house 512: every draw here forms several BigInteger products of a few hundred
    // bits on both the subject's and the oracle's side, which is the cost, and the Default tier's random batch already
    // dominates the sweep.
    private static readonly Domain MassVolume = new(Key: "mass-volume", Block: 256, EdgeFraction: 0.4, NeighborhoodFraction: 0.3);
    private static readonly Domain MassSphere = new(Key: "mass-sphere", Block: 256, EdgeFraction: 0.4, NeighborhoodFraction: 0.3);
    private static readonly Domain MassBox = new(Key: "mass-box", Block: 256, EdgeFraction: 0.4, NeighborhoodFraction: 0.3);
    private static readonly Domain MassCylinder = new(Key: "mass-cylinder", Block: 256, EdgeFraction: 0.4, NeighborhoodFraction: 0.3);
    private static readonly Domain MassCapsule = new(Key: "mass-capsule", Block: 256, EdgeFraction: 0.4, NeighborhoodFraction: 0.3);
    private static readonly Domain MassParallelAxis = new(Key: "mass-parallel-axis", Block: 256, EdgeFraction: 0.4, NeighborhoodFraction: 0.3);
    private static readonly Domain MassCompound = new(Key: "mass-compound", Block: 256, EdgeFraction: 0.4, NeighborhoodFraction: 0.3);

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
    private static readonly Domain BinaryPolynomialRing = new(Key: "binary-polynomial", Block: 256, EdgeFraction: 0.4, NeighborhoodFraction: 0.3);
    private static readonly Domain BinaryPolynomialDivision = new(Key: "binary-polynomial-division", Block: 256, EdgeFraction: 0.4, NeighborhoodFraction: 0.3);
    private static readonly Domain BinaryPolynomialGcd = new(Key: "binary-polynomial-gcd", Block: 256, EdgeFraction: 0.4, NeighborhoodFraction: 0.3);

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
    private static readonly Domain BinaryFieldDomain = new(Key: "binary-field", Block: 256, EdgeFraction: 0.4, NeighborhoodFraction: 0.3);
    private static readonly Domain BinaryFieldAxioms = new(Key: "binary-field-axioms", Block: 256, EdgeFraction: 0.4, NeighborhoodFraction: 0.3);
    private static readonly Domain BinaryFieldGroup = new(Key: "binary-field-group", Block: 256, EdgeFraction: 0.4, NeighborhoodFraction: 0.3);

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
    private static readonly Domain PrimeFieldBand = new(Key: "prime-field", Block: 256, EdgeFraction: 0.4, NeighborhoodFraction: 0.3);
    private static readonly Domain PrimeFieldChain = new(Key: "prime-field-chain", Block: 256, EdgeFraction: 0.4, NeighborhoodFraction: 0.3);
    private static readonly Domain PrimeFieldRoot = new(Key: "prime-field-root", Block: 256, EdgeFraction: 0.4, NeighborhoodFraction: 0.3);
    private static readonly Domain PrimeFieldPrimality = new(Key: "prime-field-primality", Block: 256, EdgeFraction: 0.4, NeighborhoodFraction: 0.3);
    private static readonly Domain PrimeFieldLucas = new(Key: "prime-field-lucas", Block: 256, EdgeFraction: 0.4, NeighborhoodFraction: 0.3);

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
    private static readonly Domain ExtensionField = new(Key: "extension-field", Block: 256, EdgeFraction: 0.3, NeighborhoodFraction: 0.25);
    private static readonly Domain ExtensionFieldNorm = new(Key: "extension-field-norm", Block: 256, EdgeFraction: 0.3, NeighborhoodFraction: 0.25);
    private static readonly Domain ExtensionFieldInverse = new(Key: "extension-field-inverse", Block: 256, EdgeFraction: 0.3, NeighborhoodFraction: 0.25);
    private static readonly Domain ExtensionFieldPower = new(Key: "extension-field-power", Block: 256, EdgeFraction: 0.3, NeighborhoodFraction: 0.25);
    private static readonly Domain ExtensionFieldProduct = new(Key: "extension-field-product", Block: 256, EdgeFraction: 0.3, NeighborhoodFraction: 0.25);

    /// <summary>Gets every declared law case, across every tier.</summary>
    public static IReadOnlyList<LawCase> All { get; } = Build();

    /// <summary>Gets the case lookup by id.</summary>
    public static IReadOnlyDictionary<string, LawCase> ById { get; } = All.ToDictionary(keySelector: lawCase => lawCase.Id, comparer: StringComparer.Ordinal);

    private static IReadOnlyList<LawCase> Build() =>
    [
        // ---- Smoke: the folded originals, tiny domains, under two seconds ----
        Case("smoke.complex-twin-quad", () => Laws.TwinBinary(lawId: "smoke.complex-twin-quad", domain: SmokeDomain, tier: Tier.Smoke, first: Subjects.ComplexMultiply, second: Subjects.AlgebraMultiply(pRaw: 0L, qRaw: ComplexQ), witness: Subjects.MultiplyOracle(pRaw: 0L, qRaw: ComplexQ))),
        Case("smoke.fixed-mul-ties-to-even", () => Laws.ScalarBinaryMatchesOracle(lawId: "smoke.fixed-mul-ties-to-even", domain: SmokeDomain, tier: Tier.Smoke, subject: Subjects.FixedMultiply, oracle: Subjects.FixedMultiplyOracle)),
        // The carrier's other rounding kernel, and the hottest one the multiply's mirror does not already cover.
        Case("smoke.fixed-divide-ties-to-even", () => Laws.ScalarBinaryMatchesOracle(lawId: "smoke.fixed-divide-ties-to-even", domain: SmokeDomain, tier: Tier.Smoke, subject: Subjects.FixedDivide, oracle: Subjects.FixedDivideOracle)),
        Case("smoke.closed-unit-mul-ties-to-even", () => Laws.ScalarBinaryMatchesOracle(lawId: "smoke.closed-unit-mul-ties-to-even", domain: SmokeDomain, tier: Tier.Smoke, subject: Subjects.ClosedUnitMultiply, oracle: Subjects.ClosedUnitMultiplyOracle)),
        // The family's hottest kernel: UnitFraction32 is what the sampling tier RETURNS — Pcg32XshRr.NextUnitFraction32,
        // LowDiscrepancy.R1/R2 and CertifiedLowDiscrepancy.Point all produce it — so blending two sampled fractions is the
        // operation a consumer reaches on the hot path, and it is the one carrying a full-width 32×32→64 product.
        Case("smoke.unit-fraction32-mul-ties-to-even", () => Laws.ScalarBinaryMatchesOracle(lawId: "smoke.unit-fraction32-mul-ties-to-even", domain: SmokeDomain, tier: Tier.Smoke, subject: Subjects.UnitFraction32Multiply, oracle: Subjects.UnitFraction32MultiplyOracle)),
        // The vector family's hottest kernels. FixedVector2.Dot and Wedge are the two scalar fused products every
        // consumer of the family reaches — the rotation seam in FixedComplex.Rotate/FromTo, every projection and every
        // winding test — and the BinaryElemOp shape gives Smoke the budget-bounded edge battery over BOTH operands for
        // essentially no time. FixedVector3.Cross is the same two-term fused shape three times over and is mirrored at
        // Deep instead.
        Case("smoke.vector-fused-products-one-rounding", () => Laws.BinaryMatchesOracle(lawId: "smoke.vector-fused-products-one-rounding", domain: SmokeDomain, tier: Tier.Smoke, subject: Subjects.PlaneProducts, oracle: Subjects.PlaneProductsOracle)),
        // The unsigned carrier's hottest kernel, and the same choice the two scalar smoke rows above make.
        Case("smoke.unsigned-scalar-mul-ties-to-even", () => Laws.ScalarBinaryMatchesOracle(lawId: "smoke.unsigned-scalar-mul-ties-to-even", domain: SmokeDomain, tier: Tier.Smoke, subject: Subjects.UnsignedFixedMultiply, oracle: Subjects.UnsignedFixedMultiplyOracle)),
        // The family's hot path: every rotation compose, every dual-quaternion product inside FixedRigidTransform, every
        // Slerp and every FromTo runs through the Hamilton product, and the planar side already has smoke.complex-twin-quad.
        Case("smoke.quaternion-mul-vs-oracle", () => Laws.VectorMatchesOracle(lawId: "smoke.quaternion-mul-vs-oracle", domain: SmokeDomain, tier: Tier.Smoke, width: 4, subject: Subjects.QuaternionMultiplyLanes, oracle: Subjects.QuaternionMultiplyOracle)),
        // The kinematics family's hottest kernel and the widest fused accumulator it drives: every scene-graph compose,
        // every ComposeNormalized and both dual products inside ScLerp run through it, and each of its eight lanes
        // accumulates eight leaf Q32 products before a single rounding. FixedPosition.Delta is the runner-up — one call
        // per rendered entity — but its whole statement is exact integer arithmetic and gains far more from Deep's
        // exhaustive edge sweep than from a second fast mirror, so it takes a Deep mirror instead.
        Case("smoke.rigid-compose-vs-oracle", () => Laws.VectorMatchesOracle(lawId: "smoke.rigid-compose-vs-oracle", domain: SmokeDomain, tier: Tier.Smoke, width: 8, subject: Subjects.RigidComposeLanes, oracle: Subjects.RigidComposeOracle)),
        Case("smoke.mobius-integer-exact", () => Laws.MobiusMatchesOracle(lawId: "smoke.mobius-integer-exact", domain: SmokeDomain, tier: Tier.Smoke, subject: Subjects.AlgebraMobius(pRaw: OneRaw, qRaw: OneRaw), oracleNumerator: Subjects.MobiusNumeratorOracle(pRaw: OneRaw, qRaw: OneRaw))),
        Case("smoke.presented-complex-twin", () => Laws.VectorTwin(lawId: "smoke.presented-complex-twin", domain: SmokeDomain, tier: Tier.Smoke, width: 2, first: Subjects.PresentedQuadraticMultiply(pRaw: 0L, qRaw: ComplexQ), second: Subjects.ComplexMultiplyLanes, witness: Subjects.QuadraticMultiplyLanesOracle(pRaw: 0L, qRaw: ComplexQ))),
        Case("smoke.presented-boolean-star", () => Laws.VectorMatchesOracle(lawId: "smoke.presented-boolean-star", domain: SmokeDomain, tier: Tier.Smoke, width: (Subjects.GraphOrder * Subjects.GraphOrder), subject: Subjects.PresentedBooleanStar(), oracle: Subjects.BooleanStarOracle)),
        // The refusal is the law: an unguarded star over a cyclic counting quiver must RETURN its obstruction, never
        // throw and never invent a certificate, while the exact finite truncation stays available beside it.
        Case("smoke.presented-star-unguarded-refuses", () => Laws.Claim(lawId: "smoke.presented-star-unguarded-refuses", claim: Subjects.UnguardedStarRefuses)),
        // The zero-allocation overload is the SAME loop writing caller buffers, so it is a twin rather than a variant.
        Case("smoke.presented-multiply-into-twin", () => Laws.VectorTwin(lawId: "smoke.presented-multiply-into-twin", domain: SmokeDomain, tier: Tier.Smoke, width: 8, first: Subjects.PresentedCliffordMultiplyInto(positiveCount: 3, negativeCount: 0, degenerateCount: 0), second: Subjects.PresentedCliffordMultiply(positiveCount: 3, negativeCount: 0, degenerateCount: 0), witness: null)),
        // Ledger row 15 in its smallest form: the residual at the identity twist IS the chain rule the dual number lifts.
        Case("smoke.presented-jet-residual-twin", () => Laws.TwinBinary(lawId: "smoke.presented-jet-residual-twin", domain: SmokeDomain, tier: Tier.Smoke, first: Subjects.PresentedJetResidual(), second: Subjects.DualChainRuleLift, witness: Subjects.JetResidualOracle)),
        // Co-arity greater than one at its smallest: the six planar diagrams of width two, their whole composition
        // table against the arc-tracing oracle, and the sum of the identity diagrams fixing every one of them.
        Case("smoke.presented-tangle-composes", () => Laws.Claim(lawId: "smoke.presented-tangle-composes", claim: Subjects.TangleComposesAtSmokeWidth)),
        // Ledger row 19's transfer, named: the morphism out of the free monoid on one letter per partial quotient,
        // carrying each to that quotient's digit element, reaches the element the transfer's own fold reaches and the
        // entries its module run reads out.
        Case("smoke.presented-functor-twin", () => Laws.Claim(lawId: "smoke.presented-functor-twin", claim: Subjects.FunctorTwinsTransfer)),
        // The second product at its smallest: the seven words of two letters at a window of two, their whole table
        // against the interleaving enumeration, and the one-letter quasi-shuffle whose collisions are already there.
        Case("smoke.presented-shuffle-twin", () => Laws.Claim(lawId: "smoke.presented-shuffle-twin", claim: Subjects.ShuffleComposesAtSmokeWindow)),

        // ---- Root Core/Sampling public-surface laws ----
        Case("sampling.bit-mix-constants-invert", () => Laws.Claim(lawId: "sampling.bit-mix-constants-invert", claim: CoreSurfaceClaims.BitMixConstantsInvertSurface)),
        Case("sampling.bit-mix-is-a-permutation", () => Laws.Claim(lawId: "sampling.bit-mix-is-a-permutation", claim: CoreSurfaceClaims.BitMixIsAPermutationSurface)),
        Case("scalar.cyclic-rotation-closes-its-loop", () => Laws.Claim(lawId: "scalar.cyclic-rotation-closes-its-loop", claim: CoreSurfaceClaims.CyclicRotationStructureSurface)),
        Case("core.big-integer-is-prime-vs-oracle", () => Laws.Claim(lawId: "core.big-integer-is-prime-vs-oracle", claim: CoreSurfaceClaims.BigIntegerIsPrimeSurface)),
        Case("core.big-integer-prime-factors-vs-word-kernel", () => Laws.Claim(lawId: "core.big-integer-prime-factors-vs-word-kernel", claim: CoreSurfaceClaims.BigIntegerPrimeFactorsSurface)),

        // The full-carrier scale the case above only samples, plus the Jacobi statement it cannot make. The Jacobi law
        // sits at Deep rather than Exhaustive on purpose: it is a two-second grid of statements, not a carrier-wide
        // sweep, so parking it behind an opt-in tier would cost it its everyday coverage. Its oracle is the
        // factor-and-Euler DEFINITION, with no reciprocity step anywhere in it, so at composite moduli it cannot pick
        // the same wrong value as the subject by running the library's own sibling descent on both sides.
        // The two the factorization surface could not make before: it took the gate's word at the one value the twelve
        // bases decide wrongly, and its depth was the operand's multiplicity rather than the heap.
        Case("core.witness-set-boundary-factors-exactly", () => Laws.Claim(lawId: "core.witness-set-boundary-factors-exactly", claim: CoreSurfaceClaims.WitnessSetBoundaryFactorsExactly)),
        Case("core.prime-counting-is-dense-against-a-sieve", () => Laws.Claim(lawId: "core.prime-counting-is-dense-against-a-sieve", claim: CoreSurfaceClaims.PrimeCountingIsDenseAgainstASieve)),
        Case("core.deep-multiplicity-factors-without-stack-growth", () => Laws.Claim(lawId: "core.deep-multiplicity-factors-without-stack-growth", claim: CoreSurfaceClaims.DeepMultiplicityFactorsWithoutStackGrowth)),

        Case("core.jacobi-symbol-cross-carrier", () => Laws.Claim(lawId: "core.jacobi-symbol-cross-carrier", claim: PrimalityScaleClaims.JacobiSymbolSurface)),

        // Descriptors and elements that compared unequal to themselves, and an exponential that answered outside the
        // subdomain it documents instead of refusing. All three were silent: array identity masquerading as value
        // equality, and a closed form read off the scalar lane of a square that was not scalar.
        Case("algebra.clifford-descriptor-identity-is-the-signature", () => Laws.Claim(lawId: "algebra.clifford-descriptor-identity-is-the-signature", claim: GeometricAlgebraClaims.CliffordDescriptorIdentitySurface)),
        Case("algebra.clifford-exponential-scalar-square-domain", () => Laws.Claim(lawId: "algebra.clifford-exponential-scalar-square-domain", claim: GeometricAlgebraClaims.CliffordExponentialDomainSurface)),
        Case("algebra.monogenic-identity-is-tail-and-coordinates", () => Laws.Claim(lawId: "algebra.monogenic-identity-is-tail-and-coordinates", claim: GeometricAlgebraClaims.MonogenicIdentitySurface)),

        // Carriers that admitted values they advertise they do not hold, a letter mask that broadened a split predicate
        // into a false positive, and colour indices that were never checked at all.
        Case("presented.rational-material-admits-only-rationals", () => Laws.Claim(lawId: "presented.rational-material-admits-only-rationals", claim: OracleClaims.RationalMaterialAdmitsOnlyRationals)),
        Case("presented.counting-material-admits-only-naturals", () => Laws.Claim(lawId: "presented.counting-material-admits-only-naturals", claim: OracleClaims.CountingMaterialAdmitsOnlyNaturals)),
        Case("presented.letter-mask-refuses-a-split-block", () => Laws.Claim(lawId: "presented.letter-mask-refuses-a-split-block", claim: OracleClaims.LetterMaskRefusesASplitBlock)),
        Case("presented.generator-colours-are-bounded-indices", () => Laws.Claim(lawId: "presented.generator-colours-are-bounded-indices", claim: OracleClaims.GeneratorColoursAreBoundedIndices)),
        Case("core.factorization-full-width-sweep", () => Laws.Claim(lawId: "core.factorization-full-width-sweep", claim: PrimalityScaleClaims.FactorizationFullWidthSurface)),
        Case("core.big-integer-square-root-vs-unsigned", () => Laws.Claim(lawId: "core.big-integer-square-root-vs-unsigned", claim: CoreSurfaceClaims.BigIntegerSquareRootSurface)),
        Case("core.big-integer-modular-inverse-vs-hensel", () => Laws.Claim(lawId: "core.big-integer-modular-inverse-vs-hensel", claim: CoreSurfaceClaims.BigIntegerModularInverseSurface)),
        Case("core.big-integer-modular-square-root-vs-prime-field", () => Laws.Claim(lawId: "core.big-integer-modular-square-root-vs-prime-field", claim: CoreSurfaceClaims.BigIntegerModularSquareRootSurface)),
        Case("core.binary-integer-contracts", () => Laws.Claim(lawId: "core.binary-integer-contracts", claim: CoreSurfaceClaims.BinaryIntegerSurface)),
        Case("core.discrete-measure-exact-and-compiled", () => {
                Laws.Claim(lawId: "core.discrete-measure-exact-and-compiled", claim: CoreSurfaceClaims.DiscreteMeasureSurface);
                Laws.Claim(lawId: "core.discrete-measure-exact-and-compiled", claim: CoreSurfaceClaims.CompiledRadicalTransport);
            }),
        Case("core.number-theory-contracts", () => Laws.Claim(lawId: "core.number-theory-contracts", claim: CoreSurfaceClaims.NumberTheorySurface)),
        Case("core.quadratic-surd-field-and-conversion", () => Laws.Claim(lawId: "core.quadratic-surd-field-and-conversion", claim: CoreSurfaceClaims.QuadraticSurdSurface)),
        Case("core.prime-extensions-vs-trial-division", () => Laws.Claim(lawId: "core.prime-extensions-vs-trial-division", claim: CoreSurfaceClaims.PrimeExtensionsSurface)),
        Case("core.unsigned-integer-contracts", () => Laws.Claim(lawId: "core.unsigned-integer-contracts", claim: CoreSurfaceClaims.UnsignedIntegerSurface)),
        Case("core.fnv1a-published-vector", () => Laws.Claim(lawId: "core.fnv1a-published-vector", claim: CoreSurfaceClaims.Fnv1aSurface)),
        Case("core.monotonic-partitioner-fast-invariants", () => Laws.Claim(lawId: "core.monotonic-partitioner-fast-invariants", claim: CoreSurfaceClaims.MonotonicPartitionerSurface)),

        // ---- FixedQ4816 carrier: rounding vs oracle (ties to even), add, determinism ----
        Case("scalar.mul-vs-oracle", () => Laws.ScalarBinaryMatchesOracle(lawId: "scalar.mul-vs-oracle", domain: Scalar, tier: Tier.Default, subject: Subjects.FixedMultiply, oracle: Subjects.FixedMultiplyOracle)),
        Case("scalar.add-vs-oracle", () => Laws.ScalarBinaryMatchesOracle(lawId: "scalar.add-vs-oracle", domain: Scalar, tier: Tier.Default, subject: Subjects.FixedAdd, oracle: Subjects.FixedAddOracle)),
        Case("scalar.mul-purity", () => Laws.PureScalarBinary(lawId: "scalar.mul-purity", domain: Scalar, tier: Tier.Default, op: Subjects.FixedMultiply)),
        Case("scalar.grid-and-construction", () => Laws.Claim(lawId: "scalar.grid-and-construction", claim: Subjects.FixedGridAndConstruction)),
        // The OUTWARD double seam. It was waived as presentation-only for the whole of this suite's life; the waiver's
        // premise does not survive its own siblings, because unit-fraction16/32.double-projection-exact pin the same
        // conversion exactly and unsigned-scalar.double-seam pins the unsigned twin of THIS one — a Q48.16 narrowing with
        // genuine precision loss — against a hand ladder. Inexact is not unspecified: the map is a total function of the
        // raw and every value it takes is decidable in integers.
        Case("scalar.double-projection-vs-oracle", () => Laws.SweptClaim(lawId: "scalar.double-projection-vs-oracle", domain: Scalar, tier: Tier.Default, width: 1, claim: Subjects.FixedDoubleProjectionExact)),
        Case("scalar.additive-ops-vs-oracle", () => Laws.SweptClaim(lawId: "scalar.additive-ops-vs-oracle", domain: Scalar, tier: Tier.Default, width: 1, claim: Subjects.FixedAdditiveOpsExact)),
        Case("scalar.checked-ops-refuse", () => Laws.SweptClaim(lawId: "scalar.checked-ops-refuse", domain: Scalar, tier: Tier.Default, width: 1, claim: Subjects.FixedCheckedOpsRefuse)),
        Case("scalar.divide-vs-oracle", () => Laws.ScalarBinaryMatchesOracle(lawId: "scalar.divide-vs-oracle", domain: ScalarDivision, tier: Tier.Default, subject: Subjects.FixedDivide, oracle: Subjects.FixedDivideOracle)),
        Case("scalar.modulus-vs-oracle", () => Laws.SweptClaim(lawId: "scalar.modulus-vs-oracle", domain: Scalar, tier: Tier.Default, width: 1, claim: Subjects.FixedModulusExact)),
        Case("scalar.order-vs-oracle", () => Laws.SweptClaim(lawId: "scalar.order-vs-oracle", domain: Scalar, tier: Tier.Default, width: 2, claim: Subjects.FixedOrderExact)),
        Case("scalar.magnitude-selection-vs-oracle", () => Laws.SweptClaim(lawId: "scalar.magnitude-selection-vs-oracle", domain: Scalar, tier: Tier.Default, width: 1, claim: Subjects.FixedMagnitudeSelectionExact)),
        Case("scalar.integral-parts-vs-oracle", () => Laws.SweptClaim(lawId: "scalar.integral-parts-vs-oracle", domain: Scalar, tier: Tier.Default, width: 1, claim: Subjects.FixedIntegralPartsExact)),
        Case("scalar.predicates-classify", () => Laws.SweptClaim(lawId: "scalar.predicates-classify", domain: Scalar, tier: Tier.Default, width: 1, claim: Subjects.FixedPredicatesClassify)),
        Case("scalar.lerp-endpoints-and-oracle", () => Laws.SweptClaim(lawId: "scalar.lerp-endpoints-and-oracle", domain: Scalar, tier: Tier.Default, width: 2, claim: Subjects.FixedLerpEndpointsAndOracle)),
        Case("scalar.text-round-trip", () => Laws.SweptClaim(lawId: "scalar.text-round-trip", domain: ScalarText, tier: Tier.Default, width: 1, claim: Subjects.FixedTextRoundTrip)),
        Case("scalar.text-ladder-and-refusals", () => Laws.Claim(lawId: "scalar.text-ladder-and-refusals", claim: Subjects.FixedTextLadderAndRefusals)),
        Case("scalar.styled-exponent-compensation", () => Laws.Claim(lawId: "scalar.styled-exponent-compensation", claim: Subjects.StyledExponentCompensation)),
        Case("scalar.generic-conversion-modes", () => Laws.Claim(lawId: "scalar.generic-conversion-modes", claim: Subjects.GenericConversionModes)),
        Case("scalar.culture-token-ambiguity-refused", () => Laws.Claim(lawId: "scalar.culture-token-ambiguity-refused", claim: Subjects.CultureTokenAmbiguityRefused)),
        Case("scalar.sqrt-exact", () => Laws.SweptClaim(lawId: "scalar.sqrt-exact", domain: ScalarTranscendental, tier: Tier.Default, width: 1, claim: Subjects.FixedSqrtExact)),
        Case("scalar.log2-vs-series", () => Laws.SweptClaim(lawId: "scalar.log2-vs-series", domain: ScalarTranscendental, tier: Tier.Default, width: 1, claim: Subjects.FixedLog2WithinEnvelope)),
        Case("scalar.exp2-vs-series", () => Laws.SweptClaim(lawId: "scalar.exp2-vs-series", domain: ScalarTranscendental, tier: Tier.Default, width: 1, claim: Subjects.FixedExp2WithinEnvelope)),
        Case("scalar.sincos-vs-series", () => Laws.SweptClaim(lawId: "scalar.sincos-vs-series", domain: ScalarTranscendental, tier: Tier.Default, width: 1, claim: Subjects.FixedSinCosWithinEnvelope)),
        Case("scalar.atan2-vs-series", () => Laws.SweptClaim(lawId: "scalar.atan2-vs-series", domain: ScalarTranscendental, tier: Tier.Default, width: 1, claim: Subjects.FixedAtan2WithinEnvelope)),
        Case("scalar.pow-exact-lattice", () => Laws.Claim(lawId: "scalar.pow-exact-lattice", claim: Subjects.FixedPowExactLattice)),
        Case("scalar.pow-envelope", () => Laws.SweptClaim(lawId: "scalar.pow-envelope", domain: ScalarTranscendental, tier: Tier.Default, width: 1, claim: Subjects.FixedPowWithinEnvelope)),

        // ---- FixedQ1648 (Q16.48): a range-for-resolution scalar leaning toward resolution. Non-transcendental
        // sibling of the scalar family above, retargeted at forty-eight fraction bits and a sixteen-bit integer
        // range; its distinguishing law is the FixedQ4816 peer conversion. ----
        Case("q1648.mul-vs-oracle", () => Laws.ScalarBinaryMatchesOracle(lawId: "q1648.mul-vs-oracle", domain: Q1648Scalar, tier: Tier.Default, subject: Subjects.Q1648Multiply, oracle: Subjects.Q1648MultiplyOracle)),
        Case("q1648.add-vs-oracle", () => Laws.ScalarBinaryMatchesOracle(lawId: "q1648.add-vs-oracle", domain: Q1648Scalar, tier: Tier.Default, subject: Subjects.Q1648Add, oracle: Subjects.Q1648AddOracle)),
        Case("q1648.mul-purity", () => Laws.PureScalarBinary(lawId: "q1648.mul-purity", domain: Q1648Scalar, tier: Tier.Default, op: Subjects.Q1648Multiply)),
        Case("q1648.grid-and-construction", () => Laws.Claim(lawId: "q1648.grid-and-construction", claim: Subjects.Q1648GridAndConstruction)),
        Case("q1648.additive-ops-vs-oracle", () => Laws.SweptClaim(lawId: "q1648.additive-ops-vs-oracle", domain: Q1648Scalar, tier: Tier.Default, width: 1, claim: Subjects.Q1648AdditiveOpsExact)),
        Case("q1648.checked-ops-refuse", () => Laws.SweptClaim(lawId: "q1648.checked-ops-refuse", domain: Q1648Scalar, tier: Tier.Default, width: 1, claim: Subjects.Q1648CheckedOpsRefuse)),
        Case("q1648.divide-vs-oracle", () => Laws.ScalarBinaryMatchesOracle(lawId: "q1648.divide-vs-oracle", domain: Q1648ScalarDivision, tier: Tier.Default, subject: Subjects.Q1648Divide, oracle: Subjects.Q1648DivideOracle)),
        Case("q1648.modulus-vs-oracle", () => Laws.SweptClaim(lawId: "q1648.modulus-vs-oracle", domain: Q1648Scalar, tier: Tier.Default, width: 1, claim: Subjects.Q1648ModulusExact)),
        Case("q1648.order-vs-oracle", () => Laws.SweptClaim(lawId: "q1648.order-vs-oracle", domain: Q1648Scalar, tier: Tier.Default, width: 2, claim: Subjects.Q1648OrderExact)),
        Case("q1648.magnitude-selection-vs-oracle", () => Laws.SweptClaim(lawId: "q1648.magnitude-selection-vs-oracle", domain: Q1648Scalar, tier: Tier.Default, width: 1, claim: Subjects.Q1648MagnitudeSelectionExact)),
        Case("q1648.integral-parts-vs-oracle", () => Laws.SweptClaim(lawId: "q1648.integral-parts-vs-oracle", domain: Q1648Scalar, tier: Tier.Default, width: 1, claim: Subjects.Q1648IntegralPartsExact)),
        Case("q1648.predicates-classify", () => Laws.SweptClaim(lawId: "q1648.predicates-classify", domain: Q1648Scalar, tier: Tier.Default, width: 1, claim: Subjects.Q1648PredicatesClassify)),
        Case("q1648.lerp-endpoints-and-oracle", () => Laws.SweptClaim(lawId: "q1648.lerp-endpoints-and-oracle", domain: Q1648Scalar, tier: Tier.Default, width: 2, claim: Subjects.Q1648LerpEndpointsAndOracle)),
        Case("q1648.text-round-trip", () => Laws.SweptClaim(lawId: "q1648.text-round-trip", domain: Q1648Scalar, tier: Tier.Default, width: 1, claim: Subjects.Q1648TextRoundTrip)),
        Case("q1648.text-refusals", () => Laws.Claim(lawId: "q1648.text-refusals", claim: Subjects.Q1648TextRefusals)),
        Case("q1648.styled-parse-is-genuine", () => Laws.Claim(lawId: "q1648.styled-parse-is-genuine", claim: Subjects.Q1648StyledParseIsGenuine)),
        Case("q1648.text-parse-ties", () => Laws.Claim(lawId: "q1648.text-parse-ties", claim: Subjects.Q1648TextParseTies)),
        Case("q1648.decimal-conversion-modes", () => Laws.Claim(lawId: "q1648.decimal-conversion-modes", claim: Subjects.Q1648DecimalConversionModes)),
        Case("q1648.peer-conversion-vs-fixedq4816", () => Laws.Claim(lawId: "q1648.peer-conversion-vs-fixedq4816", claim: Subjects.Q1648PeerConversionExact)),

        // ---- FixedQ3232 (Q32.32): a scalar splitting integer and fraction bits evenly, the balanced point between
        // FixedQ4816's range-leaning and FixedQ1648's resolution-leaning splits. Non-transcendental sibling of the
        // scalar family above, retargeted at thirty-two fraction bits and a thirty-two-bit integer range; its
        // distinguishing law is the FixedQ4816 peer conversion. ----
        Case("q3232.mul-vs-oracle", () => Laws.ScalarBinaryMatchesOracle(lawId: "q3232.mul-vs-oracle", domain: Q3232Scalar, tier: Tier.Default, subject: Subjects.Q3232Multiply, oracle: Subjects.Q3232MultiplyOracle)),
        Case("q3232.add-vs-oracle", () => Laws.ScalarBinaryMatchesOracle(lawId: "q3232.add-vs-oracle", domain: Q3232Scalar, tier: Tier.Default, subject: Subjects.Q3232Add, oracle: Subjects.Q3232AddOracle)),
        Case("q3232.mul-purity", () => Laws.PureScalarBinary(lawId: "q3232.mul-purity", domain: Q3232Scalar, tier: Tier.Default, op: Subjects.Q3232Multiply)),
        Case("q3232.grid-and-construction", () => Laws.Claim(lawId: "q3232.grid-and-construction", claim: Subjects.Q3232GridAndConstruction)),
        Case("q3232.additive-ops-vs-oracle", () => Laws.SweptClaim(lawId: "q3232.additive-ops-vs-oracle", domain: Q3232Scalar, tier: Tier.Default, width: 1, claim: Subjects.Q3232AdditiveOpsExact)),
        Case("q3232.checked-ops-refuse", () => Laws.SweptClaim(lawId: "q3232.checked-ops-refuse", domain: Q3232Scalar, tier: Tier.Default, width: 1, claim: Subjects.Q3232CheckedOpsRefuse)),
        Case("q3232.divide-vs-oracle", () => Laws.ScalarBinaryMatchesOracle(lawId: "q3232.divide-vs-oracle", domain: Q3232ScalarDivision, tier: Tier.Default, subject: Subjects.Q3232Divide, oracle: Subjects.Q3232DivideOracle)),
        Case("q3232.modulus-vs-oracle", () => Laws.SweptClaim(lawId: "q3232.modulus-vs-oracle", domain: Q3232Scalar, tier: Tier.Default, width: 1, claim: Subjects.Q3232ModulusExact)),
        Case("q3232.order-vs-oracle", () => Laws.SweptClaim(lawId: "q3232.order-vs-oracle", domain: Q3232Scalar, tier: Tier.Default, width: 2, claim: Subjects.Q3232OrderExact)),
        Case("q3232.magnitude-selection-vs-oracle", () => Laws.SweptClaim(lawId: "q3232.magnitude-selection-vs-oracle", domain: Q3232Scalar, tier: Tier.Default, width: 1, claim: Subjects.Q3232MagnitudeSelectionExact)),
        Case("q3232.integral-parts-vs-oracle", () => Laws.SweptClaim(lawId: "q3232.integral-parts-vs-oracle", domain: Q3232Scalar, tier: Tier.Default, width: 1, claim: Subjects.Q3232IntegralPartsExact)),
        Case("q3232.predicates-classify", () => Laws.SweptClaim(lawId: "q3232.predicates-classify", domain: Q3232Scalar, tier: Tier.Default, width: 1, claim: Subjects.Q3232PredicatesClassify)),
        Case("q3232.lerp-endpoints-and-oracle", () => Laws.SweptClaim(lawId: "q3232.lerp-endpoints-and-oracle", domain: Q3232Scalar, tier: Tier.Default, width: 2, claim: Subjects.Q3232LerpEndpointsAndOracle)),
        Case("q3232.text-round-trip", () => Laws.SweptClaim(lawId: "q3232.text-round-trip", domain: Q3232Scalar, tier: Tier.Default, width: 1, claim: Subjects.Q3232TextRoundTrip)),
        Case("q3232.text-refusals", () => Laws.Claim(lawId: "q3232.text-refusals", claim: Subjects.Q3232TextRefusals)),
        Case("q3232.styled-parse-is-genuine", () => Laws.Claim(lawId: "q3232.styled-parse-is-genuine", claim: Subjects.Q3232StyledParseIsGenuine)),
        Case("q3232.text-parse-ties", () => Laws.Claim(lawId: "q3232.text-parse-ties", claim: Subjects.Q3232TextParseTies)),
        Case("q3232.decimal-conversion-modes", () => Laws.Claim(lawId: "q3232.decimal-conversion-modes", claim: Subjects.Q3232DecimalConversionModes)),
        Case("q3232.peer-conversion-vs-fixedq4816", () => Laws.Claim(lawId: "q3232.peer-conversion-vs-fixedq4816", claim: Subjects.Q3232PeerConversionExact)),

        // ---- FixedContributionFold: raw-once accumulation, optional pool, final range and terminal quantization ----
        Case("contribution-fold.formula-vs-big-integer-oracle", () => {
                Laws.Claim(lawId: "contribution-fold.formula-vs-big-integer-oracle", claim: FixedContributionFoldClaims.FormulaExactGrid);
                Laws.SweptClaim(lawId: "contribution-fold.formula-vs-big-integer-oracle", domain: ContributionFoldFormula, tier: Tier.Default, width: 4, claim: FixedContributionFoldClaims.FormulaSample);
            }),
        Case("contribution-fold.no-pool-specialization", () => {
                Laws.Claim(lawId: "contribution-fold.no-pool-specialization", claim: FixedContributionFoldClaims.NoPoolExactGrid);
                Laws.SweptClaim(lawId: "contribution-fold.no-pool-specialization", domain: ContributionFoldNoPool, tier: Tier.Default, width: 2, claim: FixedContributionFoldClaims.NoPoolSample);
            }),
        Case("contribution-fold.raw-sum-order-independent", () => {
                Laws.Claim(lawId: "contribution-fold.raw-sum-order-independent", claim: FixedContributionFoldClaims.RawSumEveryPermutation);
                Laws.SweptClaim(lawId: "contribution-fold.raw-sum-order-independent", domain: ContributionFoldOrder, tier: Tier.Default, width: 8, claim: FixedContributionFoldClaims.RawSumSampledLonger);
            }),
        Case("contribution-fold.analog-pool-bound", () => Laws.SweptClaim(lawId: "contribution-fold.analog-pool-bound", domain: ContributionFoldAnalog, tier: Tier.Default, width: 2, claim: FixedContributionFoldClaims.AnalogPoolBound)),
        Case("contribution-fold.binary-flip-bound-sharp", () => Laws.Claim(lawId: "contribution-fold.binary-flip-bound-sharp", claim: FixedContributionFoldClaims.BinaryFlipBoundAndSharpness)),
        Case("contribution-fold.binary-composition-induction", () => Laws.Claim(lawId: "contribution-fold.binary-composition-induction", claim: FixedContributionFoldClaims.BinaryCompositionByInduction)),
        Case("contribution-fold.terminal-quantization-idempotent", () => Laws.SweptClaim(lawId: "contribution-fold.terminal-quantization-idempotent", domain: ContributionFoldQuantization, tier: Tier.Default, width: 2, claim: FixedContributionFoldClaims.TerminalQuantizationIdempotence)),
        Case("contribution-fold.overflow-boundary-exact", () => Laws.Claim(lawId: "contribution-fold.overflow-boundary-exact", claim: FixedContributionFoldClaims.OverflowBoundaryExact)),
        Case("contribution-fold.configuration-refusals", () => Laws.Claim(lawId: "contribution-fold.configuration-refusals", claim: FixedContributionFoldClaims.ConfigurationRefusals)),
        Case("contribution-fold.discriminating-examples", () => Laws.Claim(lawId: "contribution-fold.discriminating-examples", claim: FixedContributionFoldClaims.DiscriminatingExamples)),
        Case("contribution-fold.site-composition-distribution-known-false", () => Laws.KnownFalse(lawId: "contribution-fold.site-composition-distribution-known-false", counterexample: FixedContributionFoldClaims.SiteCompositionDoesNotDistribute)),

        // ---- UFixedQ4816 carrier: the unsigned Q48.16 companion, wrapping into [0, 2⁶⁴) with MinValue at zero ----
        Case("unsigned-scalar.mul-vs-oracle", () => Laws.ScalarBinaryMatchesOracle(lawId: "unsigned-scalar.mul-vs-oracle", domain: UnsignedScalar, tier: Tier.Default, subject: Subjects.UnsignedFixedMultiply, oracle: Subjects.UnsignedFixedMultiplyOracle)),
        Case("unsigned-scalar.div-vs-oracle", () => Laws.ScalarBinaryMatchesOracle(lawId: "unsigned-scalar.div-vs-oracle", domain: UnsignedScalar, tier: Tier.Default, subject: Subjects.UnsignedFixedDivide, oracle: Subjects.UnsignedFixedDivideOracle)),
        Case("unsigned-scalar.mul-purity", () => Laws.PureScalarBinary(lawId: "unsigned-scalar.mul-purity", domain: UnsignedScalar, tier: Tier.Default, op: Subjects.UnsignedFixedMultiply)),
        Case("unsigned-scalar.unchecked-kernels-vs-oracle", () => Laws.SweptClaim(lawId: "unsigned-scalar.unchecked-kernels-vs-oracle", domain: UnsignedScalar, tier: Tier.Default, width: 1, claim: Subjects.UnsignedUncheckedKernelsExact)),
        Case("unsigned-scalar.wrapping-algebra-exact", () => Laws.SweptClaim(lawId: "unsigned-scalar.wrapping-algebra-exact", domain: UnsignedScalar, tier: Tier.Default, width: 1, claim: Subjects.UnsignedWrappingAlgebraExact)),
        Case("unsigned-scalar.checked-operators-refuse", () => Laws.SweptClaim(lawId: "unsigned-scalar.checked-operators-refuse", domain: UnsignedScalar, tier: Tier.Default, width: 1, claim: Subjects.UnsignedCheckedOperatorsRefuse)),
        Case("unsigned-scalar.saturating-and-selection-exact", () => Laws.SweptClaim(lawId: "unsigned-scalar.saturating-and-selection-exact", domain: UnsignedScalar, tier: Tier.Default, width: 2, claim: Subjects.UnsignedSaturatingAndSelectionExact)),
        Case("unsigned-scalar.integer-decomposition-exact", () => Laws.SweptClaim(lawId: "unsigned-scalar.integer-decomposition-exact", domain: UnsignedScalar, tier: Tier.Default, width: 1, claim: Subjects.UnsignedIntegerDecompositionExact)),
        Case("unsigned-scalar.order-and-comparison-exact", () => Laws.SweptClaim(lawId: "unsigned-scalar.order-and-comparison-exact", domain: UnsignedScalar, tier: Tier.Default, width: 1, claim: Subjects.UnsignedOrderAndComparisonExact)),
        Case("unsigned-scalar.number-predicates-exact", () => Laws.SweptClaim(lawId: "unsigned-scalar.number-predicates-exact", domain: UnsignedScalar, tier: Tier.Default, width: 1, claim: Subjects.UnsignedNumberPredicatesExact)),
        Case("unsigned-scalar.construction-and-refusals", () => Laws.Claim(lawId: "unsigned-scalar.construction-and-refusals", claim: Subjects.UnsignedConstructionAndRefusals)),
        Case("unsigned-scalar.double-seam", () => Laws.Claim(lawId: "unsigned-scalar.double-seam", claim: Subjects.UnsignedDoubleSeam)),
        Case("unsigned-scalar.text-round-trip", () => Laws.SweptClaim(lawId: "unsigned-scalar.text-round-trip", domain: UnsignedScalar, tier: Tier.Default, width: 1, claim: Subjects.UnsignedTextRoundTrip)),
        Case("unsigned-scalar.text-ladder-and-refusals", () => Laws.Claim(lawId: "unsigned-scalar.text-ladder-and-refusals", claim: Subjects.UnsignedTextLadderAndRefusals)),

        // ---- UnitInterval32 carrier: the closed unit interval on the sampler's own grid ----
        Case("closed-unit.mul-vs-oracle", () => Laws.ScalarBinaryMatchesOracle(lawId: "closed-unit.mul-vs-oracle", domain: ClosedUnit, tier: Tier.Default, subject: Subjects.ClosedUnitMultiply, oracle: Subjects.ClosedUnitMultiplyOracle)),
        // The point of spending the thirty-third bit: both absorbing elements act EXACTLY, at every raw region and at
        // both endpoints, so nothing about the interval's closure is a rounding accident.
        Case("closed-unit.unit-and-zero-exact", () => Laws.SweptClaim(lawId: "closed-unit.unit-and-zero-exact", domain: ClosedUnit, tier: Tier.Default, width: 1, claim: Subjects.ClosedUnitUnitAndZeroExact)),
        Case("closed-unit.bounded-ops-exact", () => Laws.SweptClaim(lawId: "closed-unit.bounded-ops-exact", domain: ClosedUnit, tier: Tier.Default, width: 1, claim: Subjects.ClosedUnitBoundedOpsExact)),
        // The kinship contract: the sampler's half-open grid embeds with no representation event, and the Q48.16 seam
        // states its one rounding out loud.
        Case("closed-unit.kinship-exact", () => Laws.SweptClaim(lawId: "closed-unit.kinship-exact", domain: ClosedUnit, tier: Tier.Default, width: 1, claim: Subjects.ClosedUnitKinshipExact)),
        Case("closed-unit.construction-and-refusals", () => Laws.Claim(lawId: "closed-unit.construction-and-refusals", claim: Subjects.ClosedUnitConstructionAndRefusals)),
        // The three-factor product exists because a fused sum's term is a charge times two coefficients and the contract
        // is ONE rounding per returned coefficient, not one per pair. Its statement is the same one the pairwise product
        // makes, at the tripled scale.
        Case("closed-unit.triple-product-one-rounding", () => Laws.SweptClaim(lawId: "closed-unit.triple-product-one-rounding", domain: ClosedUnit, tier: Tier.Default, width: 2, claim: Subjects.ClosedUnitTripleProductExact)),

        // ---- UnitFraction16/UnitFraction32 carriers: the half-open unit fractions ----
        Case("unit-fraction16.mul-vs-oracle", () => Laws.ScalarBinaryMatchesOracle(lawId: "unit-fraction16.mul-vs-oracle", domain: UnitFraction16Domain, tier: Tier.Default, subject: Subjects.UnitFraction16Multiply, oracle: Subjects.UnitFraction16MultiplyOracle)),
        Case("unit-fraction16.div-vs-oracle", () => Laws.ScalarBinaryMatchesOracle(lawId: "unit-fraction16.div-vs-oracle", domain: UnitFraction16Domain, tier: Tier.Default, subject: Subjects.UnitFraction16Divide, oracle: Subjects.UnitFraction16DivideOracle)),
        Case("unit-fraction16.exact-ops-and-order", () => Laws.SweptClaim(lawId: "unit-fraction16.exact-ops-and-order", domain: UnitFraction16Domain, tier: Tier.Default, width: 1, claim: Subjects.UnitFraction16ExactOpsAndOrder)),
        Case("unit-fraction16.shifts-vs-oracle", () => Laws.SweptClaim(lawId: "unit-fraction16.shifts-vs-oracle", domain: UnitFraction16Domain, tier: Tier.Default, width: 1, claim: Subjects.UnitFraction16ShiftsMatchOracle)),
        Case("unit-fraction16.construction-and-refusals", () => Laws.Claim(lawId: "unit-fraction16.construction-and-refusals", claim: Subjects.UnitFraction16ConstructionAndRefusals)),
        Case("unit-fraction16.text-vs-oracle", () => Laws.SweptClaim(lawId: "unit-fraction16.text-vs-oracle", domain: UnitFraction16Domain, tier: Tier.Default, width: 1, claim: Subjects.UnitFraction16TextMatchesOracle)),
        Case("unit-fraction16.parse-ladder", () => Laws.Claim(lawId: "unit-fraction16.parse-ladder", claim: Subjects.UnitFraction16ParseLadderHolds)),
        Case("unit-fraction16.double-projection-exact", () => Laws.SweptClaim(lawId: "unit-fraction16.double-projection-exact", domain: UnitFraction16Domain, tier: Tier.Default, width: 1, claim: Subjects.UnitFraction16DoubleProjectionExact)),
        Case("unit-fraction32.mul-vs-oracle", () => Laws.ScalarBinaryMatchesOracle(lawId: "unit-fraction32.mul-vs-oracle", domain: UnitFraction32Domain, tier: Tier.Default, subject: Subjects.UnitFraction32Multiply, oracle: Subjects.UnitFraction32MultiplyOracle)),
        Case("unit-fraction32.div-vs-oracle", () => Laws.ScalarBinaryMatchesOracle(lawId: "unit-fraction32.div-vs-oracle", domain: UnitFraction32Domain, tier: Tier.Default, subject: Subjects.UnitFraction32Divide, oracle: Subjects.UnitFraction32DivideOracle)),
        Case("unit-fraction32.exact-ops-and-order", () => Laws.SweptClaim(lawId: "unit-fraction32.exact-ops-and-order", domain: UnitFraction32Domain, tier: Tier.Default, width: 1, claim: Subjects.UnitFraction32ExactOpsAndOrder)),
        Case("unit-fraction32.shifts-vs-oracle", () => Laws.SweptClaim(lawId: "unit-fraction32.shifts-vs-oracle", domain: UnitFraction32Domain, tier: Tier.Default, width: 1, claim: Subjects.UnitFraction32ShiftsMatchOracle)),
        Case("unit-fraction32.construction-and-refusals", () => Laws.Claim(lawId: "unit-fraction32.construction-and-refusals", claim: Subjects.UnitFraction32ConstructionAndRefusals)),
        Case("unit-fraction32.text-vs-oracle", () => Laws.SweptClaim(lawId: "unit-fraction32.text-vs-oracle", domain: UnitFraction32Domain, tier: Tier.Default, width: 1, claim: Subjects.UnitFraction32TextMatchesOracle)),
        Case("unit-fraction32.parse-ladder", () => Laws.Claim(lawId: "unit-fraction32.parse-ladder", claim: Subjects.UnitFraction32ParseLadderHolds)),
        Case("unit-fraction32.double-projection-exact", () => Laws.SweptClaim(lawId: "unit-fraction32.double-projection-exact", domain: UnitFraction32Domain, tier: Tier.Default, width: 1, claim: Subjects.UnitFraction32DoubleProjectionExact)),
        // The seam the closed interval's remarks describe in prose, stated from the FRACTION side. closed-unit.kinship-exact
        // already pins the embedding and its refusal at one from the interval side; this case does not restate them.
        Case("unit-fraction32.kinship-exact", () => Laws.SweptClaim(lawId: "unit-fraction32.kinship-exact", domain: UnitFraction32Domain, tier: Tier.Default, width: 1, claim: Subjects.UnitFraction32KinshipExact)),

        // ---- FixedComplex ((0, −1)) ----
        Case("complex.mul-vs-oracle", () => Laws.BinaryMatchesOracle(lawId: "complex.mul-vs-oracle", domain: Complex, tier: Tier.Default, subject: Subjects.ComplexMultiply, oracle: Subjects.MultiplyOracle(pRaw: 0L, qRaw: ComplexQ))),
        Case("complex.twin-quad", () => Laws.TwinBinary(lawId: "complex.twin-quad", domain: Complex, tier: Tier.Default, first: Subjects.ComplexMultiply, second: Subjects.AlgebraMultiply(pRaw: 0L, qRaw: ComplexQ), witness: Subjects.MultiplyOracle(pRaw: 0L, qRaw: ComplexQ))),
        Case("complex.mul-purity", () => Laws.PureBinary(lawId: "complex.mul-purity", domain: Complex, tier: Tier.Default, op: Subjects.ComplexMultiply)),
        Case("complex.conjugate-involution", () => Laws.RoundTrip(lawId: "complex.conjugate-involution", domain: Complex, tier: Tier.Default, forward: Subjects.ComplexConjugate, inverse: Subjects.ComplexConjugate)),
        Case("complex.negate-involution", () => Laws.RoundTrip(lawId: "complex.negate-involution", domain: Complex, tier: Tier.Default, forward: Subjects.ComplexNegate, inverse: Subjects.ComplexNegate)),
        Case("complex.multiplicative-identity", () => Laws.IdentityElement(lawId: "complex.multiplicative-identity", domain: Complex, tier: Tier.Default, op: Subjects.ComplexMultiply, identityU: OneRaw, identityV: 0L)),
        // Conjugation distributes over multiplication where no wrap occurs; the bounded sublattice is its exact home
        // (at MinValue the two's-complement negation is asymmetric, so the identity is not a full-range law).
        Case("complex.conjugate-distributes", () => Laws.ConjugateSymmetry(lawId: "complex.conjugate-distributes", domain: Sublattice, tier: Tier.Default, mul: Subjects.ComplexMultiply, conj: Subjects.ComplexConjugate)),
        Case("algebra.complex-lane-vs-oracle", () => Laws.BinaryMatchesOracle(lawId: "algebra.complex-lane-vs-oracle", domain: Complex, tier: Tier.Default, subject: Subjects.AlgebraMultiply(pRaw: 0L, qRaw: ComplexQ), oracle: Subjects.MultiplyOracle(pRaw: 0L, qRaw: ComplexQ))),

        // ---- FixedSplit ((0, +1)) ----
        Case("split.mul-vs-oracle", () => Laws.BinaryMatchesOracle(lawId: "split.mul-vs-oracle", domain: Split, tier: Tier.Default, subject: Subjects.SplitMultiply, oracle: Subjects.MultiplyOracle(pRaw: 0L, qRaw: SplitQ))),
        Case("split.twin-quad", () => Laws.TwinBinary(lawId: "split.twin-quad", domain: Split, tier: Tier.Default, first: Subjects.SplitMultiply, second: Subjects.AlgebraMultiply(pRaw: 0L, qRaw: SplitQ), witness: Subjects.MultiplyOracle(pRaw: 0L, qRaw: SplitQ))),
        Case("split.norm-vs-oracle", () => Laws.ScalarMatchesOracle(lawId: "split.norm-vs-oracle", domain: Split, tier: Tier.Default, subject: Subjects.SplitNorm, oracle: Subjects.NormOracle(pRaw: 0L, qRaw: SplitQ))),
        Case("split.norm-twin-quad", () => Laws.ScalarTwin(lawId: "split.norm-twin-quad", domain: Split, tier: Tier.Default, first: Subjects.SplitNorm, second: Subjects.AlgebraNorm(pRaw: 0L, qRaw: SplitQ), witness: Subjects.NormOracle(pRaw: 0L, qRaw: SplitQ))),
        Case("split.mul-purity", () => Laws.PureBinary(lawId: "split.mul-purity", domain: Split, tier: Tier.Default, op: Subjects.SplitMultiply)),
        Case("split.conjugate-involution", () => Laws.RoundTrip(lawId: "split.conjugate-involution", domain: Split, tier: Tier.Default, forward: Subjects.SplitConjugate, inverse: Subjects.SplitConjugate)),
        Case("split.norm-multiplicative", () => Laws.NormMultiplicativity(lawId: "split.norm-multiplicative", domain: Sublattice, tier: Tier.Default, mul: Subjects.SplitMultiply, norm: Subjects.SplitNorm, combineNorms: Subjects.FixedMultiply)),

        // ---- FixedDual<FixedQ4816> ((0, 0)) ----
        Case("dual.mul-vs-oracle", () => Laws.BinaryMatchesOracle(lawId: "dual.mul-vs-oracle", domain: Dual, tier: Tier.Default, subject: Subjects.DualMultiply, oracle: Subjects.MultiplyOracle(pRaw: 0L, qRaw: 0L))),
        Case("dual.twin-quad", () => Laws.TwinBinary(lawId: "dual.twin-quad", domain: Dual, tier: Tier.Default, first: Subjects.DualMultiply, second: Subjects.AlgebraMultiply(pRaw: 0L, qRaw: 0L), witness: Subjects.MultiplyOracle(pRaw: 0L, qRaw: 0L))),
        Case("dual.mul-purity", () => Laws.PureBinary(lawId: "dual.mul-purity", domain: Dual, tier: Tier.Default, op: Subjects.DualMultiply)),

        // ---- FixedComplex: the rest of the planar rotation type ----
        Case("complex.additive-group-exact", () => Laws.SweptClaim(lawId: "complex.additive-group-exact", domain: Complex, tier: Tier.Default, width: 2, claim: Subjects.ComplexAdditiveGroupExact)),
        Case("complex.presentation-seam", () => Laws.SweptClaim(lawId: "complex.presentation-seam", domain: Complex, tier: Tier.Default, width: 2, claim: Subjects.ComplexPresentationSeam)),
        Case("complex.div-vs-oracle", () => Laws.BinaryMatchesOracle(lawId: "complex.div-vs-oracle", domain: ComplexDivide, tier: Tier.Default, subject: Subjects.ComplexDivide, oracle: Subjects.ComplexDivideOracle)),
        Case("complex.div-refusal-and-unit", () => Laws.Claim(lawId: "complex.div-refusal-and-unit", claim: Subjects.ComplexDivRefusalAndUnit)),
        Case("complex.magnitude-vs-oracle", () => Laws.SweptClaim(lawId: "complex.magnitude-vs-oracle", domain: ComplexDirection, tier: Tier.Default, width: 2, claim: Subjects.ComplexMagnitudeExact)),
        Case("complex.normalize-unit-direction", () => Laws.SweptClaim(lawId: "complex.normalize-unit-direction", domain: ComplexDirection, tier: Tier.Default, width: 2, claim: Subjects.ComplexNormalizeUnitDirection)),
        Case("complex.from-to-direction", () => Laws.SweptClaim(lawId: "complex.from-to-direction", domain: ComplexDirection, tier: Tier.Default, width: 2, claim: Subjects.ComplexFromToDirection)),
        Case("complex.angle-seam", () => Laws.Claim(lawId: "complex.angle-seam", claim: Subjects.ComplexAngleSeam)),
        Case("complex.rotate-vs-oracle", () => Laws.BinaryMatchesOracle(lawId: "complex.rotate-vs-oracle", domain: ComplexRotate, tier: Tier.Default, subject: Subjects.ComplexRotate, oracle: Subjects.MultiplyOracle(pRaw: 0L, qRaw: ComplexQ))),

        // ---- FixedSplit: the rest of the hyperbolic sibling ----
        Case("split.additive-group-exact", () => Laws.SweptClaim(lawId: "split.additive-group-exact", domain: Split, tier: Tier.Default, width: 2, claim: Subjects.SplitAdditiveGroupExact)),
        Case("split.div-vs-oracle", () => Laws.BinaryMatchesOracle(lawId: "split.div-vs-oracle", domain: SplitDivide, tier: Tier.Default, subject: Subjects.SplitDivide, oracle: Subjects.SplitDivideOracle)),
        Case("split.unit-and-division", () => Laws.SweptClaim(lawId: "split.unit-and-division", domain: Split, tier: Tier.Default, width: 2, claim: Subjects.SplitUnitAndDivision)),
        Case("split.transform-vs-oracle", () => Laws.BinaryMatchesOracle(lawId: "split.transform-vs-oracle", domain: SplitTransform, tier: Tier.Default, subject: Subjects.SplitTransform, oracle: Subjects.MultiplyOracle(pRaw: 0L, qRaw: SplitQ))),
        Case("split.rapidity-ladder", () => Laws.Claim(lawId: "split.rapidity-ladder", claim: Subjects.SplitRapidityLadderClaim)),

        // ---- FixedDual: the rest of the dual construction, INCLUDING the two kernels the covered member id hides ----
        Case("dual.additive-group-exact", () => Laws.SweptClaim(lawId: "dual.additive-group-exact", domain: Dual, tier: Tier.Default, width: 2, claim: Subjects.DualAdditiveGroupExact)),
        Case("dual.seeds-and-identities", () => Laws.SweptClaim(lawId: "dual.seeds-and-identities", domain: Dual, tier: Tier.Default, width: 2, claim: Subjects.DualSeedsAndIdentities)),
        Case("dual.divide-vs-oracle", () => Laws.BinaryMatchesOracle(lawId: "dual.divide-vs-oracle", domain: DualDivide, tier: Tier.Default, subject: Subjects.DualDivide, oracle: Subjects.DualDivideOracle)),
        Case("dual.divide-refusals", () => Laws.Claim(lawId: "dual.divide-refusals", claim: Subjects.DualDivideRefusals)),
        Case("dual.transcendental-lifts", () => Laws.SweptClaim(lawId: "dual.transcendental-lifts", domain: Dual, tier: Tier.Default, width: 2, claim: Subjects.DualTranscendentalLifts)),
        Case("dual.quaternion-mul-vs-oracle", () => Laws.VectorMatchesOracle(lawId: "dual.quaternion-mul-vs-oracle", domain: DualQuaternion, tier: Tier.Default, width: 8, subject: Subjects.DualQuaternionMultiplyLanes, oracle: Subjects.DualQuaternionMultiplyOracle)),
        Case("dual.generic-carrier-two-roundings", () => Laws.VectorMatchesOracle(lawId: "dual.generic-carrier-two-roundings", domain: DualGeneric, tier: Tier.Default, width: 4, subject: Subjects.DualSplitMultiplyLanes, oracle: Subjects.DualSplitMultiplyOracle)),

        // ---- FixedQuaternion ----
        Case("quaternion.mul-vs-oracle", () => Laws.VectorMatchesOracle(lawId: "quaternion.mul-vs-oracle", domain: Quaternion, tier: Tier.Default, width: 4, subject: Subjects.QuaternionMultiplyLanes, oracle: Subjects.QuaternionMultiplyOracle)),
        Case("quaternion.dot-vs-oracle", () => Laws.SweptClaim(lawId: "quaternion.dot-vs-oracle", domain: Quaternion, tier: Tier.Default, width: 4, claim: Subjects.QuaternionDotExact)),
        Case("quaternion.scale-vs-oracle", () => Laws.SweptClaim(lawId: "quaternion.scale-vs-oracle", domain: Quaternion, tier: Tier.Default, width: 4, claim: Subjects.QuaternionScaleExact)),
        Case("quaternion.additive-group-exact", () => Laws.SweptClaim(lawId: "quaternion.additive-group-exact", domain: Quaternion, tier: Tier.Default, width: 4, claim: Subjects.QuaternionAdditiveGroupExact)),
        Case("quaternion.conjugate-antiautomorphism", () => Laws.SweptClaim(lawId: "quaternion.conjugate-antiautomorphism", domain: QuaternionSublattice, tier: Tier.Default, width: 4, claim: Subjects.QuaternionConjugateAntiautomorphism)),
        Case("quaternion.norm-vs-oracle", () => Laws.SweptClaim(lawId: "quaternion.norm-vs-oracle", domain: QuaternionDirection, tier: Tier.Default, width: 4, claim: Subjects.QuaternionNormExact)),
        Case("quaternion.inverse-vs-oracle", () => Laws.SweptClaim(lawId: "quaternion.inverse-vs-oracle", domain: QuaternionDirection, tier: Tier.Default, width: 4, claim: Subjects.QuaternionInverseExact)),
        Case("quaternion.normalize-unit-direction", () => Laws.SweptClaim(lawId: "quaternion.normalize-unit-direction", domain: QuaternionDirection, tier: Tier.Default, width: 4, claim: Subjects.QuaternionNormalizeUnitDirection)),
        Case("quaternion.rotate-vs-oracle", () => Laws.SweptClaim(lawId: "quaternion.rotate-vs-oracle", domain: QuaternionRotate, tier: Tier.Default, width: 4, claim: Subjects.QuaternionRotateExact)),
        Case("quaternion.axis-angle-ladder", () => Laws.Claim(lawId: "quaternion.axis-angle-ladder", claim: Subjects.QuaternionAxisAngleLadderClaim)),
        // The inbound seam. Judged against the SAME ladder as vector.adoption-ladder, deliberately: the two doors
        // must agree, and sharing the table is what would catch them drifting apart. Its second leg states what a
        // three-lane ladder cannot — that the seam does not renormalize.
        Case("quaternion.adoption-ladder", () => Laws.Claim(lawId: "quaternion.adoption-ladder", claim: Subjects.QuaternionAdoptionMatchesLadder)),
        Case("quaternion.exp-log-seam", () => Laws.Claim(lawId: "quaternion.exp-log-seam", claim: Subjects.QuaternionExpLogSeam)),

        // SinCosRaw was gated by nothing until these two cases: the case above says so in its own leg text. It is
        // internal, so the coverage manifest cannot name it and the hole was invisible to the ratchet. The reference
        // is Oracles.EncloseSinCos carried past the signed carrier by the
        // angle-addition identity, with the envelope derived from |c - 2^64/2pi| <= 1/2 rather than fitted to what the
        // subject happens to do. Proved by masking the top angle bit — the exact defect the member exists to avoid —
        // which reddens these two and NOTHING else in the tier.
        Case("quaternion.sincos-raw-full-unsigned-width", () => Laws.Claim(lawId: "quaternion.sincos-raw-full-unsigned-width", claim: TransformKernelClaims.SinCosRawFullUnsignedWidthSurface)),
        Case("quaternion.sincos-raw-width-sweep", () => Laws.Claim(lawId: "quaternion.sincos-raw-width-sweep", claim: TransformKernelClaims.SinCosRawWidthSweepSurface)),
        Case("quaternion.from-to-shortest-arc", () => Laws.SweptClaim(lawId: "quaternion.from-to-shortest-arc", domain: QuaternionDirection, tier: Tier.Default, width: 4, claim: Subjects.QuaternionFromToShortestArc)),
        Case("quaternion.slerp-endpoints-and-arc", () => Laws.Claim(lawId: "quaternion.slerp-endpoints-and-arc", claim: Subjects.QuaternionSlerpEndpointsAndArc)),

        // The renderer's seam, and the one member of this type an algebra law cannot reach: the argument ORDER into
        // System.Numerics.Quaternion. Swapping X and W in ToQuaternion leaves every other case in this suite green,
        // which is exactly why the waiver that stood here — 'the algebra laws pin the exact raw contract instead' — was
        // false about the only thing this member decides on its own.
        Case("quaternion.presentation-ladder", () => Laws.Claim(lawId: "quaternion.presentation-ladder", claim: Subjects.QuaternionPresentationMatchesLadder)),

        // ---- one fractional relation (0, ½): the fused fractional lane vs the oracle ----
        Case("algebra.fractional-mul-vs-oracle", () => Laws.BinaryMatchesOracle(lawId: "algebra.fractional-mul-vs-oracle", domain: Fractional, tier: Tier.Default, subject: Subjects.AlgebraMultiply(pRaw: 0L, qRaw: HalfQ), oracle: Subjects.MultiplyOracle(pRaw: 0L, qRaw: HalfQ))),
        Case("algebra.fractional-norm-vs-oracle", () => Laws.ScalarMatchesOracle(lawId: "algebra.fractional-norm-vs-oracle", domain: Fractional, tier: Tier.Default, subject: Subjects.AlgebraNorm(pRaw: 0L, qRaw: HalfQ), oracle: Subjects.NormOracle(pRaw: 0L, qRaw: HalfQ))),
        Case("algebra.fractional-mobius-vs-oracle", () => Laws.MobiusMatchesOracle(lawId: "algebra.fractional-mobius-vs-oracle", domain: Fractional, tier: Tier.Default, subject: Subjects.AlgebraMobius(pRaw: 0L, qRaw: HalfQ), oracleNumerator: Subjects.MobiusNumeratorOracle(pRaw: 0L, qRaw: HalfQ))),

        // ---- Möbius exactness for integer relations ----
        Case("mobius.integer-0,-1", () => Laws.MobiusMatchesOracle(lawId: "mobius.integer-0,-1", domain: Mobius, tier: Tier.Default, subject: Subjects.AlgebraMobius(pRaw: 0L, qRaw: ComplexQ), oracleNumerator: Subjects.MobiusNumeratorOracle(pRaw: 0L, qRaw: ComplexQ))),
        Case("mobius.integer-1,1", () => Laws.MobiusMatchesOracle(lawId: "mobius.integer-1,1", domain: Mobius, tier: Tier.Default, subject: Subjects.AlgebraMobius(pRaw: OneRaw, qRaw: OneRaw), oracleNumerator: Subjects.MobiusNumeratorOracle(pRaw: OneRaw, qRaw: OneRaw))),
        Case("mobius.integer-2,1", () => Laws.MobiusMatchesOracle(lawId: "mobius.integer-2,1", domain: Mobius, tier: Tier.Default, subject: Subjects.AlgebraMobius(pRaw: (2L * OneRaw), qRaw: OneRaw), oracleNumerator: Subjects.MobiusNumeratorOracle(pRaw: (2L * OneRaw), qRaw: OneRaw))),

        // ---- Integer floored division: the generics the private Int128/BigInteger copies collapsed into ----
        // The carrier's raw longs are the operand source, so the domain's edge bias lands on the signs and the extremes
        // where floored and truncated division disagree. The oracle divides in arbitrary width, where the carrier's one
        // unrepresentable quotient is an ordinary value.
        Case("integer.floor-divide-vs-oracle", () => {
                Laws.ScalarBinaryMatchesOracle(lawId: "integer.floor-divide-vs-oracle", domain: Scalar, tier: Tier.Default, subject: Subjects.FloorDivide, oracle: Subjects.FloorDivideOracle);
                Laws.Claim(lawId: "integer.floor-divide-vs-oracle", claim: Subjects.IntegerDivisionLimitsRefuse);
            }),
        Case("integer.ceiling-divide-vs-oracle", () => Laws.ScalarBinaryMatchesOracle(lawId: "integer.ceiling-divide-vs-oracle", domain: Scalar, tier: Tier.Default, subject: Subjects.CeilingDivide, oracle: Subjects.CeilingDivideOracle)),
        // The pair is pinned component-wise: its quotient against the same oracle the standalone quotient answers to,
        // and its remainder against the exact floored remainder — so a pair that agreed only in aggregate would fail.
        Case("integer.floor-divrem-quotient", () => Laws.ScalarBinaryMatchesOracle(lawId: "integer.floor-divrem-quotient", domain: Scalar, tier: Tier.Default, subject: Subjects.FloorDivRemQuotient, oracle: Subjects.FloorDivideOracle)),
        Case("integer.floor-divrem-remainder", () => Laws.ScalarBinaryMatchesOracle(lawId: "integer.floor-divrem-remainder", domain: Scalar, tier: Tier.Default, subject: Subjects.FloorDivRemRemainder, oracle: Subjects.FloorDivRemRemainderOracle)),

        // ---- the presented charged algebra: one kernel, many presentations ----
        //
        // Every case below drives PresentedAlgebra.Multiply and differs from every other ONLY by the presentation value
        // and the material type argument. The twins are against the hand-written kernels the derived form reproduces;
        // the oracle laws are against BigInteger reference arithmetic that shares nothing with either.

        Case("presented.interpreted-equals-compiled", () => {
                Laws.Claim(lawId: "presented.interpreted-equals-compiled", claim: Subjects.InterpretedEqualsCompiled);
                Laws.Claim(lawId: "presented.interpreted-equals-compiled", claim: OracleOwnershipClaims.PresentationOwnsAdmittedMemory);
            }),

        Case("presented.clifford-twin-geometric-3-0-0", () => Laws.VectorTwin(lawId: "presented.clifford-twin-geometric-3-0-0", domain: Presented, tier: Tier.Default, width: 8, first: Subjects.PresentedCliffordMultiply(positiveCount: 3, negativeCount: 0, degenerateCount: 0), second: Subjects.GeometricMultiply(positiveCount: 3, negativeCount: 0, degenerateCount: 0), witness: Subjects.CliffordProductOracle(positiveCount: 3, negativeCount: 0, degenerateCount: 0))),
        Case("presented.clifford-twin-geometric-3-0-1", () => Laws.VectorTwin(lawId: "presented.clifford-twin-geometric-3-0-1", domain: Presented, tier: Tier.Default, width: 16, first: Subjects.PresentedCliffordMultiply(positiveCount: 3, negativeCount: 0, degenerateCount: 1), second: Subjects.GeometricMultiply(positiveCount: 3, negativeCount: 0, degenerateCount: 1), witness: Subjects.CliffordProductOracle(positiveCount: 3, negativeCount: 0, degenerateCount: 1))),

        Case("presented.certification-scopes-associativity-not-confluence", () => Laws.Claim(lawId: "presented.certification-scopes-associativity-not-confluence", claim: Subjects.CertificationScopesAssociativityNotConfluence)),

        // The conformal (4,1,0) world has five generators and thirty-two blades, which the four-generator
        // GeometricAlgebra cannot reach at all: there is no twin here, so the charges answer to the bubble-sort oracle
        // and the algebra answers to its own associativity certificate.
        Case("presented.clifford-charge-vs-oracle", () => {
                Laws.Claim(lawId: "presented.clifford-charge-vs-oracle", claim: Subjects.CliffordChargesMatchOracle);
                Laws.VectorMatchesOracle(lawId: "presented.clifford-charge-vs-oracle", domain: Presented, tier: Tier.Default, width: 32, subject: Subjects.PresentedCliffordMultiply(positiveCount: 4, negativeCount: 1, degenerateCount: 0), oracle: Subjects.CliffordProductOracle(positiveCount: 4, negativeCount: 1, degenerateCount: 0));
            }),

        Case("presented.octonion-twin-doubling", () => Laws.VectorTwin(lawId: "presented.octonion-twin-doubling", domain: Presented, tier: Tier.Default, width: 8, first: Subjects.PresentedCayleyDicksonMultiply(floors: 3), second: Subjects.DoublingOctonionMultiply, witness: Subjects.CayleyDicksonProductOracle(floors: 3))),

        // The quasialgebra floor, on the exact sublattice where the associator measures coherence rather than rounding.
        Case("presented.associator-twin-doubling", () => {
                Laws.Claim(lawId: "presented.associator-twin-doubling", claim: Subjects.CayleyDicksonChargesAndCertificates);
                Laws.VectorTernaryTwin(lawId: "presented.associator-twin-doubling", domain: Sublattice, tier: Tier.Default, width: 8, first: Subjects.PresentedCayleyDicksonAssociator(floors: 3), second: Subjects.DoublingOctonionAssociator, witness: null);
            }),

        // ---- phase 3: coherence live ----
        //
        // The associator stops being only a readout and becomes a rule charge the normalizer APPLIES. The oracle is the
        // bracketing's own nested products, which re-associate nothing, so agreement is route-independence measured
        // rather than asserted; the certificate's quadruple identity is the same statement about the charges alone.
        Case("presented.reassociation-route-coherent", () => Laws.Claim(lawId: "presented.reassociation-route-coherent", claim: Subjects.ReassociationRouteCoherent)),

        // The other half of the same change: a uniform charge of one leaves a term's brackets inert, which is what every
        // phase-1 and phase-2 gate pins and what a splice charge leaking into the uniform regime would break.
        Case("presented.reassociation-brackets-inert", () => Laws.Claim(lawId: "presented.reassociation-brackets-inert", claim: Subjects.ReassociationBracketsInert)),

        // The canary and the twin the coherence slice owed. The canary pins an ABSOLUTE floor on how far a live charge
        // moves the flattener's answer, so a declaration that quietly stopped arriving fails without the case leaning on
        // the certificate's own nonassociative-triple count; the twin decides coherence a second way, by normalizing every
        // quadruple's five bracketings, so a mis-oriented pentagon inside Certify has something to disagree with. The
        // separating instance is a coherent 3-cocycle over a product that ASSOCIATES: coherence holds, faithfulness does
        // not, and the two are measured apart rather than described apart.
        Case("presented.coherence-route-independence", () => Laws.Claim(lawId: "presented.coherence-route-independence", claim: Subjects.CoherenceIsRouteIndependence)),

        // ---- phase 3: the group regime ----
        //
        // The boundary map's group row, made an instance rather than a note: a reflection world enters as measured
        // lattice data, its order is pinned twice by constructions that share no step, and everything the row promises —
        // inverses under a unit witness per generator, orbit enumeration — is a bounded attempt with an honest refusal.
        Case("presented.group-orders-exact", () => Laws.Claim(lawId: "presented.group-orders-exact", claim: Subjects.GroupOrdersExact)),

        // The world no enumeration reaches is gated by its ACTION instead: every relation the presentation declares
        // moves no node at all, and the word that reads the mirrors once is the lattice's own cycle, of the period the
        // rotation surface and the ray factorisation already carry.
        Case("presented.reflection-action-lattice", () => Laws.Claim(lawId: "presented.reflection-action-lattice", claim: Subjects.ReflectionActionMatchesLattice)),

        // The twin the group slice owed: the presented PRODUCT is the lattice action. One compiled cell, one composite
        // permutation and one pair of reflections applied in sequence must name the same element, and the pinned power
        // ladder says the same thing about repeated multiplication. The oracle is SymmetryLattice.Reflect composed by
        // hand, which runs no step the algebra runs.
        Case("presented.reflection-product-twins-action", () => Laws.Claim(lawId: "presented.reflection-product-twins-action", claim: Subjects.ReflectionProductTwinsAction)),

        // The refusals, and the pair that is the whole point of the row: inverses SURVIVE where enumeration refuses.
        Case("presented.group-limits-refuse", () => {
                Laws.Claim(lawId: "presented.group-limits-refuse", claim: Subjects.GroupLimitsRefuse);
                Laws.Claim(lawId: "presented.group-limits-refuse", claim: OracleClaims.PresentedGroupRequiresAssociativity);
            }),

        // The interval-poset instance: the incidence algebra is the quiver's shape at a sub-quiver, so mu is the
        // guarded star of the negated strict zeta and the Euler characteristic of a complex is the Möbius value of the
        // one interval spanning its bounded face order — answered to by an alternating cell count and by three
        // hand-computed numbers.
        Case("presented.incidence-euler-mass", () => Laws.Claim(lawId: "presented.incidence-euler-mass", claim: Subjects.IncidenceEulerMass)),

        // The Dirichlet window IS this order's reduced incidence algebra, so the two mus agree interval for interval
        // through the interval type — and the two bases do NOT, which is what keeps the window a quotient rather than
        // a specialization.
        Case("presented.incidence-mobius-vs-window", () => Laws.Claim(lawId: "presented.incidence-mobius-vs-window", claim: Subjects.IncidenceMobiusMatchesWindow)),

        // Stokes' identity is the adjunction, and the adjunction is one product read two ways: the boundary is the
        // incidence element multiplied on the left of a chain and the coboundary is the same element on the right of a
        // cochain, so the two bracketings of a pairing must agree. Exact over the integers, bit-identical over the
        // house scalar inside the carrier's headroom, and separated outside it.
        Case("presented.stokes-adjunction", () => {
                Laws.Claim(lawId: "presented.stokes-adjunction", claim: Subjects.StokesAdjunction);
                Laws.SweptClaim(lawId: "presented.stokes-adjunction", domain: Presented, tier: Tier.Default, width: 7, claim: Subjects.StokesAdjunctionFixed());
            }),

        // The same adjunction at three more materials. It is the associativity of one product, so it cannot depend on
        // the carrier — which is exactly why one material proves less than it looks. The teeth are the non-degeneracy
        // count: the ordered basis pairs Stokes does not annihilate are precisely the declared incidences, so a
        // collapsed boundary fails here instead of passing on an identity between two zeros.
        Case("presented.stokes-material-sweep", () => Laws.Claim(lawId: "presented.stokes-material-sweep", claim: Subjects.StokesMaterialSweep)),

        // The refusals: data that names no order and no complex is turned away at construction, and Möbius inversion
        // over a material with no signs is refused rather than approximated.
        Case("presented.incidence-limits-refuse", () => Laws.Claim(lawId: "presented.incidence-limits-refuse", claim: Subjects.IncidenceLimitsRefuse)),

        // ---- phase 4: boundary composability, and co-arity greater than one ----
        //
        // Composability stopped being written out per entry and became one comparison over the generators' own
        // boundaries. The quiver and the interval poset exercise its colour half at arity one; the planar tangle
        // exercises its width half at a co-arity that is genuinely not one, and its cups and caps are the first
        // generators in the library whose two boundaries differ in length.

        // The derivation reproduces both endpoint tests cell for cell, predicted from the argument data rather than
        // from the entry, with the boundary comparison restated here and required to agree with the annihilations.
        Case("presented.boundary-composition-unmoved", () => Laws.Claim(lawId: "presented.boundary-composition-unmoved", claim: Subjects.BoundaryCompositionUnmoved)),

        // The basis: block by block against the tabulated Catalan numbers AND against the ballot difference, which
        // reaches the same value without a Catalan recursion, so a mis-transcribed table fails beside a mis-enumeration.
        Case("presented.tangle-basis-counts", () => Laws.Claim(lawId: "presented.tangle-basis-counts", claim: Subjects.TangleBasisCounts)),

        // The three algebraic relations, asserted on the DERIVED product at three materials. Nothing in the catalogue
        // entry mentions them, so a mis-traced arc or a mis-counted loop breaks one of the three.
        Case("presented.tangle-relations-hold", () => Laws.Claim(lawId: "presented.tangle-relations-hold", claim: Subjects.TangleRelationsHold)),

        // The width cap is derived from the 512 normal forms a finite basis holds, so the width past the last admitted
        // one is refused rather than admitted and then found unusable. That the last admitted width is REACHED, at its
        // 377 diagrams, is asserted where a width-six presentation is already built: deep.presented-tangle-sweep. This
        // case costs one throw per refusal and builds nothing.
        Case("presented.tangle-limits-refuse", () => Laws.Claim(lawId: "presented.tangle-limits-refuse", claim: Subjects.TangleLimitsRefuse)),

        // The canary: composing two diagrams must actually strand off closed loops, and those loops must actually be
        // charged. Every other statement in this slice holds just as well at a loop charge silently equal to one.
        Case("presented.tangle-loop-charge-canary", () => Laws.Claim(lawId: "presented.tangle-loop-charge-canary", claim: Subjects.TangleLoopChargeCanary)),

        // ---- phase 4: the braiding certificate ----
        //
        // The braiding is DERIVED from the compiled cells rather than declared beside them, which is strictly more than
        // the associator's flag reports: the commutation charge of an ordered pair is searched over the material's one,
        // its negation and — at a field material — the coefficient the two cells' own charges name, and it is issued
        // only after the two orderings were found to differ by it. So the reported braiding is the product's own.

        // The charges against two constructions that read no cell: the doubling recursion and the bubble-sort sign
        // oracle, and — at every floor the tower ships — the shipped nested tower multiplying both orderings out.
        Case("presented.braiding-derived-vs-doubling", () => Laws.Claim(lawId: "presented.braiding-derived-vs-doubling", claim: Subjects.BraidingDerivedVsDoubling)),

        // Coherence of the braiding is a mathematical fact about the data, so it is witnessed rather than thrown: the
        // octonion floor fails the hexagons and carries the charges that disagree, and the degenerate Clifford
        // signature reports no braiding for the opposite reason — its annihilating pairs constrain no charge, so the
        // derivation never finishes and no identity is stated to fail. Both routes to a false flag are covered, and
        // the two are kept apart. The quantum torus is the instance that separates the two flags, since every
        // catalogue braiding is a sign and a sign is its own mirror.
        Case("presented.braiding-hexagon-witnessed", () => Laws.Claim(lawId: "presented.braiding-hexagon-witnessed", claim: Subjects.BraidingHexagonWitnessed)),

        // The limit, and the pair that is the whole point of the row: the SAME presentation shape issues no charge at a
        // material that cannot name one half and issues it at a field material that can. A missing flag is not a
        // failure, and it is not the budget either.
        Case("presented.braiding-limits-issue-no-flag", () => Laws.Claim(lawId: "presented.braiding-limits-issue-no-flag", claim: Subjects.BraidingLimitsIssueNoFlag)),

        // The canary: the derived charges must actually be nontrivial on more pairs than the measured floor, and each
        // of those pairs must re-multiply. A braiding that collapsed to the trivial one satisfies both hexagons, the
        // symmetric flag and every refusal case, so only a floor catches it.
        Case("presented.braiding-nontrivial-canary", () => Laws.Claim(lawId: "presented.braiding-nontrivial-canary", claim: Subjects.BraidingNontrivialCanary)),

        // ---- phase 4: presentation morphisms and substitution systems ----
        //
        // A morphism is admitted by evaluating the source's own relations on the images, so the law's job is to prove
        // that the admission means what it says: the map really carries products to products and sums to sums, on
        // elements that are not basis elements and that the admission never examined.
        Case("presented.functor-preserves-relations", () => {
                Laws.Claim(lawId: "presented.functor-preserves-relations", claim: Subjects.FunctorPreservesRelations);
                Laws.Claim(lawId: "presented.functor-preserves-relations", claim: OracleOwnershipClaims.FunctorRequiresOneMaterial);
            }),

        Case("presented.element-ownership-is-uniform", () => Laws.Claim(lawId: "presented.element-ownership-is-uniform", claim: OracleOwnershipClaims.ForeignElementsAreRejectedUniformly)),

        // The refusal, re-derived from the obstruction's own data: the named rule is folded through the images by hand
        // and must really fail, and the named basis pair — the annihilation a degree window states and no rule
        // carries — must really be one the images do not preserve.
        Case("presented.functor-refuses-witness", () => Laws.Claim(lawId: "presented.functor-refuses-witness", claim: Subjects.FunctorRefusesWitness)),

        // A substitution system IS a morphism of free monoids, and its word must never be an element: the composed
        // letter images at √13 and √19 run 52 and 411 symbols, past what a mixed-radix key holds, so only MapWord
        // reaches them. The shipped quasicrystal streamer shares the period and the substitution recipe with the
        // subject, so the leg that stands outside both is the mechanical word of the same slope.
        Case("presented.substitution-twins-quasicrystal", () => Laws.Claim(lawId: "presented.substitution-twins-quasicrystal", claim: Subjects.SubstitutionTwinsQuasicrystal)),

        // The abelianization against the inflation lens, with the orientation pinned: counting occurrences gives the
        // TRANSPOSE of the substitution matrix, which four of the six periods separate from the direct reading.
        Case("presented.substitution-matrix-vs-inflation", () => Laws.Claim(lawId: "presented.substitution-matrix-vs-inflation", claim: Subjects.SubstitutionMatrixVsInflation)),

        // ---- phase 4: the graph zeta ----
        //
        // det(I − tA) and its reciprocal, read out of the algebra's own trace and powers. Nothing new multiplies here:
        // the power sums are Trace of Power, the coefficients come out of a bounded loop over them, and the zeta is the
        // shipped guarded star of the negated augmentation part inside the jet presentation.

        // The recursion against an enumeration that shares no step with it: the oracle forms no power, takes no trace
        // and divides nowhere, while the subject does all three. The order-two case is a third route again, through a
        // continued-fraction period folded as convergent matrices.
        Case("presented.zeta-charpoly-vs-minors", () => Laws.Claim(lawId: "presented.zeta-charpoly-vs-minors", claim: Subjects.ZetaCharacteristicVsMinors)),

        // The power sums ARE closed-walk counts, which is what makes the polynomial a graph invariant rather than a
        // matrix identity. Length zero is part of the statement: it is the order's worth of ones the recursion runs at.
        Case("presented.zeta-traces-vs-walk-counts", () => Laws.Claim(lawId: "presented.zeta-traces-vs-walk-counts", claim: Subjects.ZetaTracesVsWalkCounts)),

        // The reciprocal, under a nilpotence certificate the star ISSUES rather than assumes, checked in both orders and
        // at degree bounds above, at and below the order — an inverse modulo t^(d+1) depends on nothing above that
        // degree, so truncating the polynomial does not truncate the statement.
        Case("presented.zeta-reciprocal-round-trip", () => Laws.Claim(lawId: "presented.zeta-reciprocal-round-trip", claim: Subjects.ZetaReciprocalRoundTrip)),

        // The licence, measured on both sides: the recursion divides by every index up to the order, so a material that
        // certifies no inverses stops at index one and a field of characteristic p stops at p — and the same modulus
        // answers at the order below p. Over the house scalar nothing is offered at all, which is what exact-only means.
        Case("presented.zeta-limits-refuse", () => Laws.Claim(lawId: "presented.zeta-limits-refuse", claim: Subjects.ZetaLimitsRefuse)),

        // ---- phase 4: the second product ----
        //
        // The shuffle and the quasi-shuffle are ONE catalogue entry: the generators are the words of a bounded length,
        // the cells are the interleavings with their multiplicities, and an empty letter product is the degenerate case
        // where no two heads collide. Nothing in the kernel changes — a second product is a second presentation.

        // Every cell against a brute enumeration that generates every step-kind sequence and TESTS it, where the entry
        // reads three shorter cells; and the certificate, which COMPUTES commutativity and associativity, following the
        // letter product to false wherever the letter product is not itself both.
        Case("presented.shuffle-vs-enumeration", () => Laws.Claim(lawId: "presented.shuffle-vs-enumeration", claim: Subjects.ShuffleMatchesEnumeration)),

        // The binomial coefficients, read twice out of the same entry — as the multiplicity one letter's shuffle
        // carries, and as the number of words two different letters interleave into — against a Pascal's triangle built
        // by addition alone, which reaches them without a factorial, a product or a division.
        Case("presented.shuffle-vs-binomial", () => Laws.Claim(lawId: "presented.shuffle-vs-binomial", claim: Subjects.ShuffleMatchesBinomials)),

        // The degenerate case, pinned from both sides: the default argument IS the empty letter product, no collision
        // term leaks into it, and a collision adds exactly the shortened terms while leaving the shuffle's own cell
        // untouched at the top length.
        Case("presented.quasishuffle-degenerates-to-shuffle", () => Laws.Claim(lawId: "presented.quasishuffle-degenerates-to-shuffle", claim: Subjects.QuasiShuffleDegeneratesToShuffle)),

        // A word over one letter names an iterated sum, and multiplying two iterated sums merges their index sets — the
        // interleavings where no index coincides, the collisions where they do. So the identity holds for the
        // quasi-shuffle and FAILS for the shuffle, which is what makes the collision term load-bearing. The sequences
        // come from the antidifference of a different presentation entirely, and are pinned against Pascal first.
        Case("presented.quasishuffle-vs-prefix-sums", () => Laws.Claim(lawId: "presented.quasishuffle-vs-prefix-sums", claim: Subjects.QuasiShuffleMatchesPrefixSums)),

        // The caps, which are the 512 normal forms a finite basis holds read at each argument, and the one refusal that
        // is a mathematical statement rather than a budget: a collision naming a letter the alphabet does not carry
        // names no element of this algebra, and the refusal says which ordered pair blocked. The refusals are throws
        // and cost nothing; the tuples BUILT here stay at a window of four or below, and the near-cap ones are left to
        // presented.shuffle-near-cap-basis, since each of those emits one rule per ordered pair of its 511 or 512 words
        // under the compiled basis this case reads.
        Case("presented.shuffle-limits-refuse", () => Laws.Claim(lawId: "presented.shuffle-limits-refuse", claim: Subjects.ShuffleLimitsRefuse)),

        // The canary: the interleaving must actually split a product into several words and actually carry the
        // multiplicity each is reached with. A second product that quietly degenerated to concatenation satisfies every
        // flag, the degeneracy claim and every refusal above, and only a floor catches it.
        Case("presented.shuffle-multiterm-canary", () => Laws.Claim(lawId: "presented.shuffle-multiterm-canary", claim: Subjects.ShuffleMultiTermCanary)),

        // ---- phase 4: knot state sums ----
        //
        // The last clause of the mandate, and it adds NO library member: a knot invariant here is a morphism out of the
        // free monoid on the crossing letters into the planar tangle algebra, a product with the cup and cap layers, and
        // a pairing at the empty diagram — every one of them shipped before this slice. The construction is the whole
        // phase's claim at its sharpest, so its gates are correspondingly hard.

        // The braid relations hold on the images although the free source imposed none of them, and the loop charge is
        // what makes them hold: at any other charge the crossing and its mirror stop composing to the identity.
        Case("presented.braid-relation-holds", () => Laws.Claim(lawId: "presented.braid-relation-holds", claim: Subjects.BraidRelationHolds)),

        // The published bracket of the unknot, of both trefoil chiralities and of the figure-eight, carried as integer
        // Laurent coefficients and folded by Horner, answering over the rationals and over three prime fields — which is
        // what multi-point evaluation buys instead of a coefficient ring holding a formal variable.
        Case("presented.state-sum-vs-tabulated", () => Laws.Claim(lawId: "presented.state-sum-vs-tabulated", claim: Subjects.StateSumMatchesTabulated)),

        // The second oracle, and the reason there are two: the enumeration builds each state's whole closed diagram as
        // one graph and counts its components, knowing nothing about knots, so it catches a mis-transcribed table where
        // the table catches a wrong construction. It runs out to eight crossings, where two-to-the-crossings still fits.
        Case("presented.state-sum-vs-smoothing-enumeration", () => Laws.Claim(lawId: "presented.state-sum-vs-smoothing-enumeration", claim: Subjects.StateSumMatchesSmoothingEnumeration)),

        // The moves, all three, with the first one stated honestly: the second and third leave the value fixed and the
        // first multiplies it by minus the crossing charge cubed, so the readout is an invariant of the DIAGRAM.
        Case("presented.state-sum-move-invariant", () => Laws.Claim(lawId: "presented.state-sum-move-invariant", claim: Subjects.StateSumMoveInvariant)),

        // What is refused and what is merely not claimed, kept apart: an odd plat and a plat past the width cap are
        // refused; the braid group's finite basis does not exist and every basis-dependent readout says so; its word
        // problem is a BUDGET, reported as one; and equal values are not equal knots, witnessed by a curl.
        Case("presented.knot-limits-refuse", () => Laws.Claim(lawId: "presented.knot-limits-refuse", claim: Subjects.KnotLimitsRefuse)),

        // The strongest canary in the phase. An invariant collapsed to a constant satisfies every twin, every relation,
        // every refusal and every move claim above, because all of those hold just as well of a constant — only a floor
        // on how many declared pairs the values separate catches it, and only the two trefoils prove it sees chirality.
        Case("presented.state-sum-separates-canary", () => Laws.Claim(lawId: "presented.state-sum-separates-canary", claim: Subjects.StateSumSeparatesCanary)),

        // ---- phase 3: the declared second kernel (O1) ----
        //
        // Elementary-divisor reduction is not a convolution and cannot be a presentation, so it is carried openly as a
        // second kernel and made to prove itself: the triple IS the certificate. Every hand-checkable form and every
        // swept draw is re-multiplied here and answered to by the classical gcd-of-minors invariants, which run no step
        // the reduction runs.
        Case("presented.smith-certificate-remultiplies", () => {
                Laws.Claim(lawId: "presented.smith-certificate-remultiplies", claim: Subjects.SmithKnownForms);
                Laws.SweptClaim(lawId: "presented.smith-certificate-remultiplies", domain: Presented, tier: Tier.Default, width: 9, claim: Subjects.SmithCertificateRemultiplies());
            }),

        // The bound, kept honest rather than asserted: a ten-by-ten matrix of single-digit entries drives intermediate
        // coefficients into the kilobits, the ceiling refuses that reduction where it is set low and answers the same
        // matrix where it is set high, and the smallest-pivot rule is MEASURED against a first-nonzero foil.
        Case("presented.smith-growth-refuses", () => Laws.Claim(lawId: "presented.smith-growth-refuses", claim: Subjects.SmithGrowthBounded)),

        // The re-multiplication the second kernel owed at scale: the swept case is three-by-three and square, so square
        // orders through eight, both rectangular orientations and one wide draw are proved here, each re-multiplied and
        // inverted both ways in this file. The one matrix whose answer is a classical fact rather than a recomputation
        // is the reflection lattice's own Cartan matrix, built from the group slice's MEASURED bond diagram: its
        // determinant is one, so its elementary divisors are eight ones and nothing else.
        Case("presented.smith-remultiplies-at-scale", () => Laws.Claim(lawId: "presented.smith-remultiplies-at-scale", claim: Subjects.SmithRemultipliesAtScale)),

        // The two consumers the obstruction promised. The elementary divisors ARE the integral torsion coefficients, so
        // the smallest complex carrying torsion is the oracle; and Betti numbers over a field material are the echelon
        // path already in the tree, so they needed no new code at all. The two disagree only where the torsion meets
        // the characteristic, which is measured with a mod-two sweep rather than described.
        // A dimension is a LABEL, and the graded tables are sized by the largest one rather than by the cell count, so
        // a one-cell complex labelled a billion asked for roughly 12 GB and int.MaxValue overflowed the top. Both
        // halves are stated: an oversized label is refused, and the widest grading the 84-cell cap allows is admitted
        // whole, so the bound is reachable rather than a wall.
        Case("presented.cell-dimension-bounded-by-cells", () => Laws.Claim(lawId: "presented.cell-dimension-bounded-by-cells", claim: Subjects.CellDimensionBoundHolds)),

        Case("presented.homology-torsion-and-betti", () => {
                Laws.Claim(lawId: "presented.homology-torsion-and-betti", claim: Subjects.HomologyTorsionAndBetti);
                Laws.Claim(lawId: "presented.homology-torsion-and-betti", claim: OracleClaims.NonChainHomologyRefuses);
            }),

        Case("presented.gf2-twins-binaryfield", () => {
                Laws.VectorTwin(lawId: "presented.gf2-twins-binaryfield", domain: Presented, tier: Tier.Default, width: 8, first: Subjects.PresentedBinaryFieldMultiply(degree: 8, reductionTail: 0x1BUL), second: Subjects.BinaryFieldMultiply8, witness: null);
                Laws.VectorTwin(lawId: "presented.gf2-twins-binaryfield", domain: Presented, tier: Tier.Default, width: 16, first: Subjects.PresentedBinaryFieldMultiply(degree: 16, reductionTail: 0x2BUL), second: Subjects.BinaryFieldMultiply16, witness: Subjects.BinaryFieldProductOracle(degree: 16, reductionTail: 0x2BUL));
                Laws.VectorMatchesOracle(lawId: "presented.gf2-twins-binaryfield", domain: Presented, tier: Tier.Default, width: 8, subject: Subjects.PresentedBinaryFieldMultiply(degree: 8, reductionTail: 0x1BUL), oracle: Subjects.BinaryFieldProductOracle(degree: 8, reductionTail: 0x1BUL));
            }),

        Case("presented.quadratic-twin-algebra-integer-lane", () => Laws.VectorTwin(lawId: "presented.quadratic-twin-algebra-integer-lane", domain: Presented, tier: Tier.Default, width: 2, first: Subjects.PresentedQuadraticMultiply(pRaw: OneRaw, qRaw: OneRaw), second: Subjects.QuadraticMultiplyLanes(pRaw: OneRaw, qRaw: OneRaw), witness: Subjects.QuadraticMultiplyLanesOracle(pRaw: OneRaw, qRaw: OneRaw))),
        Case("presented.quadratic-twin-algebra-fractional-lane", () => Laws.VectorTwin(lawId: "presented.quadratic-twin-algebra-fractional-lane", domain: Presented, tier: Tier.Default, width: 2, first: Subjects.PresentedQuadraticMultiply(pRaw: 0L, qRaw: HalfQ), second: Subjects.QuadraticMultiplyLanes(pRaw: 0L, qRaw: HalfQ), witness: Subjects.QuadraticMultiplyLanesOracle(pRaw: 0L, qRaw: HalfQ))),

        Case("presented.power-twins-companion", () => Laws.TwinPower(lawId: "presented.power-twins-companion", domain: Presented, tier: Tier.Default, first: Subjects.PresentedRootPower(), second: Subjects.CompanionRootPower, witness: Subjects.CompanionRootPowerOracle)),

        // ONE quiver presentation at three materials — reachable, shortest, and how many — with no second kernel.
        Case("presented.tropical-star-vs-oracle", () => Laws.VectorMatchesOracle(lawId: "presented.tropical-star-vs-oracle", domain: Presented, tier: Tier.Default, width: (Subjects.GraphOrder * Subjects.GraphOrder), subject: Subjects.PresentedTropicalStar(), oracle: Subjects.TropicalStarOracle)),
        Case("presented.counting-power-vs-oracle", () => Laws.VectorMatchesOracle(lawId: "presented.counting-power-vs-oracle", domain: Presented, tier: Tier.Default, width: (Subjects.GraphOrder * Subjects.GraphOrder), subject: Subjects.PresentedWalkCount(length: 5), oracle: Subjects.WalkCountOracle(length: 5))),

        // ---- the unit interval as a material family: the SAME quiver presentation at three more materials ----
        //
        // Three more questions about one graph — the most probable route, the widest bottleneck, and the route whose
        // steps' shortfalls from certainty still sum to under one — and not one line of new kernel between them. Each
        // oracle walks the graph a different way from the star: two enumerate simple paths and one runs a max-min triple
        // loop, so none of them forms a power at all.
        Case("presented.most-likely-path-vs-oracle", () => Laws.VectorMatchesOracle(lawId: "presented.most-likely-path-vs-oracle", domain: Presented, tier: Tier.Default, width: (Subjects.GraphOrder * Subjects.GraphOrder), subject: Subjects.PresentedMostLikelyPathStar(), oracle: Subjects.MostLikelyPathStarOracle)),
        Case("presented.fuzzy-closure-vs-max-min", () => Laws.VectorMatchesOracle(lawId: "presented.fuzzy-closure-vs-max-min", domain: Presented, tier: Tier.Default, width: (Subjects.GraphOrder * Subjects.GraphOrder), subject: Subjects.PresentedFuzzyStar(), oracle: Subjects.FuzzyStarOracle)),
        Case("presented.bounded-sum-route-vs-oracle", () => Laws.VectorMatchesOracle(lawId: "presented.bounded-sum-route-vs-oracle", domain: Presented, tier: Tier.Default, width: (Subjects.GraphOrder * Subjects.GraphOrder), subject: Subjects.PresentedBoundedSumStar(), oracle: Subjects.BoundedSumStarOracle)),

        // THE BOOLEAN SUBLATTICE TWIN. Confine the three materials to the two endpoints and all three collapse onto the
        // Boolean material exactly: the maximum is disjunction, and the rounded product, the minimum and the bounded sum
        // are all conjunction there. It is the statement that the family EXTENDS the Boolean answer rather than
        // approximating it, and it is what a rounding defect at an endpoint would break first.
        Case("presented.unit-interval-boolean-sublattice", () => Laws.VectorMatchesOracle(lawId: "presented.unit-interval-boolean-sublattice", domain: Presented, tier: Tier.Default, width: (Subjects.GraphOrder * Subjects.GraphOrder), subject: Subjects.PresentedUnitIntervalBooleanSublattice(), oracle: Subjects.BooleanStarOracle)),

        // THE POWER-OF-TWO TWIN — the log-domain isomorphism as a law on the subfamily where BOTH sides are exact. Arc
        // weights at exact powers of two make every most-likely-path product a shift and every negated logarithm an
        // integer, so the likelihood and the tropical distance must name the same cost at every pair, and therefore the
        // same decision. The envelope is stated at the subject: exponents zero through seven over at most three arcs, so
        // a total never reaches the 32 fraction bits at which the likelihood would underflow while the cost stayed
        // finite.
        Case("presented.unit-interval-power-of-two-twin", () => Laws.VectorTwin(lawId: "presented.unit-interval-power-of-two-twin", domain: Presented, tier: Tier.Default, width: (Subjects.GraphOrder * Subjects.GraphOrder), first: Subjects.PresentedMostLikelyPathPowerOfTwo(), second: Subjects.PresentedTropicalPowerOfTwo(), witness: null)),

        // The three semirings against arbitrary width rather than against the carrier they are built on. Every other
        // statement here quantifies over GRAPHS, where a material's pairwise product is reached only through the fused
        // fold and a quiet change to it can hide; this one names the product, the addition, both identities, the zero
        // test and distributivity at every swept raw pair. It also carries the suite's ONLY absolute statement of the
        // fused term's single rounding — three interior factors against the triple-product oracle — because every other
        // fused fold here charges its terms with one, where the one-rounding and two-rounding disciplines coincide.
        Case("presented.unit-interval-semirings-vs-oracle", () => Laws.SweptClaim(lawId: "presented.unit-interval-semirings-vs-oracle", domain: ClosedUnit, tier: Tier.Default, width: 2, claim: Subjects.UnitIntervalSemiringsExact)),

        // The star licence, proved rather than inherited: the SHIPPED idempotent certificate carries all three closures
        // with no new certificate code, on a graph where the counting material refuses forever.
        Case("presented.unit-interval-star-licensing", () => Laws.Claim(lawId: "presented.unit-interval-star-licensing", claim: Subjects.UnitIntervalStarLicensing)),

        Case("presented.material-contract-boundaries", () => Laws.Claim(lawId: "presented.material-contract-boundaries", claim: Subjects.OracleMaterialContractBoundaries)),

        Case("presented.finite-basis-outcome-is-typed", () => Laws.Claim(lawId: "presented.finite-basis-outcome-is-typed", claim: OracleClaims.FiniteBasisCapacityIsTyped)),

        // The first complement beyond Boolean. The pattern lens's complement was a two-valued surface because only one
        // material carried a De Morgan involution; the fuzzy material carries the exact one minus x, so a complemented
        // pattern is GRADED — the same spans at the complementary weights — and the lens needed no new code to say so.
        Case("presented.fuzzy-complement-lens", () => Laws.Claim(lawId: "presented.fuzzy-complement-lens", claim: Subjects.FuzzyComplementLens)),

        // The canary. Every other law here says two things agree; this one says the fused accumulate is load-bearing by
        // requiring it to DISAGREE with the per-term-rounding discipline on a floor of the swept operands. Measured over
        // five consecutive frontier windows the two diverge on 241 to 247 of the 504 cases — the single-lane edge
        // battery contributes none of them, every product there having one term — so the floor sits a quarter below the
        // observed minimum: strong enough to fail outright if the fused path were quietly rounding per term, loose
        // enough that a fresh operand window cannot trip it.
        Case("presented.fused-vs-per-product-diverges", () => Laws.DivergenceCanary(lawId: "presented.fused-vs-per-product-diverges", domain: Presented, tier: Tier.Default, width: 8, fused: Subjects.PresentedCliffordMultiply(positiveCount: 3, negativeCount: 0, degenerateCount: 0), perProduct: Subjects.CliffordPerProductOracle(positiveCount: 3, negativeCount: 0, degenerateCount: 0), minimumDivergences: 180)),

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
        Case("presented.unit-interval-fused-vs-per-term-diverges", () => Laws.DivergenceCanary(lawId: "presented.unit-interval-fused-vs-per-term-diverges", domain: Presented, tier: Tier.Default, width: 6, fused: Subjects.UnitIntervalFusedTerms, perProduct: Subjects.UnitIntervalPerTermRounding, minimumDivergences: 38)),

        // The material contract every fused kernel rests on, at every material in the set.
        Case("presented.material-fused-identities", () => Laws.SweptClaim(lawId: "presented.material-fused-identities", domain: Presented, tier: Tier.Default, width: 6, claim: Subjects.MaterialFusedIdentities)),

        // ---- phase 2: modules by presentation morphism ----
        //
        // A module is a state, a step and a readout — the stepper framing, which is this object's module theory rather
        // than a second kernel. Every case below is the SAME product at another presentation, and every cross-check is
        // either a shipped kernel or a shared-nothing oracle.

        // The zero-allocation overload, at both a signature with a degenerate generator and one without.
        Case("presented.multiply-into-twins-multiply", () => {
                Laws.VectorTwin(lawId: "presented.multiply-into-twins-multiply", domain: Presented, tier: Tier.Default, width: 8, first: Subjects.PresentedCliffordMultiplyInto(positiveCount: 3, negativeCount: 0, degenerateCount: 0), second: Subjects.PresentedCliffordMultiply(positiveCount: 3, negativeCount: 0, degenerateCount: 0), witness: null);
                Laws.VectorTwin(lawId: "presented.multiply-into-twins-multiply", domain: Presented, tier: Tier.Default, width: 16, first: Subjects.PresentedCliffordMultiplyInto(positiveCount: 3, negativeCount: 0, degenerateCount: 1), second: Subjects.PresentedCliffordMultiply(positiveCount: 3, negativeCount: 0, degenerateCount: 1), witness: null);
            }),

        // Ledger row 15: the residual at the identity twist is a derivation, and on the jet presentation its unit
        // coefficient IS FixedDual's chain-rule lift, bit for bit over the whole raw range. The claim beside it
        // separates the three twists and proves the twisted Leibniz rule where no relation can break it.
        Case("presented.jet-residual-twins-dual", () => {
                Laws.TwinBinary(lawId: "presented.jet-residual-twins-dual", domain: Dual, tier: Tier.Default, first: Subjects.PresentedJetResidual(), second: Subjects.DualChainRuleLift, witness: Subjects.JetResidualOracle);
                Laws.Claim(lawId: "presented.jet-residual-twins-dual", claim: Subjects.ResidualTwistsSeparate);
            }),

        // The pair-up theorem on an exact material: the behavior of a tensor is the termwise product of behaviors.
        Case("presented.tensor-behavior-vs-product", () => Laws.VectorTwin(lawId: "presented.tensor-behavior-vs-product", domain: Presented, tier: Tier.Default, width: Subjects.TensorLaneWidth, first: Subjects.PresentedTensorBehavior(), second: Subjects.TensorBehaviorProductOracle(), witness: Subjects.ExactTensorBehavior())),

        // THE PAIR-UP CANARY. The construction survives every material and the THEOREM does not: a tensor's cells are
        // not products of already-rounded cells, so over the house scalar the two sides of the row above must actually
        // DISAGREE. Measured over five consecutive frontier windows they diverge on 266 to 268 of the 466 swept cases —
        // the single-lane edge battery contributes almost none of them, its behaviors being zero on both sides — so the
        // floor sits a third below the observed minimum: strong enough to fail outright if pairing ever started
        // commuting with rounding, loose enough that a fresh operand window cannot trip it.
        Case("presented.pair-up-rounds-canary", () => {
                Laws.DivergenceCanary(lawId: "presented.pair-up-rounds-canary", domain: Presented, tier: Tier.Default, width: Subjects.TensorLaneWidth, fused: Subjects.PresentedFixedTensorBehavior(), perProduct: Subjects.FixedTensorBehaviorProductOracle(), minimumDivergences: 180);
                Laws.SweptClaim(lawId: "presented.pair-up-rounds-canary", domain: Presented, tier: Tier.Default, width: Subjects.TensorLaneWidth, claim: Subjects.FixedTensorBehaviorProductIsExact());
            }),

        // Ledger row 18: Dirichlet convolution IS the product at a divisibility window, so mu is the guarded star of
        // the negated strict zeta and mu ⋆ zeta is the unit. Cross-checked against the shipped factorization and
        // prime-counting kernels, which share nothing with any convolution.
        Case("presented.mobius-star-round-trip", () => Laws.Claim(lawId: "presented.mobius-star-round-trip", claim: Subjects.MobiusStarRoundTrip)),

        // Ledger rows 16 and 17: derivative matching at a finite alphabet, weighted and Boolean, against a
        // shared-nothing backtracking oracle over a pattern TREE — a construction the subject does not have at all.
        Case("presented.matcher-vs-backtracking-oracle", () => Laws.Claim(lawId: "presented.matcher-vs-backtracking-oracle", claim: Subjects.MatcherMatchesBacktrackingOracle)),

        // The weight a scaled pattern gives a span, read back out. Every other pattern statement quantifies over
        // ELEMENTS — the Leibniz rule, the matcher against its oracle — so a Scale that ignored its weight argument
        // would return perfectly valid elements and leave all of them green. This one names the value, at a counting
        // material where the scale multiplies and at a tropical one where it adds, so nothing about it can be faked.
        // Only the members it genuinely drives are credited; the rest of the pattern surface has its own creditors.
        Case("presented.pattern-scale-weights", () => Laws.Claim(lawId: "presented.pattern-scale-weights", claim: Subjects.PatternScaleWeights)),

        // The declared second axis (O2): a predicate algebra supplies conjunction, complement and satisfiability, one
        // shared loop cuts the partition, and the kernel receives a letter count and a mask — never a predicate.
        Case("presented.alphabet-refinement-partitions", () => Laws.Claim(lawId: "presented.alphabet-refinement-partitions", claim: Subjects.AlphabetRefinementPartitions)),

        Case("presented.matcher-binds-alphabet-identity", () => Laws.Claim(lawId: "presented.matcher-binds-alphabet-identity", claim: OracleClaims.MatcherRejectsDifferentAlphabetIdentity)),

        // Ledger row 20: exact machine equivalence by pairing radical, decided against brute word enumeration to the
        // Myhill bound, and the quotient proved canonical — same behavior, minimal dimension, idempotent.
        Case("presented.machine-equivalence-vs-enumeration", () => Laws.Claim(lawId: "presented.machine-equivalence-vs-enumeration", claim: Subjects.MachineEquivalenceMatchesEnumeration)),

        // Ledger row 21: a substochastic chain's powers neither vanish nor stabilize, so the iterative star refuses
        // forever and the resolvent answers in one solve. The proof is re-multiplication, not a truncation.
        Case("presented.resolvent-remultiplies", () => Laws.Claim(lawId: "presented.resolvent-remultiplies", claim: Subjects.ResolventRemultiplies)),

        // Ledger row 22: the antidifference is the guarded star of the shift on degree-bounded jets, and it reproduces
        // the shipped exactly-inverted prefix sums place for place.
        Case("presented.antidifference-vs-layer-sequence", () => Laws.Claim(lawId: "presented.antidifference-vs-layer-sequence", claim: Subjects.AntidifferenceMatchesLayerSequence)),

        // Ledger row 7: uniform prime-power fields. Degree two against the shipped extension field, above it against a
        // schoolbook polynomial oracle, since nothing in the tree constructs those fields at all.
        Case("presented.monogenic-twins-prime-extension", () => Laws.Claim(lawId: "presented.monogenic-twins-prime-extension", claim: Subjects.PrimeExtensionTwinsMonogenic)),

        // Ledger row 10: the companion quiver's product IS the projective step, so a matrix step through the shared
        // kernel reproduces MobiusStep over the whole raw range and ProjectiveStep above degree two.
        Case("presented.companion-quiver-twins-mobius", () => {
                Laws.MobiusMatchesOracle(lawId: "presented.companion-quiver-twins-mobius", domain: Mobius, tier: Tier.Default, subject: Subjects.PresentedCompanionMobius(pRaw: OneRaw, qRaw: OneRaw), oracleNumerator: Subjects.MobiusNumeratorOracle(pRaw: OneRaw, qRaw: OneRaw));
                Laws.MobiusMatchesOracle(lawId: "presented.companion-quiver-twins-mobius", domain: Fractional, tier: Tier.Default, subject: Subjects.PresentedCompanionMobius(pRaw: 0L, qRaw: HalfQ), oracleNumerator: Subjects.MobiusNumeratorOracle(pRaw: 0L, qRaw: HalfQ));
                Laws.Claim(lawId: "presented.companion-quiver-twins-mobius", claim: Subjects.CompanionQuiverTwinsProjectiveStep);
            }),

        // Ledger row 11: orientation is the top-grade coefficient of a triple join, and the join is non-metric — no
        // signature enters it — so an independent determinant is the whole cross-check.
        Case("presented.orientation-twins-determinant", () => Laws.Claim(lawId: "presented.orientation-twins-determinant", claim: Subjects.OrientationTwinsDeterminant)),

        Case("presented.complement-admission-proves-inverses", () => Laws.Claim(lawId: "presented.complement-admission-proves-inverses", claim: OracleClaims.ComplementAdmissionRequiresMutualInverses)),

        // Ledger row 19: a continued fraction is a word run through the codiscrete quiver on two objects, which IS the
        // two-by-two matrix algebra, so the convergent recurrence needs no transfer-matrix code of its own.
        Case("presented.transfer-twins-convergents", () => Laws.Claim(lawId: "presented.transfer-twins-convergents", claim: Subjects.TransferTwinsConvergents)),

        // ---- the continued-fraction lenses: one certificate, one tiling, two readings each ----

        // The equidistribution certificate is not a measured statistic but the largest partial quotient of the
        // generator's continued fraction, so the oracle is that fraction walked independently in BigInteger.
        Case("certified.certificate-vs-partial-quotients", () => Laws.Claim(lawId: "certified.certificate-vs-partial-quotients", claim: Subjects.CertificateMatchesPartialQuotients)),

        // Ring-coordinate random access and the streamed substitution are two implementations of ONE tiling: the walk
        // inverts and steps by a tile vector, Contains equals the walked vertex set over a covered box, and the walk
        // word is a factor of the streamed word.
        Case("quasicrystal.chain-walk-vs-streamed-word", () => Laws.Claim(lawId: "quasicrystal.chain-walk-vs-streamed-word", claim: Subjects.ChainWalkMatchesStreamedWord)),

        // ---- the fixed-point vectors: FixedVector2 (the plane) and FixedVector3 (the space) ----
        //
        // All four fused kernels route through FixedQ4816.RoundProductSum after an OR-gated lane choice: below the gate
        // a plain long accumulator, above it an Int128 one. Both lanes implement the SAME contract — one ties-to-even
        // rounding of the exact product sum at shift sixteen, wrapped to the carrier — and that is proved rather than
        // assumed. Under the gate every product magnitude is below 2^(2k) and the long sum cannot overflow; above it
        // the Int128 sum CAN wrap, but a wrap moves the exact sum by k·2¹²⁸ and hence the rounded value by k·2¹¹²,
        // which is zero modulo 2⁶⁴ and cannot move tie parity. So the oracle is derived from the EXACT sum in every
        // case, and the two lanes are swept by two cases against the one reference.
        Case("vector.plane-products-vs-oracle", () => Laws.BinaryMatchesOracle(lawId: "vector.plane-products-vs-oracle", domain: Vector, tier: Tier.Default, subject: Subjects.PlaneProducts, oracle: Subjects.PlaneProductsOracle)),
        Case("vector.space-products-vs-oracle", () => {
                Laws.VectorMatchesOracle(lawId: "vector.space-products-vs-oracle", domain: Vector, tier: Tier.Default, width: 3, subject: Subjects.SpaceDotLanes, oracle: Subjects.SpaceDotLanesOracle);
                Laws.VectorMatchesOracle(lawId: "vector.space-products-vs-oracle", domain: Vector, tier: Tier.Default, width: 3, subject: Subjects.SpaceCrossLanes, oracle: Subjects.SpaceCrossLanesOracle);
            }),
        // The lane the full-range cases cannot reach: a sixty-four-bit draw lands below 2³¹ once in 2³² draws, so
        // without a fold the narrow branch would be pinned by the edge battery alone. Subject and oracle apply the
        // IDENTICAL fold, so every sampled operand reaches a defined comparison rather than being skipped
        // asymmetrically.
        Case("vector.narrow-lane-vs-oracle", () => {
                Laws.BinaryMatchesOracle(lawId: "vector.narrow-lane-vs-oracle", domain: VectorNarrow, tier: Tier.Default, subject: Subjects.NarrowPlaneProducts, oracle: Subjects.NarrowPlaneProductsOracle);
                Laws.VectorMatchesOracle(lawId: "vector.narrow-lane-vs-oracle", domain: VectorNarrow, tier: Tier.Default, width: 3, subject: Subjects.NarrowSpaceDotLanes, oracle: Subjects.NarrowSpaceDotLanesOracle);
                Laws.VectorMatchesOracle(lawId: "vector.narrow-lane-vs-oracle", domain: VectorNarrow, tier: Tier.Default, width: 3, subject: Subjects.NarrowSpaceCrossLanes, oracle: Subjects.NarrowSpaceCrossLanesOracle);
            }),
        Case("vector.componentwise-vs-oracle", () => Laws.SweptClaim(lawId: "vector.componentwise-vs-oracle", domain: Vector, tier: Tier.Default, width: 4, claim: Subjects.VectorComponentwiseMatchesOracle)),
        // Each swept case is evaluated TWICE — once on the full-range operands, which mostly drive the refusal path,
        // and once on narrow-folded ones, which mostly drive the success path — so both branches of both norms are
        // covered at every draw rather than only where the sampler happens to land.
        Case("vector.norm-vs-oracle", () => Laws.SweptClaim(lawId: "vector.norm-vs-oracle", domain: VectorNorm, tier: Tier.Default, width: 3, claim: Subjects.VectorNormMatchesOracle)),
        // The family's cross-type seam: the plane embeds in the space EXACTLY. Doc-anchored — FixedVector2.Wedge's
        // remark calls itself the planar restriction of FixedVector3.Cross, and Cross's returns clause points back at
        // it. The two gates coincide (Wedge ORs four magnitudes at 2³¹, Cross ORs six at 2³¹ and the two embedded Z
        // lanes contribute zero), so the wedge embedding is exact at FULL range rather than on a sublattice; the dot
        // embedding crosses lanes (2³¹ against 2³⁰) and is still exact, which is a statement worth making on its own.
        Case("vector.kinship-exact", () => {
                Laws.VectorTwin(lawId: "vector.kinship-exact", domain: Vector, tier: Tier.Default, width: 2, first: Subjects.PlaneWedgeAndDotLanes, second: Subjects.SpaceEmbeddedWedgeAndDotLanes, witness: Subjects.PlaneWedgeAndDotOracleLanes);
                Laws.SweptClaim(lawId: "vector.kinship-exact", domain: Vector, tier: Tier.Default, width: 2, claim: Subjects.VectorPlaneInSpaceExact);
            }),
        // Every operand is m·2¹⁶ with |m| ≤ 4092, so every product, sum and difference below is EXACT in Q16 and the
        // identities are equalities of integers rather than approximations. The magnitude audit that makes them
        // unconditional: a cross lane stays under 2⁴¹, a nested cross and a scaled dot under 2⁵⁴, and the Jacobi sum
        // under 3·2⁵⁴ — all inside the signed carrier, so nothing wraps. Note the INNER cross runs on the narrow lane
        // and the OUTER one on the Int128 lane, so a single exact identity exercises both accumulators.
        Case("vector.exact-algebra-on-the-sublattice", () => Laws.SweptClaim(lawId: "vector.exact-algebra-on-the-sublattice", domain: VectorLattice, tier: Tier.Default, width: 6, claim: Subjects.VectorExactAlgebra)),
        Case("vector.identity-and-negation", () => Laws.SweptClaim(lawId: "vector.identity-and-negation", domain: Vector, tier: Tier.Default, width: 3, claim: Subjects.VectorIdentityAndNegation)),
        Case("vector.construction-and-refusals", () => Laws.Claim(lawId: "vector.construction-and-refusals", claim: Subjects.VectorConstructionAndRefusals)),
        // Normalization is a THREE-STAGE pipeline: a common power-of-two precondition at leading bit forty-five, a
        // Q16-scaled nearest root as the one common denominator, and one ties-to-even ratio per component. Its distance
        // from the ideal single-rounding unit vector is PROVED rather than measured — the precondition perturbs the
        // exact ratio by at most 2⁻⁴⁶ where it is a left shift and at most 2⁻²⁸ where it is a rounding right shift, and
        // both are far below a half, so the two disciplines can part only at a ratio within 2⁻²⁸ of a half-integer and
        // then by exactly one raw.
        Case("vector.normalize-vs-ideal-and-staged", () => Laws.SweptClaim(lawId: "vector.normalize-vs-ideal-and-staged", domain: VectorDirection, tier: Tier.Default, width: 3, claim: Subjects.VectorNormalizeMatchesOracles)),

        // The other member quaternion.exp-log-seam declared uncovered. Its DIRECTION output turned out to have indirect
        // coverage — transposing a lane reddens the exp/log seams — but its MAGNITUDE output had none, and that is the
        // half stated here as an exact identity rather than a tolerance: the returned raw magnitude IS the nearest
        // integer root of the exact BigInteger squared sum, which is stronger than any ULP bound a float reference
        // could set.
        Case("vector.normalize-with-magnitude-full-unsigned-width", () => Laws.Claim(lawId: "vector.normalize-with-magnitude-full-unsigned-width", claim: TransformKernelClaims.NormalizeWithMagnitudeFullUnsignedWidthSurface)),
        Case("vector.normalize-ideal-bound-full-width-sweep", () => Laws.Claim(lawId: "vector.normalize-ideal-bound-full-width-sweep", claim: TransformKernelClaims.NormalizeIdealBoundWidthSweepSurface)),
        Case("vector.presentation-ladder", () => Laws.Claim(lawId: "vector.presentation-ladder", claim: Subjects.VectorPresentationMatchesLadder)),
        // The inbound seam, the mirror of the row above. Its own ladder, not a round trip through that one:
        // ToVector3 is lossy, so a round trip would pin only the rows that survive it and would silently stop
        // discriminating exactly where the narrowing is interesting.
        Case("vector.adoption-ladder", () => Laws.Claim(lawId: "vector.adoption-ladder", claim: Subjects.VectorAdoptionMatchesLadder)),
        Case("vector.record-print-is-components-only", () => Laws.Claim(lawId: "vector.record-print-is-components-only", claim: Subjects.VectorRecordPrintsComponentsOnly)),
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
        Case("vector.fused-products-diverge-from-per-product", () => Laws.DivergenceCanary(lawId: "vector.fused-products-diverge-from-per-product", domain: VectorNarrow, tier: Tier.Default, width: 3, fused: Subjects.NarrowFusedProductLanes, perProduct: Subjects.NarrowPerProductLanes, minimumDivergences: 75)),
        // The norm's own canary, on the same fold and for the same reason: one rounding of the exact sum of squares has
        // to be observably different from rounding each square first. Measured over the same five frontier windows they
        // diverge on 117, 118, 119, 122 and 126 of the 437 swept cases — a narrower band than the product canary's,
        // because a square's low bits are less unevenly distributed than a mixed product's — so the floor again sits a
        // quarter below the observed minimum.
        Case("vector.fused-norm-diverges-from-per-square", () => Laws.DivergenceCanary(lawId: "vector.fused-norm-diverges-from-per-square", domain: VectorNarrow, tier: Tier.Default, width: 3, fused: Subjects.NarrowFusedSquaredNormLanes, perProduct: Subjects.NarrowPerSquareLanes, minimumDivergences: 87)),

        // ---- FixedPosition: the hierarchical world coordinate, an EXACT type throughout ----
        Case("position.canonical-vs-oracle", () => Laws.SweptClaim(lawId: "position.canonical-vs-oracle", domain: Position, tier: Tier.Default, width: 4, claim: Subjects.PositionCanonicalExact)),
        Case("position.delta-vs-oracle", () => Laws.SweptClaim(lawId: "position.delta-vs-oracle", domain: PositionDelta, tier: Tier.Default, width: 6, claim: Subjects.PositionDeltaExact)),
        Case("position.translate-vs-oracle", () => Laws.SweptClaim(lawId: "position.translate-vs-oracle", domain: PositionTranslate, tier: Tier.Default, width: 6, claim: Subjects.PositionTranslateExact)),
        Case("position.group-structure-exact", () => Laws.SweptClaim(lawId: "position.group-structure-exact", domain: PositionTranslate, tier: Tier.Default, width: 6, claim: Subjects.PositionGroupStructureExact)),
        Case("position.render-relative-ladder", () => Laws.Claim(lawId: "position.render-relative-ladder", claim: Subjects.PositionRenderRelativeLadder)),
        Case("position.print-members-invariant-cells", () => Laws.Claim(lawId: "position.print-members-invariant-cells", claim: Subjects.PositionPrintsInvariantCells)),

        // ---- FixedRigidTransform: the unit dual quaternion ----
        Case("rigid.compose-vs-oracle", () => Laws.VectorMatchesOracle(lawId: "rigid.compose-vs-oracle", domain: Rigid, tier: Tier.Default, width: 8, subject: Subjects.RigidComposeLanes, oracle: Subjects.RigidComposeOracle)),
        Case("rigid.identity-and-inverse-exact", () => Laws.SweptClaim(lawId: "rigid.identity-and-inverse-exact", domain: Rigid, tier: Tier.Default, width: 8, claim: Subjects.RigidIdentityAndInverseExact)),
        Case("rigid.translation-vs-oracle", () => Laws.SweptClaim(lawId: "rigid.translation-vs-oracle", domain: Rigid, tier: Tier.Default, width: 8, claim: Subjects.RigidTranslationExact)),
        Case("rigid.from-rotation-translation", () => Laws.SweptClaim(lawId: "rigid.from-rotation-translation", domain: Rigid, tier: Tier.Default, width: 8, claim: Subjects.RigidFromRotationTranslation)),
        Case("rigid.normalize-unit-constraints", () => Laws.SweptClaim(lawId: "rigid.normalize-unit-constraints", domain: RigidDirection, tier: Tier.Default, width: 8, claim: Subjects.RigidNormalizeUnitConstraints)),
        Case("rigid.from-dual-quaternion-refusals", () => Laws.Claim(lawId: "rigid.from-dual-quaternion-refusals", claim: Subjects.RigidFromDualQuaternionRefusals)),
        Case("rigid.compose-normalized-twin", () => Laws.SweptClaim(lawId: "rigid.compose-normalized-twin", domain: Rigid, tier: Tier.Default, width: 8, claim: Subjects.RigidComposeNormalizedTwin)),
        Case("rigid.transform-point-vs-oracle", () => Laws.SweptClaim(lawId: "rigid.transform-point-vs-oracle", domain: RigidPoint, tier: Tier.Default, width: 8, claim: Subjects.RigidTransformPointExact)),
        Case("rigid.exp-log-seam", () => Laws.Claim(lawId: "rigid.exp-log-seam", claim: Subjects.RigidExpLogSeam)),
        Case("rigid.sclerp-endpoints-and-screw", () => Laws.Claim(lawId: "rigid.sclerp-endpoints-and-screw", claim: Subjects.RigidScLerpEndpointsAndScrew)),

        // ---- FixedRateAccumulator and FixedVector3RateAccumulator: an EXACT rational ledger ----
        Case("rate.schedule-vs-ledger", () => Laws.SweptClaim(lawId: "rate.schedule-vs-ledger", domain: Rate, tier: Tier.Default, width: 4, claim: Subjects.RateScheduleVsLedger)),
        Case("rate.construction-and-refusals", () => Laws.Claim(lawId: "rate.construction-and-refusals", claim: Subjects.RateConstructionAndRefusals)),
        Case("rate.unit-advance-exact", () => Laws.SweptClaim(lawId: "rate.unit-advance-exact", domain: Rate, tier: Tier.Default, width: 2, claim: Subjects.RateUnitAdvanceExact)),
        Case("rate.vector-axes-independent", () => Laws.SweptClaim(lawId: "rate.vector-axes-independent", domain: Rate, tier: Tier.Default, width: 6, claim: Subjects.RateVectorAxesIndependent)),
        Case("rate.vector-construction-and-refusals", () => Laws.Claim(lawId: "rate.vector-construction-and-refusals", claim: Subjects.RateVectorConstructionAndRefusals)),

        // ---- FixedSymmetricSolve: scale-free 2×2/3×3 symmetric solve and invert (internal — see the type's own
        // remarks for the bit budget and the Invert-only refusal envelope) ----
        Case("symmetric-solve.solve2-vs-oracle", () => Laws.SweptClaim(lawId: "symmetric-solve.solve2-vs-oracle", domain: SymmetricSolve2, tier: Tier.Default, width: 5, claim: SymmetricSolveClaims.Solve2VsOracle)),
        Case("symmetric-solve.solve3-vs-oracle", () => Laws.SweptClaim(lawId: "symmetric-solve.solve3-vs-oracle", domain: SymmetricSolve3, tier: Tier.Default, width: 9, claim: SymmetricSolveClaims.Solve3VsOracle)),
        Case("symmetric-solve.invert2-vs-oracle", () => Laws.SweptClaim(lawId: "symmetric-solve.invert2-vs-oracle", domain: SymmetricInvert2, tier: Tier.Default, width: 3, claim: SymmetricSolveClaims.Invert2VsOracle)),
        Case("symmetric-solve.invert3-vs-oracle", () => Laws.SweptClaim(lawId: "symmetric-solve.invert3-vs-oracle", domain: SymmetricInvert3, tier: Tier.Default, width: 6, claim: SymmetricSolveClaims.Invert3VsOracle)),
        Case("symmetric-solve.solve2-vs-bareiss", () => Laws.SweptClaim(lawId: "symmetric-solve.solve2-vs-bareiss", domain: SymmetricSolve2, tier: Tier.Default, width: 5, claim: SymmetricSolveClaims.Solve2VsBareiss)),
        Case("symmetric-solve.solve3-vs-bareiss", () => Laws.SweptClaim(lawId: "symmetric-solve.solve3-vs-bareiss", domain: SymmetricSolve3, tier: Tier.Default, width: 9, claim: SymmetricSolveClaims.Solve3VsBareiss)),
        Case("symmetric-solve.invert2-vs-bareiss", () => Laws.SweptClaim(lawId: "symmetric-solve.invert2-vs-bareiss", domain: SymmetricInvert2, tier: Tier.Default, width: 3, claim: SymmetricSolveClaims.Invert2VsBareiss)),
        Case("symmetric-solve.invert3-vs-bareiss", () => Laws.SweptClaim(lawId: "symmetric-solve.invert3-vs-bareiss", domain: SymmetricInvert3, tier: Tier.Default, width: 6, claim: SymmetricSolveClaims.Invert3VsBareiss)),
        Case("symmetric-solve.solve3-extreme-magnitude-agrees", () => Laws.Claim(lawId: "symmetric-solve.solve3-extreme-magnitude-agrees", claim: SymmetricSolveClaims.Solve3ExtremeMagnitudeAgrees)),
        Case("symmetric-solve.singular-matrices-refuse", () => Laws.Claim(lawId: "symmetric-solve.singular-matrices-refuse", claim: SymmetricSolveClaims.SingularMatricesRefuse)),
        Case("symmetric-solve.invert-large-magnitude-envelope-refuses", () => Laws.Claim(lawId: "symmetric-solve.invert-large-magnitude-envelope-refuses", claim: SymmetricSolveClaims.InvertLargeMagnitudeEnvelopeRefuses)),
        Case("symmetric-solve.lossy-rank-one-singular-refuses", () => Laws.Claim(lawId: "symmetric-solve.lossy-rank-one-singular-refuses", claim: SymmetricSolveClaims.LossyRankOneSingularRefuses)),
        Case("symmetric-solve.lossless-boundary-is-exact", () => Laws.Claim(lawId: "symmetric-solve.lossless-boundary-is-exact", claim: SymmetricSolveClaims.LosslessBoundaryIsExact)),
        Case("symmetric-solve.divide-magnitude-rounded-full-width-agrees", () => Laws.Claim(lawId: "symmetric-solve.divide-magnitude-rounded-full-width-agrees", claim: SymmetricSolveClaims.DivideMagnitudeRoundedFullWidthAgrees)),
        Case("symmetric-solve.refusal-leaves-no-stale-output", () => Laws.Claim(lawId: "symmetric-solve.refusal-leaves-no-stale-output", claim: SymmetricSolveClaims.RefusalLeavesNoStaleOutput)),
        Case("symmetric-solve.solve2-residual-within-envelope", () => Laws.SweptClaim(lawId: "symmetric-solve.solve2-residual-within-envelope", domain: SymmetricSolve2, tier: Tier.Default, width: 5, claim: SymmetricSolveClaims.Solve2ResidualWithinEnvelope)),
        Case("symmetric-solve.solve3-residual-within-envelope", () => Laws.SweptClaim(lawId: "symmetric-solve.solve3-residual-within-envelope", domain: SymmetricSolve3, tier: Tier.Default, width: 9, claim: SymmetricSolveClaims.Solve3ResidualWithinEnvelope)),
        Case("symmetric-solve.invert2-residual-within-envelope", () => Laws.SweptClaim(lawId: "symmetric-solve.invert2-residual-within-envelope", domain: SymmetricInvert2, tier: Tier.Default, width: 3, claim: SymmetricSolveClaims.Invert2ResidualWithinEnvelope)),
        Case("symmetric-solve.invert3-residual-within-envelope", () => Laws.SweptClaim(lawId: "symmetric-solve.invert3-residual-within-envelope", domain: SymmetricInvert3, tier: Tier.Default, width: 6, claim: SymmetricSolveClaims.Invert3ResidualWithinEnvelope)),
        Case("symmetric-solve.solve3-non-diagonal-exact-value", () => Laws.Claim(lawId: "symmetric-solve.solve3-non-diagonal-exact-value", claim: SymmetricSolveClaims.Solve3NonDiagonalExactValue)),
        Case("symmetric-solve.solve3-all-cofactors-exact-value", () => Laws.Claim(lawId: "symmetric-solve.solve3-all-cofactors-exact-value", claim: SymmetricSolveClaims.Solve3AllCofactorsExactValue)),
        Case("symmetric-solve.apply2-vs-oracle", () => Laws.SweptClaim(lawId: "symmetric-solve.apply2-vs-oracle", domain: SymmetricApply2, tier: Tier.Default, width: 5, claim: SymmetricSolveClaims.Apply2VsOracle)),
        Case("symmetric-solve.apply3-vs-oracle", () => Laws.SweptClaim(lawId: "symmetric-solve.apply3-vs-oracle", domain: SymmetricApply3, tier: Tier.Default, width: 9, claim: SymmetricSolveClaims.Apply3VsOracle)),
        Case("symmetric-solve.apply-refusal-and-symmetry", () => Laws.Claim(lawId: "symmetric-solve.apply-refusal-and-symmetry", claim: SymmetricSolveClaims.ApplyRefusalAndSymmetry)),

        // ---- FusedArithmetic: the mixed-scale one-rounding products (internal) ----
        Case("mixed-scale.product-vs-oracle", () => Laws.SweptClaim(lawId: "mixed-scale.product-vs-oracle", domain: MixedScale, tier: Tier.Default, width: 4, claim: MixedScaleClaims.ProductVsOracle)),
        Case("mixed-scale.checked-product-matches-representability", () => Laws.SweptClaim(lawId: "mixed-scale.checked-product-matches-representability", domain: MixedScale, tier: Tier.Default, width: 4, claim: MixedScaleClaims.CheckedProductMatchesRepresentability)),
        Case("mixed-scale.triple-product-vs-oracle", () => Laws.SweptClaim(lawId: "mixed-scale.triple-product-vs-oracle", domain: MixedScaleTriple, tier: Tier.Default, width: 4, claim: MixedScaleClaims.TripleProductVsOracle)),
        Case("mixed-scale.extreme-scale-counts-are-congruent", () => Laws.Claim(lawId: "mixed-scale.extreme-scale-counts-are-congruent", claim: MixedScaleClaims.ExtremeScaleCountsAreCongruent)),

        // ---- FixedDirectedRounding: the conservative upper bounds (public — Puck.World.Data's first production caller) ----
        Case("directed-rounding.ceiling-square-root-is-least-upper-bound", () => Laws.SweptClaim(lawId: "directed-rounding.ceiling-square-root-is-least-upper-bound", domain: DirectedRoot, tier: Tier.Default, width: 4, claim: DirectedRoundingClaims.CeilingSquareRootIsLeastUpperBound)),
        Case("directed-rounding.ceiling-product-is-least-upper-bound", () => Laws.SweptClaim(lawId: "directed-rounding.ceiling-product-is-least-upper-bound", domain: DirectedProduct, tier: Tier.Default, width: 4, claim: DirectedRoundingClaims.CeilingProductIsLeastUpperBound)),
        Case("directed-rounding.ceiling-quotient-is-least-upper-bound", () => Laws.SweptClaim(lawId: "directed-rounding.ceiling-quotient-is-least-upper-bound", domain: DirectedQuotient, tier: Tier.Default, width: 4, claim: DirectedRoundingClaims.CeilingQuotientIsLeastUpperBound)),
        Case("directed-rounding.ceiling-product-sum-is-least-upper-bound", () => Laws.SweptClaim(lawId: "directed-rounding.ceiling-product-sum-is-least-upper-bound", domain: DirectedProductSum, tier: Tier.Default, width: 4, claim: DirectedRoundingClaims.CeilingProductSumIsLeastUpperBound)),
        Case("directed-rounding.ceiling-magnitude-is-least-upper-bound", () => Laws.SweptClaim(lawId: "directed-rounding.ceiling-magnitude-is-least-upper-bound", domain: DirectedMagnitude, tier: Tier.Default, width: 4, claim: DirectedRoundingClaims.CeilingMagnitudeIsLeastUpperBound)),
        Case("directed-rounding.negative-operands-refuse", () => Laws.Claim(lawId: "directed-rounding.negative-operands-refuse", claim: DirectedRoundingClaims.NegativeOperandsRefuse)),

        // ---- FixedMassProperties: volumes, bodies, transfer, compound and the inversions (internal) ----
        Case("mass-properties.volumes-vs-oracle", () => Laws.SweptClaim(lawId: "mass-properties.volumes-vs-oracle", domain: MassVolume, tier: Tier.Default, width: 3, claim: MassPropertyClaims.VolumesVsOracle)),
        Case("mass-properties.sphere-vs-oracle", () => Laws.SweptClaim(lawId: "mass-properties.sphere-vs-oracle", domain: MassSphere, tier: Tier.Default, width: 4, claim: MassPropertyClaims.SphereVsOracle)),
        Case("mass-properties.box-vs-oracle", () => Laws.SweptClaim(lawId: "mass-properties.box-vs-oracle", domain: MassBox, tier: Tier.Default, width: 4, claim: MassPropertyClaims.BoxVsOracle)),
        Case("mass-properties.cylinder-vs-oracle", () => Laws.SweptClaim(lawId: "mass-properties.cylinder-vs-oracle", domain: MassCylinder, tier: Tier.Default, width: 4, claim: MassPropertyClaims.CylinderVsOracle)),
        Case("mass-properties.capsule-vs-oracle", () => Laws.SweptClaim(lawId: "mass-properties.capsule-vs-oracle", domain: MassCapsule, tier: Tier.Default, width: 4, claim: MassPropertyClaims.CapsuleVsOracle)),
        Case("mass-properties.parallel-axis-vs-oracle", () => Laws.SweptClaim(lawId: "mass-properties.parallel-axis-vs-oracle", domain: MassParallelAxis, tier: Tier.Default, width: 10, claim: MassPropertyClaims.ParallelAxisVsOracle)),
        Case("mass-properties.compound-vs-oracle", () => Laws.SweptClaim(lawId: "mass-properties.compound-vs-oracle", domain: MassCompound, tier: Tier.Default, width: 10, claim: MassPropertyClaims.CompoundVsOracle)),
        Case("mass-properties.capsule-degenerates-to-sphere", () => Laws.Claim(lawId: "mass-properties.capsule-degenerates-to-sphere", claim: MassPropertyClaims.CapsuleDegeneratesToSphere)),
        Case("mass-properties.inversion-refuses-below-resolution", () => Laws.Claim(lawId: "mass-properties.inversion-refuses-below-resolution", claim: MassPropertyClaims.InversionRefusesBelowResolution)),
        Case("mass-properties.pinned-pi-is-correctly-rounded", () => Laws.Claim(lawId: "mass-properties.pinned-pi-is-correctly-rounded", claim: MassPropertyClaims.PinnedPiIsCorrectlyRounded)),

        // ---- the GF(2)[t] ring beneath the binary fields ----
        // EVERYTHING in this family is EXACT. There is no rounding discipline anywhere in BinaryPolynomial, so the
        // substrate condition does not merely get discharged leg by leg — it never arises, and each leg below says so
        // in those words rather than reciting a ties-to-even story that does not apply. What IS live here is
        // reduction (the fold's direction and shift), the width and carry edges the packed carrier imposes, and the
        // refusal contracts.
        Case("polynomial.additive-group-and-accessors", () => Laws.SweptClaim(lawId: "polynomial.additive-group-and-accessors", domain: BinaryPolynomialRing, tier: Tier.Default, width: 2, claim: Subjects.BinaryPolynomialAdditiveAndAccessors)),
        Case("polynomial.multiply-vs-carryless-oracle", () => {
                Laws.ScalarBinaryMatchesOracle(lawId: "polynomial.multiply-vs-carryless-oracle", domain: BinaryPolynomialRing, tier: Tier.Default, subject: Subjects.BinaryPolynomialMultiply, oracle: Subjects.BinaryPolynomialMultiplyOracle);
                Laws.SweptClaim(lawId: "polynomial.multiply-vs-carryless-oracle", domain: BinaryPolynomialRing, tier: Tier.Default, width: 2, claim: Subjects.BinaryPolynomialCheckedMultiplyAndRingLaws);
            }),
        Case("polynomial.divrem-vs-monomial-oracle", () => Laws.SweptClaim(lawId: "polynomial.divrem-vs-monomial-oracle", domain: BinaryPolynomialDivision, tier: Tier.Default, width: 2, claim: Subjects.BinaryPolynomialDivRemVsOracle)),
        Case("polynomial.gcd-vs-binary-descent-oracle", () => Laws.SweptClaim(lawId: "polynomial.gcd-vs-binary-descent-oracle", domain: BinaryPolynomialGcd, tier: Tier.Default, width: 2, claim: Subjects.BinaryPolynomialGcdVsOracle)),
        Case("polynomial.shifts-are-monomial-arithmetic", () => Laws.SweptClaim(lawId: "polynomial.shifts-are-monomial-arithmetic", domain: BinaryPolynomialRing, tier: Tier.Default, width: 2, claim: Subjects.BinaryPolynomialShiftsAreMonomialArithmetic)),
        Case("polynomial.irreducible-census-and-trial-division", () => Laws.Claim(lawId: "polynomial.irreducible-census-and-trial-division", claim: () => Subjects.BinaryPolynomialIrreducibility(censusDegree: 12, trialDegree: 8))),
        Case("polynomial.primitive-order-and-census", () => Laws.Claim(lawId: "polynomial.primitive-order-and-census", claim: () => Subjects.BinaryPolynomialPrimitivity(censusDegree: 10))),
        // IsIrreducible is cited here even though the body never calls it: FactorOddCycle decides every candidate with
        // it, so a wrong decision moves the factor list this case compares against the cyclotomic cosets — which the
        // campaign's mutation probe confirmed in both directions. Nothing else in the body is credited on that basis.
        Case("polynomial.factor-odd-cycle-vs-cyclotomic-cosets", () => Laws.Claim(lawId: "polynomial.factor-odd-cycle-vs-cyclotomic-cosets", claim: Subjects.BinaryPolynomialFactorOddCycle)),
        // DivRem is this family's own hot path: operator / and operator % are one call through to it
        // (BinaryPolynomial.cs:100-108), GreatestCommonDivisor reaches it through operator % (cs:220), and
        // FactorOddCycle's quotient loop calls it directly (cs:171). NOT IsIrreducible's delegate, which a previous
        // wording claimed: that route reaches BinaryFieldKernels.PolynomialRemainder, a self-contained shift-and-XOR
        // long division over the packed carrier (BinaryFieldKernels.cs:953-986), and touches DivRem nowhere.
        Case("smoke.polynomial-divrem-vs-monomial-oracle", () => Laws.SweptClaim(lawId: "smoke.polynomial-divrem-vs-monomial-oracle", domain: SmokeDomain, tier: Tier.Smoke, width: 2, claim: Subjects.BinaryPolynomialDivRemVsOracle)),

        // ---- the GF(2^k) quotients ----
        // EVERYTHING here is EXACT too. No rounding discipline exists anywhere in BinaryField<T>, so the substrate
        // condition drops out of every leg below and each says so in those words rather than reciting a ties-to-even
        // story that does not apply. What IS live is reduction (which modulus the fold actually applies, and in which
        // direction), representation (the leading term the tail form deliberately elides), the width and carry edges
        // five carriers impose, and the refusal contracts. Hardware-versus-fallback and rung-versus-scalar parity is
        // Post's binary-field stage and is deliberately NOT re-gated here: these laws exercise the mathematics through
        // the public surface.
        Case("binary-field.product-and-reduction-vs-oracle", () => Laws.SweptClaim(lawId: "binary-field.product-and-reduction-vs-oracle", domain: BinaryFieldDomain, tier: Tier.Default, width: 3, claim: Subjects.BinaryFieldProductAndReductionExact)),
        // Deliberately NOT citing BinaryFieldCatalog: the field axioms hold under ANY modulus with a non-zero constant
        // term, so this case could not catch a wrong catalog constant and must not claim to. BinaryFieldTail is
        // withheld for the same reason and was cited here in error: the tail IS the modulus, and a field running under
        // a legal modulus other than the one it was handed satisfies every line of this case — the campaign's probe did
        // exactly that and reddened five other binary-field cases while this one stayed green. ReductionTail answers to
        // binary-field.product-and-reduction-vs-oracle, which reads it back against the published pair.
        // BinaryFieldDegreeMember stays: Degree drives the operand fold, so it is load-bearing here.
        Case("binary-field.axioms-at-five-carriers", () => Laws.SweptClaim(lawId: "binary-field.axioms-at-five-carriers", domain: BinaryFieldAxioms, tier: Tier.Default, width: 3, claim: Subjects.BinaryFieldAxiomsExact)),
        // Only the five catalog fields here: Inverse, Divide and SquareRoot's uniqueness all require an irreducible
        // modulus, and the catalog is the set this suite has an irreducibility statement for. A drawn modulus would be
        // reducible almost always and the statements would be meaningless.
        Case("binary-field.multiplicative-group-vs-oracle", () => {
                Laws.SweptClaim(lawId: "binary-field.multiplicative-group-vs-oracle", domain: BinaryFieldGroup, tier: Tier.Default, width: 3, claim: Subjects.BinaryFieldGroupExact);
                Laws.Claim(lawId: "binary-field.multiplicative-group-vs-oracle", claim: Subjects.BinaryFieldGroupRefusals);
            }),
        // A fixed claim rather than a swept one on purpose: a region statement is about LENGTH, ALIASING and the
        // vector rungs' tails, and the arithmetic each element carries is pinned element by element by the three cases
        // above. Sweeping the content would re-buy what those already own at a thousand times the cost.
        Case("binary-field.regions-vs-oracle", () => Laws.Claim(lawId: "binary-field.regions-vs-oracle", claim: Subjects.BinaryFieldRegionsExact)),
        Case("binary-field.construction-and-refusals", () => Laws.Claim(lawId: "binary-field.construction-and-refusals", claim: Subjects.BinaryFieldConstructionAndRefusals)),
        Case("binary-field.irreducibility-vs-trial-division", () => Laws.Claim(lawId: "binary-field.irreducibility-vs-trial-division", claim: () => Subjects.BinaryFieldIrreducibility(censusDegree: 8, trialDegree: 8))),

        // The wide degrees the case above can only take on the catalog's word. It calls IsIrreducible() on all five
        // presets, but a `true` there is the subject reporting on itself; nothing independent says the degree-32, -64
        // and -128 moduli are irreducible. These two prove it — positives by an exact multiplicative-order certificate
        // in BigInteger, negatives by carryless construction — and the sweep is the same body at scale, which is why it
        // builds its whole basis inline rather than consuming a Domain.
        Case("binary-field.wide-degree-irreducibility-certificates", () => Laws.Claim(lawId: "binary-field.wide-degree-irreducibility-certificates", claim: BinaryFieldWideDegreeClaims.WideDegreeIrreducibilityCertificatesSurface)),
        Case("binary-field.wide-degree-irreducibility-sweep", () => Laws.Claim(lawId: "binary-field.wide-degree-irreducibility-sweep", claim: BinaryFieldWideDegreeClaims.WideDegreeIrreducibilitySweepSurface)),
        // The family's hottest kernel: every region rung's table and matrix, every inversion chain step, every
        // exponentiation and every square resolves to Multiply.
        Case("smoke.binary-field-product-vs-oracle", () => {
                Laws.VectorMatchesOracle(lawId: "smoke.binary-field-product-vs-oracle", domain: SmokeDomain, tier: Tier.Smoke, width: 8, subject: Subjects.BinaryFieldMultiply8, oracle: Subjects.BinaryFieldProductOracle(degree: 8, reductionTail: 0x1BUL));
                Laws.VectorMatchesOracle(lawId: "smoke.binary-field-product-vs-oracle", domain: SmokeDomain, tier: Tier.Smoke, width: 16, subject: Subjects.BinaryFieldMultiply16, oracle: Subjects.BinaryFieldProductOracle(degree: 16, reductionTail: 0x2BUL));
            }),

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
        Case("prime-field.create-and-refusals", () => Laws.Claim(lawId: "prime-field.create-and-refusals", claim: Subjects.PrimeFieldCreateAndRefusals)),
        Case("prime-field.arithmetic-vs-oracle", () => Laws.SweptClaim(lawId: "prime-field.arithmetic-vs-oracle", domain: PrimeFieldBand, tier: Tier.Default, width: 1, claim: Subjects.PrimeFieldArithmeticExact)),
        Case("prime-field.pow-vs-modpow", () => Laws.SweptClaim(lawId: "prime-field.pow-vs-modpow", domain: PrimeFieldChain, tier: Tier.Default, width: 1, claim: Subjects.PrimeFieldPowMatchesModularPower)),
        Case("prime-field.inverse-and-batch", () => Laws.Claim(lawId: "prime-field.inverse-and-batch", claim: Subjects.PrimeFieldInverseAndBatch)),
        Case("prime-field.legendre-vs-reciprocity", () => Laws.SweptClaim(lawId: "prime-field.legendre-vs-reciprocity", domain: PrimeFieldRoot, tier: Tier.Default, width: 1, claim: Subjects.PrimeFieldLegendreMatchesReciprocity)),
        Case("prime-field.sqrt-descent-and-refusal", () => Laws.SweptClaim(lawId: "prime-field.sqrt-descent-and-refusal", domain: PrimeFieldRoot, tier: Tier.Default, width: 1, claim: Subjects.PrimeFieldSquareRootExact)),
        Case("prime-field.is-prime-vs-sieve-and-witness-ladder", () => Laws.Claim(lawId: "prime-field.is-prime-vs-sieve-and-witness-ladder", claim: Subjects.PrimeFieldIsPrimeAgainstSieveAndWitnesses)),
        Case("prime-field.is-prime-vs-witness-oracle", () => Laws.SweptClaim(lawId: "prime-field.is-prime-vs-witness-oracle", domain: PrimeFieldPrimality, tier: Tier.Default, width: 1, claim: Subjects.PrimeFieldIsPrimeMatchesWitnessOracle)),

        // The exhaustive scale the two cases above only sample. The Baillie-PSW sweep visits every 32-bit value; it
        // runs in full because it was MEASURED at five to six minutes rather than assumed too expensive. Its oracle is
        // a segmented sieve of Eratosthenes written in the claims file — deliberately not a second Puck.Maths
        // primality kernel, which would let one shared defect green both sides.
        Case("prime-field.montgomery-chains-exhaustive", () => Laws.Claim(lawId: "prime-field.montgomery-chains-exhaustive", claim: PrimalityScaleClaims.MontgomeryChainsSurface)),
        Case("prime-field.baillie-psw-exhaustive", () => Laws.Claim(lawId: "prime-field.baillie-psw-exhaustive", claim: PrimalityScaleClaims.BailliePswSurface)),
        Case("prime-field.strong-round-vs-oracle", () => Laws.SweptClaim(lawId: "prime-field.strong-round-vs-oracle", domain: PrimeFieldPrimality, tier: Tier.Default, width: 1, claim: Subjects.PrimeFieldStrongRoundMatchesOracle)),
        Case("prime-field.lucas-vs-companion-matrix", () => Laws.SweptClaim(lawId: "prime-field.lucas-vs-companion-matrix", domain: PrimeFieldLucas, tier: Tier.Default, width: 1, claim: Subjects.PrimeFieldLucasMatchesCompanionMatrix)),
        Case("prime-field.baillie-composition", () => {
                Laws.SweptClaim(lawId: "prime-field.baillie-composition", domain: PrimeFieldPrimality, tier: Tier.Default, width: 1, claim: Subjects.PrimeFieldBaillieComposition);
                Laws.Claim(lawId: "prime-field.baillie-composition", claim: Subjects.PrimeFieldBaillieCarriage);
            }),
        Case("prime-field.pseudoprime-populations", () => Laws.Claim(lawId: "prime-field.pseudoprime-populations", claim: Subjects.PrimeFieldPseudoprimePopulations)),

        // ---- the quadratic extension field: F_p(sqrt(d)) as a pair over PrimeField64 ----
        Case("extension-field.ring-vs-oracle", () => Laws.SweptClaim(lawId: "extension-field.ring-vs-oracle", domain: ExtensionField, tier: Tier.Default, width: 2, claim: Subjects.ExtensionRingExact(full: false))),
        Case("extension-field.norm-trace-frobenius-vs-oracle", () => Laws.SweptClaim(lawId: "extension-field.norm-trace-frobenius-vs-oracle", domain: ExtensionFieldNorm, tier: Tier.Default, width: 2, claim: Subjects.ExtensionNormTraceFrobeniusExact(full: false))),
        Case("extension-field.inverse-vs-oracle", () => Laws.SweptClaim(lawId: "extension-field.inverse-vs-oracle", domain: ExtensionFieldInverse, tier: Tier.Default, width: 2, claim: Subjects.ExtensionInverseExact)),
        // Deliberately NOT citing ExtensionFrobenius, ExtensionNorm or ExtensionFromBase: legs 2 and 3 are ABOUT
        // conjugation and the norm, but they reach neither member. The claim body calls only Pow, Multiply, One, Zero
        // and the Element surface, and Pow is square-and-multiply over Multiply alone, so a wrong Frobenius, Norm or
        // FromBase cannot move this case — the campaign's probe broke all three at once and it stayed green. They
        // answer to extension-field.norm-trace-frobenius-vs-oracle, which reads them directly.
        Case("extension-field.pow-vs-oracle", () => Laws.SweptClaim(lawId: "extension-field.pow-vs-oracle", domain: ExtensionFieldPower, tier: Tier.Default, width: 2, claim: Subjects.ExtensionPowExact)),
        Case("extension-field.batch-inverse-vs-oracle", () => Laws.Claim(lawId: "extension-field.batch-inverse-vs-oracle", claim: Subjects.ExtensionBatchInverseExact)),
        // ExtensionMultiply and ExtensionOne are cited because the field-invariant sweep below closes each inverse
        // through them — Multiply(element, Inverse(element)) == One over every accepted generator at five primes — so
        // this case would fail if either were wrong. Neither was cited before that sweep existed.
        Case("extension-field.construction-and-refusals", () => Laws.Claim(lawId: "extension-field.construction-and-refusals", claim: Subjects.ExtensionConstructionAndRefusals)),
        Case("extension-field.product-vs-oracle", () => Laws.BinaryMatchesOracle(lawId: "extension-field.product-vs-oracle", domain: ExtensionFieldProduct, tier: Tier.Default, subject: Subjects.ExtensionProduct(entry: 2), oracle: Subjects.ExtensionProductOracle(entry: 2))),

        // This family's hot path: Inverse, LegendreCharacter, TrySqrt's descent and all four primality entry points
        // reach their arithmetic through Pow's Montgomery chain, so a smoke run that never touched it would report
        // confidence it does not have.
        Case("smoke.prime-field-pow-vs-modpow", () => Laws.SweptClaim(lawId: "smoke.prime-field-pow-vs-modpow", domain: SmokeDomain, tier: Tier.Smoke, width: 1, claim: Subjects.PrimeFieldPowMatchesModularPowerSmoke)),
        // The extension's hot path: Pow is a chain of the product, Inverse forms the norm out of it and BatchInverse is
        // a running product of it, so every other operation in the type bottoms out here.
        Case("smoke.extension-field-product-vs-oracle", () => Laws.BinaryMatchesOracle(lawId: "smoke.extension-field-product-vs-oracle", domain: SmokeDomain, tier: Tier.Smoke, subject: Subjects.ExtensionProduct(entry: 2), oracle: Subjects.ExtensionProductOracle(entry: 2))),

        // ---- sampling: the public refusal contracts of the wing's two constructing surfaces ----
        // The Sampling wing's expensive statistical evidence lives in Post's digital-net stage and in the Deep
        // distribution cases below. What the standard tier owes is the fast half: the contracts a caller reads off the
        // XML and acts on, which no statistical measurement is even shaped to state.
        Case("sampling.direction-number-refusal-ladder", () => Laws.Claim(lawId: "sampling.direction-number-refusal-ladder", claim: Subjects.DigitalNetDirectionNumberRefusals)),
        Case("sampling.alias-refusal-and-fixed-twins", () => Laws.Claim(lawId: "sampling.alias-refusal-and-fixed-twins", claim: Subjects.AliasTableRefusalsAndFixedTwins)),
        // The cone table is the wing's one same-machine-replay type, and its contract was written about the double
        // pair it discards rather than the float pair it stores. This case states the surviving property.
        Case("sampling.cone-table-stored-norm-and-uniqueness", () => Laws.Claim(lawId: "sampling.cone-table-stored-norm-and-uniqueness", claim: Subjects.ConeDirectionTableContract)),
        Case("sampling.pcg-reference-vector-and-state", () => Laws.Claim(lawId: "sampling.pcg-reference-vector-and-state", claim: Subjects.PcgReferenceVectorAndState)),
        Case("sampling.digital-net-identities-and-net-property", () => Laws.Claim(lawId: "sampling.digital-net-identities-and-net-property", claim: Subjects.DigitalNetSampleAndShuffleIdentities)),
        Case("sampling.field-noise-bounds-and-gradient", () => Laws.Claim(lawId: "sampling.field-noise-bounds-and-gradient", claim: Subjects.FieldNoiseBoundsAndTwins)),
        Case("sampling.normal-quantile-ladder", () => Laws.Claim(lawId: "sampling.normal-quantile-ladder", claim: Subjects.NormalQuantileLadderAndRefusals)),
        Case("sampling.low-discrepancy-recurrence", () => Laws.Claim(lawId: "sampling.low-discrepancy-recurrence", claim: Subjects.LowDiscrepancyRecurrence)),
        Case("sampling.secure-random-intervals", () => Laws.Claim(lawId: "sampling.secure-random-intervals", claim: Subjects.SecureRandomContracts)),

        // ---- Deep: exhaustive edge cross batteries ----
        Case("deep.presented-clifford-twin", () => Laws.VectorTwin(lawId: "deep.presented-clifford-twin", domain: Presented, tier: Tier.Deep, width: 8, first: Subjects.PresentedCliffordMultiply(positiveCount: 3, negativeCount: 0, degenerateCount: 0), second: Subjects.GeometricMultiply(positiveCount: 3, negativeCount: 0, degenerateCount: 0), witness: Subjects.CliffordProductOracle(positiveCount: 3, negativeCount: 0, degenerateCount: 0))),
        Case("deep.presented-complement-all-signatures", () => Laws.Claim(lawId: "deep.presented-complement-all-signatures", claim: OracleClaims.ComplementCliffordSignaturesDeep)),
        Case("deep.complex-mul-vs-oracle", () => Laws.BinaryMatchesOracle(lawId: "deep.complex-mul-vs-oracle", domain: Complex, tier: Tier.Deep, subject: Subjects.ComplexMultiply, oracle: Subjects.MultiplyOracle(pRaw: 0L, qRaw: ComplexQ))),
        Case("deep.split-mul-vs-oracle", () => Laws.BinaryMatchesOracle(lawId: "deep.split-mul-vs-oracle", domain: Split, tier: Tier.Deep, subject: Subjects.SplitMultiply, oracle: Subjects.MultiplyOracle(pRaw: 0L, qRaw: SplitQ))),
        Case("deep.quaternion-mul-vs-oracle", () => Laws.VectorMatchesOracle(lawId: "deep.quaternion-mul-vs-oracle", domain: Quaternion, tier: Tier.Deep, width: 4, subject: Subjects.QuaternionMultiplyLanes, oracle: Subjects.QuaternionMultiplyOracle)),
        Case("deep.dual-quaternion-mul-vs-oracle", () => Laws.VectorMatchesOracle(lawId: "deep.dual-quaternion-mul-vs-oracle", domain: DualQuaternion, tier: Tier.Deep, width: 8, subject: Subjects.DualQuaternionMultiplyLanes, oracle: Subjects.DualQuaternionMultiplyOracle)),
        Case("deep.complex-div-vs-oracle", () => Laws.BinaryMatchesOracle(lawId: "deep.complex-div-vs-oracle", domain: ComplexDivide, tier: Tier.Deep, subject: Subjects.ComplexDivide, oracle: Subjects.ComplexDivideOracle)),
        Case("deep.complex-rotate-vs-oracle", () => Laws.BinaryMatchesOracle(lawId: "deep.complex-rotate-vs-oracle", domain: ComplexRotate, tier: Tier.Deep, subject: Subjects.ComplexRotate, oracle: Subjects.MultiplyOracle(pRaw: 0L, qRaw: ComplexQ))),
        Case("deep.split-div-vs-oracle", () => Laws.BinaryMatchesOracle(lawId: "deep.split-div-vs-oracle", domain: SplitDivide, tier: Tier.Deep, subject: Subjects.SplitDivide, oracle: Subjects.SplitDivideOracle)),
        Case("deep.split-transform-vs-oracle", () => Laws.BinaryMatchesOracle(lawId: "deep.split-transform-vs-oracle", domain: SplitTransform, tier: Tier.Deep, subject: Subjects.SplitTransform, oracle: Subjects.MultiplyOracle(pRaw: 0L, qRaw: SplitQ))),
        Case("deep.fractional-mul-vs-oracle", () => Laws.BinaryMatchesOracle(lawId: "deep.fractional-mul-vs-oracle", domain: Fractional, tier: Tier.Deep, subject: Subjects.AlgebraMultiply(pRaw: 0L, qRaw: HalfQ), oracle: Subjects.MultiplyOracle(pRaw: 0L, qRaw: HalfQ))),
        // The last admissible tangle width: all 377 diagrams and all 142,129 ordered pairs against the arc-tracing
        // oracle, which is the width the 512-normal-form cap makes the boundary of the construction.
        Case("deep.presented-tangle-sweep", () => Laws.Claim(lawId: "deep.presented-tangle-sweep", claim: Subjects.TangleDeepSweep)),
        // The narrow width is FINITE, so at Deep the sampling comes off entirely: every raw the type can hold is rendered,
        // parsed back, projected and complemented, and multiplied and divided against a committed divisor band.
        Case("deep.unit-fraction16-exhaustive", () => Laws.Claim(lawId: "deep.unit-fraction16-exhaustive", claim: Subjects.UnitFraction16Exhaustive)),
        Case("deep.unit-fraction32-mul-vs-oracle", () => Laws.ScalarBinaryMatchesOracle(lawId: "deep.unit-fraction32-mul-vs-oracle", domain: UnitFraction32Domain, tier: Tier.Deep, subject: Subjects.UnitFraction32Multiply, oracle: Subjects.UnitFraction32MultiplyOracle)),
        // The one division kernel the family landed without a Deep mirror, and the mirror is stronger IN KIND rather
        // than only in sample count: the Default law's operand fold takes (min, max), so its quotient never exceeds one
        // and the Math.Min clamp fires at exactly one point — the edge square's diagonal, where the quotient is exactly
        // 2³² and nothing rounds. Everything above one rested on three rows of a hand ladder. This mirror drops the
        // ordering, so the ulong quotient grows toward 2⁶⁴ on live operands and the clamp becomes load-bearing.
        Case("deep.unit-fraction32-div-vs-oracle", () => Laws.ScalarBinaryMatchesOracle(lawId: "deep.unit-fraction32-div-vs-oracle", domain: UnitFraction32Domain, tier: Tier.Deep, subject: Subjects.UnitFraction32DivideUnordered, oracle: Subjects.UnitFraction32DivideUnorderedOracle)),
        Case("deep.unit-fraction32-text-vs-oracle", () => Laws.SweptClaim(lawId: "deep.unit-fraction32-text-vs-oracle", domain: UnitFraction32Domain, tier: Tier.Deep, width: 1, claim: Subjects.UnitFraction32TextMatchesOracle)),
        Case("deep.fixed-divide-vs-oracle", () => Laws.ScalarBinaryMatchesOracle(lawId: "deep.fixed-divide-vs-oracle", domain: ScalarDivision, tier: Tier.Deep, subject: Subjects.FixedDivide, oracle: Subjects.FixedDivideOracle)),
        Case("deep.fixed-transcendental-envelope", () => Laws.SweptClaim(lawId: "deep.fixed-transcendental-envelope", domain: ScalarTranscendental, tier: Tier.Deep, width: 1, claim: Subjects.FixedTranscendentalDeepSweep)),
        Case("deep.fixed-text-round-trip", () => Laws.SweptClaim(lawId: "deep.fixed-text-round-trip", domain: ScalarText, tier: Tier.Deep, width: 1, claim: Subjects.FixedTextRoundTrip)),
        Case("deep.unsigned-scalar-div-vs-oracle", () => Laws.ScalarBinaryMatchesOracle(lawId: "deep.unsigned-scalar-div-vs-oracle", domain: UnsignedScalar, tier: Tier.Deep, subject: Subjects.UnsignedFixedDivide, oracle: Subjects.UnsignedFixedDivideOracle)),
        // The sampling comes off entirely here: every branch of the five integer maps and the three integrality
        // classifiers is decided by the sixteen-bit fraction word and the parity of the integer part, so sweeping ALL
        // 2¹⁶ fraction words at seven integer parts is exhaustive over the branch space rather than merely wider.
        Case("deep.unsigned-scalar-fraction-sweep", () => Laws.Claim(lawId: "deep.unsigned-scalar-fraction-sweep", claim: Subjects.UnsignedFractionSweep)),
        Case("deep.vector-plane-products-vs-oracle", () => Laws.BinaryMatchesOracle(lawId: "deep.vector-plane-products-vs-oracle", domain: Vector, tier: Tier.Deep, subject: Subjects.PlaneProducts, oracle: Subjects.PlaneProductsOracle)),
        Case("deep.vector-narrow-lane-vs-oracle", () => Laws.BinaryMatchesOracle(lawId: "deep.vector-narrow-lane-vs-oracle", domain: VectorNarrow, tier: Tier.Deep, subject: Subjects.NarrowPlaneProducts, oracle: Subjects.NarrowPlaneProductsOracle)),
        Case("deep.vector-cross-vs-oracle", () => {
                Laws.VectorMatchesOracle(lawId: "deep.vector-cross-vs-oracle", domain: Vector, tier: Tier.Deep, width: 3, subject: Subjects.SpaceCrossLanes, oracle: Subjects.SpaceCrossLanesOracle);
                Laws.VectorMatchesOracle(lawId: "deep.vector-cross-vs-oracle", domain: Vector, tier: Tier.Deep, width: 3, subject: Subjects.SpaceDotLanes, oracle: Subjects.SpaceDotLanesOracle);
            }),
        Case("deep.vector-normalize-vs-ideal-and-staged", () => Laws.SweptClaim(lawId: "deep.vector-normalize-vs-ideal-and-staged", domain: VectorDirection, tier: Tier.Deep, width: 3, claim: Subjects.VectorNormalizeMatchesOracles)),
        Case("deep.rigid-compose-vs-oracle", () => Laws.VectorMatchesOracle(lawId: "deep.rigid-compose-vs-oracle", domain: Rigid, tier: Tier.Deep, width: 8, subject: Subjects.RigidComposeLanes, oracle: Subjects.RigidComposeOracle)),
        Case("deep.position-delta-vs-oracle", () => Laws.SweptClaim(lawId: "deep.position-delta-vs-oracle", domain: PositionDelta, tier: Tier.Deep, width: 6, claim: Subjects.PositionDeltaExact)),
        Case("deep.position-translate-vs-oracle", () => Laws.SweptClaim(lawId: "deep.position-translate-vs-oracle", domain: PositionTranslate, tier: Tier.Deep, width: 6, claim: Subjects.PositionTranslateExact)),
        Case("deep.rigid-transform-point-vs-oracle", () => Laws.SweptClaim(lawId: "deep.rigid-transform-point-vs-oracle", domain: RigidPoint, tier: Tier.Deep, width: 8, claim: Subjects.RigidTransformPointExact)),
        Case("deep.rate-schedule-vs-ledger", () => Laws.SweptClaim(lawId: "deep.rate-schedule-vs-ledger", domain: Rate, tier: Tier.Deep, width: 4, claim: Subjects.RateScheduleVsLedger)),
        // No Deep mirror stands for polynomial.additive-group-and-accessors or
        // polynomial.shifts-are-monomial-arithmetic, and the campaign says why rather than leaving the gap silent:
        // both are exact identities over operations linear in their operand (+, the two shifts) or over one accessor,
        // and every seam either carries is a FIXED LADDER — the identity constants, the shift-count ladder at 63/64,
        // the written forms — which a wider random batch does not touch. A mirror there would buy draw volume over
        // ground the Default battery already crosses, which the tier's own contract calls buying nothing.
        Case("deep.polynomial-multiply-vs-carryless-oracle", () => {
                Laws.ScalarBinaryMatchesOracle(lawId: "deep.polynomial-multiply-vs-carryless-oracle", domain: BinaryPolynomialRing, tier: Tier.Deep, subject: Subjects.BinaryPolynomialMultiply, oracle: Subjects.BinaryPolynomialMultiplyOracle);
                Laws.SweptClaim(lawId: "deep.polynomial-multiply-vs-carryless-oracle", domain: BinaryPolynomialRing, tier: Tier.Deep, width: 2, claim: Subjects.BinaryPolynomialCheckedMultiplyAndRingLaws);
            }),
        Case("deep.polynomial-divrem-vs-monomial-oracle", () => Laws.SweptClaim(lawId: "deep.polynomial-divrem-vs-monomial-oracle", domain: BinaryPolynomialDivision, tier: Tier.Deep, width: 2, claim: Subjects.BinaryPolynomialDivRemVsOracle)),
        Case("deep.polynomial-gcd-vs-binary-descent-oracle", () => Laws.SweptClaim(lawId: "deep.polynomial-gcd-vs-binary-descent-oracle", domain: BinaryPolynomialGcd, tier: Tier.Deep, width: 2, claim: Subjects.BinaryPolynomialGcdVsOracle)),
        Case("deep.polynomial-irreducible-census-and-trial-division", () => Laws.Claim(lawId: "deep.polynomial-irreducible-census-and-trial-division", claim: () => Subjects.BinaryPolynomialIrreducibility(censusDegree: 16, trialDegree: 12))),
        Case("deep.polynomial-primitive-order-and-census", () => {
                Laws.Claim(lawId: "deep.polynomial-primitive-order-and-census", claim: () => Subjects.BinaryPolynomialPrimitivity(censusDegree: 14));
                Laws.Claim(lawId: "deep.polynomial-primitive-order-and-census", claim: Subjects.BinaryPolynomialPrimitiveSearch);
            }),
        Case("deep.polynomial-factor-odd-cycle-vs-cyclotomic-cosets", () => Laws.Claim(lawId: "deep.polynomial-factor-odd-cycle-vs-cyclotomic-cosets", claim: Subjects.BinaryPolynomialFactorOddCycleExhaustive)),
        Case("deep.binary-field-multiplicative-group", () => {
                Laws.SweptClaim(lawId: "deep.binary-field-multiplicative-group", domain: BinaryFieldGroup, tier: Tier.Deep, width: 3, claim: (left, right) => Subjects.BinaryFieldGroupExact(left: left, right: right, everyDegree: true));
                Laws.Claim(lawId: "deep.binary-field-multiplicative-group", claim: Subjects.BinaryFieldGroupRefusals);
            }),
        Case("deep.binary-field-irreducible-census", () => Laws.Claim(lawId: "deep.binary-field-irreducible-census", claim: () => Subjects.BinaryFieldIrreducibility(censusDegree: 16, trialDegree: 12))),
        Case("deep.binary-field-degree8-exhaustive", () => Laws.Claim(lawId: "deep.binary-field-degree8-exhaustive", claim: Subjects.BinaryFieldDegree8Exhaustive)),
        // prime-field.create-and-refusals has NO Deep mirror by design: its statement is a fixed refusal ladder rather
        // than a sweep, and it already runs the full fifteen-rung modulus ladder at Default. A "stronger" version
        // would only be a longer list of the same shape.
        Case("deep.prime-field-arithmetic-vs-oracle", () => Laws.SweptClaim(lawId: "deep.prime-field-arithmetic-vs-oracle", domain: PrimeFieldBand, tier: Tier.Deep, width: 1, claim: Subjects.PrimeFieldArithmeticExactDeep)),
        Case("deep.prime-field-pow-vs-modpow", () => Laws.SweptClaim(lawId: "deep.prime-field-pow-vs-modpow", domain: PrimeFieldChain, tier: Tier.Deep, width: 1, claim: Subjects.PrimeFieldPowMatchesModularPowerDeep)),
        Case("deep.prime-field-inverse-and-batch", () => Laws.Claim(lawId: "deep.prime-field-inverse-and-batch", claim: Subjects.PrimeFieldInverseAndBatchDeep)),
        // TWO statements in one Deep case, each leg naming the id it mirrors: the character and the square root sweep
        // the same operand stream at Default through their shared prime-field-root key, and mirroring them apart would
        // break that sharing.
        Case("deep.prime-field-root-and-character", () => {
                Laws.SweptClaim(lawId: "deep.prime-field-root-and-character", domain: PrimeFieldRoot, tier: Tier.Deep, width: 1, claim: Subjects.PrimeFieldLegendreMatchesReciprocityDeep);
                Laws.SweptClaim(lawId: "deep.prime-field-root-and-character", domain: PrimeFieldRoot, tier: Tier.Deep, width: 1, claim: Subjects.PrimeFieldSquareRootExactDeep);
            }),
        Case("deep.prime-field-is-prime-exact", () => {
                Laws.Claim(lawId: "deep.prime-field-is-prime-exact", claim: Subjects.PrimeFieldIsPrimeAgainstSieveAndWitnessesDeep);
                Laws.SweptClaim(lawId: "deep.prime-field-is-prime-exact", domain: PrimeFieldPrimality, tier: Tier.Deep, width: 1, claim: Subjects.PrimeFieldIsPrimeMatchesWitnessOracle);
            }),
        Case("deep.prime-field-strong-round-vs-oracle", () => Laws.SweptClaim(lawId: "deep.prime-field-strong-round-vs-oracle", domain: PrimeFieldPrimality, tier: Tier.Deep, width: 1, claim: Subjects.PrimeFieldStrongRoundMatchesOracle)),
        Case("deep.prime-field-lucas-vs-companion-matrix", () => Laws.SweptClaim(lawId: "deep.prime-field-lucas-vs-companion-matrix", domain: PrimeFieldLucas, tier: Tier.Deep, width: 1, claim: Subjects.PrimeFieldLucasMatchesCompanionMatrixDeep)),
        Case("deep.prime-field-baillie-and-populations", () => {
                Laws.SweptClaim(lawId: "deep.prime-field-baillie-and-populations", domain: PrimeFieldPrimality, tier: Tier.Deep, width: 1, claim: Subjects.PrimeFieldBaillieComposition);
                Laws.Claim(lawId: "deep.prime-field-baillie-and-populations", claim: Subjects.PrimeFieldBaillieCarriage);
                Laws.Claim(lawId: "deep.prime-field-baillie-and-populations", claim: Subjects.PrimeFieldPseudoprimePopulationsDeep);
            }),
        Case("deep.extension-field-product-vs-oracle", () => Laws.BinaryMatchesOracle(lawId: "deep.extension-field-product-vs-oracle", domain: ExtensionFieldProduct, tier: Tier.Deep, subject: Subjects.ExtensionProduct(entry: 2), oracle: Subjects.ExtensionProductOracle(entry: 2))),
        // TWO statements in one Deep case, each leg naming the id it mirrors: the ring statement and the norm/trace
        // statement are separate keys at Default and stay separate here, so each pair advances one counter together.
        Case("deep.extension-field-ring-and-norm", () => {
                Laws.SweptClaim(lawId: "deep.extension-field-ring-and-norm", domain: ExtensionField, tier: Tier.Deep, width: 2, claim: Subjects.ExtensionRingExact(full: true));
                Laws.SweptClaim(lawId: "deep.extension-field-ring-and-norm", domain: ExtensionFieldNorm, tier: Tier.Deep, width: 2, claim: Subjects.ExtensionNormTraceFrobeniusExact(full: true));
            }),
        // ---- The statistical, lattice and presented-kernel statements, grouped by the surface each interrogates ----
        // ---- Pcg32XshRr vs the reference implementation ----
        Case("sampling.pcg-transcribed-reference-and-decorrelation", () => Laws.Claim(lawId: "sampling.pcg-transcribed-reference-and-decorrelation", claim: SamplingClaims.PcgTranscribedReferenceAndDecorrelationSurface)),
        // ---- log2 / gaussian / alias table ----
        Case("sampling.gaussian-moments-cdf-tail", () => Laws.Claim(lawId: "sampling.gaussian-moments-cdf-tail", claim: SamplingClaims.GaussianMomentsCdfTailSurface)),
        Case("sampling.shuffle-permutation-uniformity", () => Laws.Claim(lawId: "sampling.shuffle-permutation-uniformity", claim: SamplingClaims.ShuffleUniformitySurface)),
        Case("sampling.alias-table-frequency-distribution", () => Laws.Claim(lawId: "sampling.alias-table-frequency-distribution", claim: SamplingClaims.AliasTableFrequencyDistributionSurface)),

        // The full-volume counterparts of the three above, plus the measured star discrepancy nothing else in the tree
        // carries. Each gates at the TIGHTER of its published threshold and eight standard errors derived from the
        // sample count in the same run, so the volume buys real tightening rather than a restated bound. They sit at
        // Deep, not Exhaustive, because the four together measure
        // 2.5 seconds: a cheap case parked behind an opt-in tier loses its everyday coverage, and it would be the
        // WEAKER reduced-volume sibling left guarding Deep. Each shares its sibling's seed and multiplies its draw
        // count, so the sibling's samples are a prefix of these under strictly looser thresholds.
        Case("sampling.gaussian-moments-cdf-tail-at-scale", () => Laws.Claim(lawId: "sampling.gaussian-moments-cdf-tail-at-scale", claim: SamplingDistributionClaims.GaussianMomentsCdfTailAtScaleSurface)),
        Case("sampling.shuffle-permutation-uniformity-at-scale", () => Laws.Claim(lawId: "sampling.shuffle-permutation-uniformity-at-scale", claim: SamplingDistributionClaims.ShuffleUniformityAtScaleSurface)),
        Case("sampling.alias-table-frequency-at-scale", () => Laws.Claim(lawId: "sampling.alias-table-frequency-at-scale", claim: SamplingDistributionClaims.AliasTableFrequencyAtScaleSurface)),
        Case("sampling.certified-low-discrepancy-measured-across-scales", () => Laws.Claim(lawId: "sampling.certified-low-discrepancy-measured-across-scales", claim: SamplingDistributionClaims.CertifiedLowDiscrepancyMeasuredAcrossScalesSurface)),
        // ---- field noise + low discrepancy ----
        Case("sampling.field-noise-periodicity-canary-and-distribution", () => Laws.Claim(lawId: "sampling.field-noise-periodicity-canary-and-distribution", claim: SamplingClaims.FieldNoisePeriodicityAndDistributionSurface)),
        // ---- CertifiedLowDiscrepancy ----
        Case("sampling.certified-low-discrepancy-bound-and-teeth", () => Laws.Claim(lawId: "sampling.certified-low-discrepancy-bound-and-teeth", claim: SamplingClaims.CertifiedLowDiscrepancyBoundTeethAndGapSurface)),
        // ---- SymmetryLattice (the exact E8 root system CyclicRotation is the heartbeat of) ----
        // Deep rather than Exhaustive: 240 x 240 reflection pairs is milliseconds, not a full-carrier sweep. Exhaustive
        // is opt-in and rarely run, so parking a cheap case there would cost the statement its everyday coverage
        // without buying breadth.
        Case("integer.symmetry-lattice-exact-structure", () => Laws.Claim(lawId: "integer.symmetry-lattice-exact-structure", claim: LatticeClaims.SymmetryLatticeExactStructureSurface)),
        // ---- HilbertCurve (locality-preserving space-filling curve) ----
        // Deep rather than Exhaustive for the same reason: orders one through nine is a sub-second sweep.
        Case("integer.hilbert-curve-bijection-and-locality", () => Laws.Claim(lawId: "integer.hilbert-curve-bijection-and-locality", claim: LatticeClaims.HilbertCurveExhaustiveBijectionSurface)),

        // The case above proves the curve is a bijection ON its domain; these two say what happens off it and who owns
        // the lattice's shared metadata. Both were previously unstated, and both failed silently rather than loudly.
        Case("integer.hilbert-curve-refuses-outside-its-domain", () => Laws.Claim(lawId: "integer.hilbert-curve-refuses-outside-its-domain", claim: LatticeClaims.HilbertCurveRefusesOutsideItsDomain)),
        Case("integer.ray-cycle-factors-are-not-writable-by-consumers", () => Laws.Claim(lawId: "integer.ray-cycle-factors-are-not-writable-by-consumers", claim: LatticeClaims.RayCycleFactorsAreNotWritableByConsumers)),
        Case("integer.hilbert-curve-high-order-round-trip", () => Laws.Claim(lawId: "integer.hilbert-curve-high-order-round-trip", claim: LatticeClaims.HilbertCurveHighOrderRoundTripSurface)),
        // ---- HexagonalCoordinate (exact Eisenstein-integer hex grid) ----
        Case("vector.hexagonal-coordinate-ring-and-rotation", () => Laws.Claim(lawId: "vector.hexagonal-coordinate-ring-and-rotation", claim: LatticeClaims.HexagonalCoordinateAlgebraicStructureSurface)),
        Case("vector.hexagonal-coordinate-length-matches-graph-distance", () => Laws.Claim(lawId: "vector.hexagonal-coordinate-length-matches-graph-distance", claim: LatticeClaims.HexagonalCoordinateLengthMatchesGraphDistanceSurface)),
        Case("vector.hexagonal-coordinate-round-is-nearest-cell", () => Laws.Claim(lawId: "vector.hexagonal-coordinate-round-is-nearest-cell", claim: LatticeClaims.HexagonalCoordinateRoundIsNearestCellSurface)),
        // ---- scalar specification oracles ----
        Case("scalar.jacobi-symbol-fixed-width-vs-exact-descent", () => Laws.Claim(lawId: "scalar.jacobi-symbol-fixed-width-vs-exact-descent", claim: ScalarFieldClaims.JacobiSymbolFixedWidthVsExactDescentSurface)),
        Case("scalar.binary-integer-wide-carrier-vs-oracle", () => Laws.Claim(lawId: "scalar.binary-integer-wide-carrier-vs-oracle", claim: ScalarFieldClaims.BinaryIntegerWideCarrierSurface)),

        // The two conversion seams where the cross-machine promise was resting on the host rather than on code: a NaN
        // whose integer conversion the CLI does not specify, and a rendering that read the ambient culture. Both are
        // contract statements, so the suite now enforces the boundary the README draws instead of describing it.
        Case("scalar.conversion-seams-do-not-depend-on-the-host", () => Laws.Claim(lawId: "scalar.conversion-seams-do-not-depend-on-the-host", claim: ScalarFieldClaims.ConversionSeamsDoNotDependOnTheHost)),
        // ---- binary field: the suite's only CRC statement ----
        Case("algebra.binary-polynomial-crc32-published-vector", () => Laws.Claim(lawId: "algebra.binary-polynomial-crc32-published-vector", claim: ScalarFieldClaims.BinaryPolynomialCrc32PublishedVectorSurface)),
        // ---- MetallicQuasicrystal random access ----
        Case("quasicrystal.metallic-random-access-vs-streamed-word", () => Laws.Claim(lawId: "quasicrystal.metallic-random-access-vs-streamed-word", claim: QuasicrystalClaims.MetallicRandomAccessMatchesStreamedWord)),
        // ---- ModularTransform + ContinuedFraction ----
        Case("quasicrystal.modular-transform-classes-and-cusp-action", () => Laws.Claim(lawId: "quasicrystal.modular-transform-classes-and-cusp-action", claim: QuasicrystalClaims.ModularTransformClassesAndCuspAction)),
        Case("quasicrystal.gauss-reduction-into-fundamental-domain", () => Laws.Claim(lawId: "quasicrystal.gauss-reduction-into-fundamental-domain", claim: QuasicrystalClaims.GaussReductionEntersFundamentalDomain)),

        // The DECISION rather than the reduction, at the signed carrier's corners. The case above sweeps operands at or
        // below 24, where no width matters; the definiteness test needs 129 signed bits at the extremes and so both
        // admitted an indefinite form and refused a positive-definite one. Its oracle forms the discriminant in
        // BigInteger, sharing nothing with a production check that now compares unsigned magnitudes and never forms the
        // difference at all — which is the point, since the old oracle recomputed the subject's own expression.
        Case("quasicrystal.gauss-reduction-definiteness-across-the-carrier", () => Laws.Claim(lawId: "quasicrystal.gauss-reduction-definiteness-across-the-carrier", claim: QuasicrystalClaims.GaussReductionDefinitenessAcrossTheCarrier)),
        Case("quasicrystal.continued-fraction-periods-and-full-width", () => Laws.Claim(lawId: "quasicrystal.continued-fraction-periods-and-full-width", claim: QuasicrystalClaims.ContinuedFractionPeriodsAndFullWidthRegressions)),
        // ---- QuadraticInflation + MetallicQuasicrystal ----
        Case("quasicrystal.inflation-invariants-and-polynomial-tails", () => Laws.Claim(lawId: "quasicrystal.inflation-invariants-and-polynomial-tails", claim: QuasicrystalClaims.QuadraticInflationInvariantsAndPolynomialTails)),
        Case("quasicrystal.metallic-reproduces-golden-silver-fixed-point", () => Laws.Claim(lawId: "quasicrystal.metallic-reproduces-golden-silver-fixed-point", claim: QuasicrystalClaims.MetallicReproducesGoldenSilverAndIsAFixedPoint)),
        // ---- QuadraticQuasicrystal (the general chain) ----
        Case("quasicrystal.general-chain-is-sturmian-and-tile-length-consistent", () => Laws.Claim(lawId: "quasicrystal.general-chain-is-sturmian-and-tile-length-consistent", claim: QuasicrystalClaims.GeneralQuasicrystalIsSturmianAndTileLengthConsistent)),
        Case("quasicrystal.width-and-period-regressions", () => Laws.Claim(lawId: "quasicrystal.width-and-period-regressions", claim: QuasicrystalClaims.QuadraticQuasicrystalWidthAndPeriodRegressions)),
        // ---- QuadraticQuasicrystal.Chain random access ----
        Case("quasicrystal.chain-single-term-matches-metallic-and-new-periods", () => Laws.Claim(lawId: "quasicrystal.chain-single-term-matches-metallic-and-new-periods", claim: QuasicrystalClaims.ChainSingleTermMatchesMetallicAndNewPeriodsWalk)),
        // ---- quaternion / dual ----
        // The three ladder cases below declare Leg.Structural rather than Leg.PublishedConstant. Their literals were
        // computed offline in double from the closed form, then cross-checked against the shipped kernel to confirm
        // the tolerance before being copied in — so the band was set by observing the subject. That is a regression
        // pin, not classical evidence, and condition (C) forbids calling it independent.
        Case("quaternion.from-axis-angle-ladder-transcription", () => Laws.Claim(lawId: "quaternion.from-axis-angle-ladder-transcription", claim: GeometryClaims.QuaternionFromAxisAngleLadderSurface)),
        Case("quaternion.exp-log-ladder-transcription", () => Laws.Claim(lawId: "quaternion.exp-log-ladder-transcription", claim: GeometryClaims.QuaternionExpLogSurface)),
        Case("quaternion.slerp-ladder-transcription", () => Laws.Claim(lawId: "quaternion.slerp-ladder-transcription", claim: GeometryClaims.QuaternionSlerpSurface)),
        Case("quaternion.algebraic-sanity-and-fromto-poles", () => Laws.Claim(lawId: "quaternion.algebraic-sanity-and-fromto-poles", claim: GeometryClaims.QuaternionAlgebraicSanitySurface)),
        Case("quaternion.from-to-full-width-alignment", () => Laws.Claim(lawId: "quaternion.from-to-full-width-alignment", claim: GeometryClaims.QuaternionFromToAlignmentSurface)),
        Case("dual.chain-rule-ladder-and-exact-spot-checks", () => Laws.Claim(lawId: "dual.chain-rule-ladder-and-exact-spot-checks", claim: GeometryClaims.DualDerivativeSurface)),
        // ---- vector2 wedge/dot ----
        Case("quaternion.hamilton-product-dot-inverse-full-width", () => Laws.Claim(lawId: "quaternion.hamilton-product-dot-inverse-full-width", claim: GeometryClaims.QuaternionHamiltonProductDotInverseSurface)),
        Case("quaternion.rotate-schedule-transcription-full-width", () => Laws.Claim(lawId: "quaternion.rotate-schedule-transcription-full-width", claim: GeometryClaims.QuaternionRotateScheduleTranscriptionSurface)),
        Case("vector.plane-full-width-oracle-and-identities", () => Laws.Claim(lawId: "vector.plane-full-width-oracle-and-identities", claim: GeometryClaims.Vector2FullWidthOracleAndIdentitiesSurface)),
        Case("vector.space-full-width-oracle-and-length-policy", () => Laws.Claim(lawId: "vector.space-full-width-oracle-and-length-policy", claim: GeometryClaims.Vector3DotCrossOracleSurface)),
        // ---- complex / rigid transform ----
        Case("complex.division-multiply-full-width-oracle", () => Laws.Claim(lawId: "complex.division-multiply-full-width-oracle", claim: GeometryClaims.ComplexDivisionMultiplyFullWidthOracleSurface)),
        Case("complex.from-to-full-width-alignment-and-scale-safety", () => Laws.Claim(lawId: "complex.from-to-full-width-alignment-and-scale-safety", claim: GeometryClaims.ComplexFromToAndScaleSafetySurface)),
        Case("quaternion.normalize-full-width-oracle-and-four-square-carry", () => Laws.Claim(lawId: "quaternion.normalize-full-width-oracle-and-four-square-carry", claim: GeometryClaims.NormalizeFullWidthOracleSurface)),
        Case("rigid.round-trip-ladder-self-consistency", () => Laws.Claim(lawId: "rigid.round-trip-ladder-self-consistency", claim: GeometryClaims.RigidTransformRoundTripSurface)),
        // ---- the presented algebra: kernels, modules, diagrams and the zeta seam ----
        Case("presented.clifford-signature-ladder-vs-geometric", () => Laws.Claim(lawId: "presented.clifford-signature-ladder-vs-geometric", claim: PresentedKernelClaims.CliffordSignatureLadderMatchesGeometricAlgebra)),
        Case("presented.octonion-twist-cocycle-count", () => Laws.Claim(lawId: "presented.octonion-twist-cocycle-count", claim: PresentedKernelClaims.OctonionTwistCocycleCountMatchesDoublingAssociatorSupport)),
        Case("presented.binary-field-wide-degrees-twin", () => Laws.Claim(lawId: "presented.binary-field-wide-degrees-twin", claim: PresentedKernelClaims.WideBinaryFieldTwinsShippedKernel)),
        Case("presented.sedenion-pair-zero-divisor-count", () => Laws.Claim(lawId: "presented.sedenion-pair-zero-divisor-count", claim: PresentedKernelClaims.SedenionPairSumZeroDivisorCount)),
        Case("presented.path-algebra-argument-validation", () => Laws.Claim(lawId: "presented.path-algebra-argument-validation", claim: PresentedKernelClaims.PathAlgebraArgumentValidationRefusesByShape)),
        Case("presented.live-associator-vs-doubling-tower", () => Laws.Claim(lawId: "presented.live-associator-vs-doubling-tower", claim: PresentedModuleClaims.LiveAssociatorMatchesDoublingTower)),
        Case("presented.sedenion-quadruple-bracketing-exhaustive", () => Laws.Claim(lawId: "presented.sedenion-quadruple-bracketing-exhaustive", claim: PresentedModuleClaims.SedenionQuadrupleBracketingsExhaustive)),
        Case("presented.braiding-self-consistent-eight-instances", () => Laws.Claim(lawId: "presented.braiding-self-consistent-eight-instances", claim: PresentedDiagramClaims.BraidingCertificateSelfConsistentAtEightInstances)),
        Case("presented.quantum-torus-vs-skew-pairing", () => Laws.Claim(lawId: "presented.quantum-torus-vs-skew-pairing", claim: PresentedDiagramClaims.QuantumTorusChargeMatchesSkewPairing)),
        Case("presented.functor-twins-transfer-varied-length", () => Laws.Claim(lawId: "presented.functor-twins-transfer-varied-length", claim: PresentedDiagramClaims.FunctorTwinsTransferAtVariedLength)),
        Case("presented.unit-interval-power-of-two-envelope-boundary", () => Laws.Claim(lawId: "presented.unit-interval-power-of-two-envelope-boundary", claim: PresentedZetaClaims.UnitIntervalPowerOfTwoEnvelopeBoundary)),
        Case("presented.unit-interval-fused-competing-terms-vs-oracle", () => Laws.Claim(lawId: "presented.unit-interval-fused-competing-terms-vs-oracle", claim: PresentedZetaClaims.UnitIntervalFusedCompetingTermsVsOracle)),
        // ---- the partitioner, digital-net, binary-field and fixed-point stage sweeps ----
        Case("core.monotonic-partitioner-full-domain-sweep", () => Laws.Claim(lawId: "core.monotonic-partitioner-full-domain-sweep", claim: MonotonicPartitionerClaims.RoutingIsDeterministicMonotonicAndUniformSurface)),
        Case("core.monotonic-partitioner-metrics-vs-reference-walk", () => Laws.Claim(lawId: "core.monotonic-partitioner-metrics-vs-reference-walk", claim: MonotonicPartitionerClaims.MetricsMatchReferenceChainWalkSurface)),
        Case("core.monotonic-partitioner-guid-protocol-pin", () => Laws.Claim(lawId: "core.monotonic-partitioner-guid-protocol-pin", claim: MonotonicPartitionerClaims.GuidRoutesThroughTrailingEntropyProtocolSurface)),
        Case("core.monotonic-partitioner-bucket-count-refusals", () => Laws.Claim(lawId: "core.monotonic-partitioner-bucket-count-refusals", claim: MonotonicPartitionerClaims.BucketCountOutOfRangeRefusesSurface)),
        Case("core.binary-integer-signed-extremes-and-refusals", () => Laws.Claim(lawId: "core.binary-integer-signed-extremes-and-refusals", claim: WorldCoordClaims.BinaryIntegerSignedExtremesAndRefusalsSurface)),
        Case("sampling.digital-net-property-through-order-fourteen", () => Laws.Claim(lawId: "sampling.digital-net-property-through-order-fourteen", claim: DigitalNetClaims.NetPropertyThroughOrderFourteenSurface)),
        Case("sampling.digital-net-shifted-and-shuffled-blocks-are-nets", () => Laws.Claim(lawId: "sampling.digital-net-shifted-and-shuffled-blocks-are-nets", claim: DigitalNetClaims.ShiftedAndShuffledBlocksAreNetsSurface)),
        Case("sampling.digital-net-radical-inverse-full-range", () => Laws.Claim(lawId: "sampling.digital-net-radical-inverse-full-range", claim: DigitalNetClaims.RadicalInverseFullRangeSurface)),
        Case("sampling.cone-table-build-purity-and-quantized-coverage", () => Laws.Claim(lawId: "sampling.cone-table-build-purity-and-quantized-coverage", claim: DigitalNetClaims.ConeTableBuildPurityAndQuantizedCoverageSurface)),
        Case("binary-field.narrow-degree-inverse-vs-oracle", () => Laws.Claim(lawId: "binary-field.narrow-degree-inverse-vs-oracle", claim: BinaryFieldRegionClaims.NarrowDegreeInverseSurface)),
        Case("binary-field.narrow-degree-regions-vs-oracle", () => Laws.Claim(lawId: "binary-field.narrow-degree-regions-vs-oracle", claim: BinaryFieldRegionClaims.NarrowDegreeRegionsSurface)),
        Case("binary-field.region-tiers-vs-scalar-rung", () => Laws.Claim(lawId: "binary-field.region-tiers-vs-scalar-rung", claim: BinaryFieldRegionClaims.RegionTiersVsScalarRungSurface)),
        Case("binary-field.wide-region-tiers-vs-scalar-rung", () => Laws.Claim(lawId: "binary-field.wide-region-tiers-vs-scalar-rung", claim: BinaryFieldRegionClaims.WideRegionTiersVsScalarRungSurface)),
        Case("binary-field.region-lengths-vs-scalar-rung", () => Laws.Claim(lawId: "binary-field.region-lengths-vs-scalar-rung", claim: BinaryFieldRegionClaims.RegionLengthsVsScalarRungSurface)),
        Case("reed-solomon.generator-roots-vs-oracle", () => Laws.Claim(lawId: "reed-solomon.generator-roots-vs-oracle", claim: ReedSolomonClaims.GeneratorRootsSurface)),
        Case("reed-solomon.published-remainder", () => Laws.Claim(lawId: "reed-solomon.published-remainder", claim: ReedSolomonClaims.PublishedRemainderSurface)),
        Case("reed-solomon.codeword-syndromes-vanish", () => Laws.Claim(lawId: "reed-solomon.codeword-syndromes-vanish", claim: ReedSolomonClaims.CodewordSyndromesSurface)),
        Case("reed-solomon.refusals-and-wide-carrier", () => Laws.Claim(lawId: "reed-solomon.refusals-and-wide-carrier", claim: ReedSolomonClaims.SurfaceRefusalsAndWideCarrierSurface)),
        Case("vector.move-toward-boundaries-and-segment", () => Laws.Claim(lawId: "vector.move-toward-boundaries-and-segment", claim: MoveTowardAndEmitterClaims.MoveTowardSurface)),
        Case("core.rust-port-emitters-are-pure-and-live", () => Laws.Claim(lawId: "core.rust-port-emitters-are-pure-and-live", claim: MoveTowardAndEmitterClaims.RustPortEmitterSurface)),
        Case("core.layer-sequence-walker-and-bounded-horizon", () => Laws.Claim(lawId: "core.layer-sequence-walker-and-bounded-horizon", claim: FixedPointContractClaims.LayerSequenceWalkerAndBoundedHorizonSurface)),
        Case("core.bitwise-pair-signed-narrow-and-wide-carriers", () => Laws.Claim(lawId: "core.bitwise-pair-signed-narrow-and-wide-carriers", claim: FixedPointContractClaims.BitwisePairSignedNarrowAndWideCarriersSurface)),
        Case("sampling.field-noise-wide-position-alias-and-rebase", () => Laws.Claim(lawId: "sampling.field-noise-wide-position-alias-and-rebase", claim: FixedPointContractClaims.FieldNoiseWidePositionAliasAndRebaseSurface)),
        Case("core.unsigned-square-root-uint128-carrier-boundary", () => Laws.Claim(lawId: "core.unsigned-square-root-uint128-carrier-boundary", claim: FixedPointContractClaims.UnsignedSquareRootUInt128CarrierBoundarySurface)),
        Case("core.fixed-tick-conversion-rounds-up-against-rational-arithmetic", () => Laws.Claim(lawId: "core.fixed-tick-conversion-rounds-up-against-rational-arithmetic", claim: FixedPointContractClaims.FixedTickConversionRoundsUpAgainstRationalArithmetic)),
        Case("core.fixed-tick-conversion-exact-refuses-inexact-decimals", () => Laws.Claim(lawId: "core.fixed-tick-conversion-exact-refuses-inexact-decimals", claim: FixedPointContractClaims.TryDurationEngineTicksExactAgainstDecimalBits)),
        Case("scalar.cyclic-rotation-plane-count-matches-coxeter-conjugacy", () => Laws.Claim(lawId: "scalar.cyclic-rotation-plane-count-matches-coxeter-conjugacy", claim: Subjects.CyclicRotationPlaneCountIsCoxeterConjugacyPairCount)),
        Case("sampling.field-noise-sample-vs-exact-oracle", () => Laws.Claim(lawId: "sampling.field-noise-sample-vs-exact-oracle", claim: FieldNoiseOracleClaims.FieldNoiseSampleMatchesExactOracle)),
        // ---- the presented structure surface and the quadratic-integer wing ----
        Case("presented.clifford-conformal-cells-vs-oracle", () => Laws.Claim(lawId: "presented.clifford-conformal-cells-vs-oracle", claim: PresentedStructureClaims.ConformalCliffordCellsSurface)),
        Case("presented.sedenion-basis-vs-doubling-tower", () => Laws.Claim(lawId: "presented.sedenion-basis-vs-doubling-tower", claim: PresentedStructureClaims.SedenionBasisVsDoublingTowerSurface)),
        Case("presented.quiver-counting-star-vs-walk-oracle", () => Laws.Claim(lawId: "presented.quiver-counting-star-vs-walk-oracle", claim: PresentedStructureClaims.QuiverCountingStarVsWalkOracleSurface)),
        Case("presented.divisibility-cubed-divisor-count", () => Laws.Claim(lawId: "presented.divisibility-cubed-divisor-count", claim: PresentedStructureClaims.DirichletDivisorCubeSurface)),
        Case("presented.duality-weighted-equivalence-vs-enumeration", () => Laws.Claim(lawId: "presented.duality-weighted-equivalence-vs-enumeration", claim: PresentedStructureClaims.WeightedDualityEquivalenceSurface)),
        Case("presented.complement-wedge-and-incidence-beyond-euclidean", () => Laws.Claim(lawId: "presented.complement-wedge-and-incidence-beyond-euclidean", claim: PresentedStructureClaims.NonMetricComplementBeyondEuclideanSurface)),
        Case("presented.transfer-functor-vs-legacy-copies", () => Laws.Claim(lawId: "presented.transfer-functor-vs-legacy-copies", claim: PresentedStructureClaims.TransferFunctorLegacyCopiesSurface)),
        Case("presented.motor-sandwich-vs-geometric-algebra", () => Laws.Claim(lawId: "presented.motor-sandwich-vs-geometric-algebra", claim: PresentedStructureClaims.MotorSandwichVsGeometricAlgebraSurface)),
        Case("presented.shuffle-near-cap-basis", () => Laws.Claim(lawId: "presented.shuffle-near-cap-basis", claim: PresentedStructureClaims.ShuffleNearCapBasisSurface)),
        Case("presented.homology-torus-free-rank-two", () => Laws.Claim(lawId: "presented.homology-torus-free-rank-two", claim: PresentedStructureClaims.HomologyTorusFreeRankTwoSurface)),
        Case("quadratic-integer.class-number-one-worlds-factor-prime-canonical", () => Laws.Claim(lawId: "quadratic-integer.class-number-one-worlds-factor-prime-canonical", claim: QuadraticIntegerClaims.ClassNumberOneWorldsFactorSurface)),
        Case("quadratic-integer.golden-unit-and-splitting-vs-jacobi", () => Laws.Claim(lawId: "quadratic-integer.golden-unit-and-splitting-vs-jacobi", claim: QuadraticIntegerClaims.GoldenUnitAndSplittingSurface)),
        Case("quadratic-integer.sum-of-two-squares-and-class-group-witness", () => Laws.Claim(lawId: "quadratic-integer.sum-of-two-squares-and-class-group-witness", claim: QuadraticIntegerClaims.SumOfTwoSquaresAndWitnessSurface)),
        Case("quadratic-integer.factorization-is-deterministic", () => Laws.Claim(lawId: "quadratic-integer.factorization-is-deterministic", claim: QuadraticIntegerClaims.FactorizationDeterminismSurface)),
        Case("quadratic-integer.fast-tier-routing-vs-independent-reference", () => Laws.Claim(lawId: "quadratic-integer.fast-tier-routing-vs-independent-reference", claim: QuadraticIntegerClaims.FastTierRoutingSurface)),
        Case("quadratic-integer.real-order-fundamental-unit-vs-retired-scan", () => Laws.Claim(lawId: "quadratic-integer.real-order-fundamental-unit-vs-retired-scan", claim: QuadraticIntegerClaims.RealOrderFundamentalUnitVsRetiredScanSurface)),
        Case("quadratic-integer.landmine-and-descriptor-invariance", () => Laws.Claim(lawId: "quadratic-integer.landmine-and-descriptor-invariance", claim: QuadraticIntegerClaims.LandmineAndDescriptorInvarianceSurface)),
        Case("quadratic-integer.pell-delegation-vs-retired-convergent-loop", () => Laws.Claim(lawId: "quadratic-integer.pell-delegation-vs-retired-convergent-loop", claim: QuadraticIntegerClaims.PellDelegationVsRetiredConvergentLoopSurface)),
        Case("quadratic-integer.audit-hang-completes-forced-sign", () => Laws.Claim(lawId: "quadratic-integer.audit-hang-completes-forced-sign", claim: QuadraticIntegerClaims.AuditHangCompletesForcedSignSurface)),
        Case("quadratic-integer.real-order-prime-norm-existence-vs-retired-orbit-box", () => Laws.Claim(lawId: "quadratic-integer.real-order-prime-norm-existence-vs-retired-orbit-box", claim: QuadraticIntegerClaims.RealOrderPrimeNormExistenceVsRetiredOrbitBoxSurface)),
        Case("quadratic-integer.real-order-factorization-beyond-orbit-box", () => Laws.Claim(lawId: "quadratic-integer.real-order-factorization-beyond-orbit-box", claim: QuadraticIntegerClaims.RealOrderFactorizationBeyondOrbitBoxSurface)),
        Case("algebra.quadratic-surd-twin-lane", () => Laws.Claim(lawId: "algebra.quadratic-surd-twin-lane", claim: DoublingTowerClaims.QuadraticSurdTwinLaneSurface)),
        Case("algebra.quadratic-twin-linear-ops-full-range", () => Laws.Claim(lawId: "algebra.quadratic-twin-linear-ops-full-range", claim: DoublingTowerClaims.QuadraticTwinLinearOpsFullRangeSurface)),
        Case("algebra.doubling-floor1-matches-fixed-complex", () => Laws.Claim(lawId: "algebra.doubling-floor1-matches-fixed-complex", claim: DoublingTowerClaims.DoublingFloor1MatchesFixedComplexSurface)),
        Case("algebra.doubling-floor2-matches-fixed-quaternion", () => Laws.Claim(lawId: "algebra.doubling-floor2-matches-fixed-quaternion", claim: DoublingTowerClaims.DoublingFloor2MatchesFixedQuaternionSurface)),
        Case("algebra.doubling-floor2-commutator-witness", () => Laws.Claim(lawId: "algebra.doubling-floor2-commutator-witness", claim: DoublingTowerClaims.DoublingFloor2CommutatorWitnessSurface)),
        Case("algebra.doubling-floor3-octonion-norm-vs-oracle", () => Laws.Claim(lawId: "algebra.doubling-floor3-octonion-norm-vs-oracle", claim: DoublingTowerClaims.DoublingFloor3OctonionNormVsOracleSurface)),
        Case("presented.clifford-planar-complex-twin", () => Laws.TwinBinary(lawId: "presented.clifford-planar-complex-twin", domain: CliffordPlanarComplex, tier: Tier.Default, first: GeometricAlgebraClaims.GeometricPlanarComplexSubject, second: GeometricAlgebraClaims.FixedComplexLanes, witness: GeometricAlgebraClaims.ComplexOracleWitness)),
        Case("presented.clifford-planar-split-twin", () => Laws.TwinBinary(lawId: "presented.clifford-planar-split-twin", domain: CliffordPlanarSplit, tier: Tier.Default, first: GeometricAlgebraClaims.GeometricPlanarSplitSubject, second: GeometricAlgebraClaims.FixedSplitLanes, witness: GeometricAlgebraClaims.SplitOracleWitness)),
        Case("presented.clifford-planar-dual-twin", () => Laws.TwinBinary(lawId: "presented.clifford-planar-dual-twin", domain: CliffordPlanarDual, tier: Tier.Default, first: GeometricAlgebraClaims.GeometricPlanarDualSubject, second: GeometricAlgebraClaims.FixedDualLanes, witness: GeometricAlgebraClaims.DualOracleWitness)),
        Case("presented.clifford-quaternion-even-twin", () => Laws.VectorTwin(lawId: "presented.clifford-quaternion-even-twin", domain: CliffordQuaternionEven, tier: Tier.Default, width: 4, first: GeometricAlgebraClaims.GeometricQuaternionEvenFirst, second: GeometricAlgebraClaims.GeometricQuaternionEvenSecond, witness: null)),
        Case("presented.clifford-motor-rigid-transform-twin", () => Laws.SweptClaim(lawId: "presented.clifford-motor-rigid-transform-twin", domain: CliffordMotor, tier: Tier.Default, width: 10, claim: GeometricAlgebraClaims.GeometricMotorRigidTransformSurface)),
        Case("presented.clifford-reverse-anti-automorphism", () => Laws.SweptClaim(lawId: "presented.clifford-reverse-anti-automorphism", domain: CliffordReverse, tier: Tier.Default, width: 16, claim: GeometricAlgebraClaims.GeometricReverseSurface)),
        Case("presented.clifford-multivector-decomposition", () => Laws.SweptClaim(lawId: "presented.clifford-multivector-decomposition", domain: CliffordMultivector, tier: Tier.Default, width: 16, claim: GeometricAlgebraClaims.GeometricMultivectorDecompositionSurface)),
        Case("algebra.monogenic-degree2-and-degree3-match-independent-reference", () => Laws.SweptClaim(lawId: "algebra.monogenic-degree2-and-degree3-match-independent-reference", domain: MonogenicExact, tier: Tier.Default, width: 8, claim: GeometricAlgebraClaims.MonogenicExactSurface)),
        Case("algebra.monogenic-plastic-ratio-recurrence", () => Laws.Claim(lawId: "algebra.monogenic-plastic-ratio-recurrence", claim: GeometricAlgebraClaims.MonogenicPlasticRatioSurface)),
        Case("algebra.monogenic-fixed-fusion-diverges-from-reference", () => Laws.DivergenceCanary(lawId: "algebra.monogenic-fixed-fusion-diverges-from-reference", domain: MonogenicFusion, tier: Tier.Default, width: 3, fused: GeometricAlgebraClaims.MonogenicFusedMultiply, perProduct: GeometricAlgebraClaims.MonogenicPerProductMultiply, minimumDivergences: 100)),

        // ---- meet: the attenuation carriers — the lawful core of the authority system's narrowing pipeline ----
        // Every case sweeps all three shipped carriers: MeetMask64, MeetQuantity64, and the product closed at
        // mask × quantity (the envelope shape the intended consumers pair). The identity/absorber/monotonicity cases
        // are the discriminating ones — union and maximum satisfy idempotence, commutativity and associativity too, so
        // only Top/Bottom/never-widens separate a meet from its dual. The authority DECISION is deliberately absent:
        // it is not a lattice (order-dependent exclusivity, rule-reporting verdicts, non-commuting grant transitions),
        // and only the envelope attenuation codified here is algebra.
        Case("meet.associative", () => Laws.SweptClaim(lawId: "meet.associative", domain: MeetAssociative, tier: Tier.Default, width: 3, claim: MeetClaims.MeetIsAssociative)),
        Case("meet.attenuation-never-widens", () => Laws.SweptClaim(lawId: "meet.attenuation-never-widens", domain: MeetMonotonicity, tier: Tier.Default, width: 3, claim: MeetClaims.MeetNeverWidens)),
        Case("meet.bottom-absorbing", () => Laws.SweptClaim(lawId: "meet.bottom-absorbing", domain: MeetBottomAbsorption, tier: Tier.Default, width: 2, claim: MeetClaims.BottomAbsorbs)),
        Case("meet.commutative", () => Laws.SweptClaim(lawId: "meet.commutative", domain: MeetCommutative, tier: Tier.Default, width: 2, claim: MeetClaims.MeetIsCommutative)),
        Case("meet.idempotent", () => Laws.SweptClaim(lawId: "meet.idempotent", domain: MeetIdempotent, tier: Tier.Default, width: 2, claim: MeetClaims.MeetIsIdempotent)),
        Case("meet.order-agrees-with-meet", () => Laws.SweptClaim(lawId: "meet.order-agrees-with-meet", domain: MeetOrderCoherence, tier: Tier.Default, width: 2, claim: MeetClaims.OrderAgreesWithMeet)),
        Case("meet.product-composes-componentwise", () => Laws.SweptClaim(lawId: "meet.product-composes-componentwise", domain: MeetProductComposition, tier: Tier.Default, width: 3, claim: MeetClaims.ProductComposesComponentwise)),
        Case("meet.top-identity", () => Laws.SweptClaim(lawId: "meet.top-identity", domain: MeetTopIdentity, tier: Tier.Default, width: 2, claim: MeetClaims.TopIsIdentity)),
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
        var declaration = LawDeclarations.All.TryGetValue(key: id, value: out var found)
            ? found
            : throw new InvalidOperationException($"law id '{id}' has no declaration under tests/Puck.Maths.Tests/laws/.");

        return new(
            Id: id,
            Tier: ParseTier(id: id, token: declaration.Tier),
            Members: [.. declaration.Members.Select(selector: member => ResolveMember(id: id, member: member))],
            Legs: [.. declaration.Legs.Select(selector: leg => leg.ToLeg())],
            Run: run
        );
    }

    /// <summary>Resolves one declared member reference to a <see cref="CoverRef"/> by looking up its declaring type in
    /// the Puck.Maths assembly.</summary>
    /// <param name="id">The owning law id, named in the exception if resolution fails.</param>
    /// <param name="member">The declared member reference.</param>
    /// <returns>The resolved cover reference.</returns>
    /// <exception cref="InvalidOperationException"><paramref name="member"/>'s type does not resolve.</exception>
    private static CoverRef ResolveMember(string id, MemberRef member) {
        var type = MathsAssembly.GetType(name: member.Type)
            ?? throw new InvalidOperationException($"law '{id}' names a member of type '{member.Type}', which does not resolve in the Puck.Maths assembly.");

        return new(Type: type, Name: member.Name);
    }

    /// <summary>Parses a declared tier token.</summary>
    /// <param name="id">The owning law id, named in the exception if parsing fails.</param>
    /// <param name="token">The tier token, for example <c>"Deep"</c>.</param>
    /// <returns>The parsed tier.</returns>
    /// <exception cref="InvalidOperationException"><paramref name="token"/> names no <see cref="Tier"/> member.</exception>
    private static Tier ParseTier(string id, string token) =>
        Enum.TryParse<Tier>(value: token, ignoreCase: false, result: out var parsed)
            ? parsed
            : throw new InvalidOperationException($"law '{id}' declares tier '{token}', which is not a recognized Tier.");
}
