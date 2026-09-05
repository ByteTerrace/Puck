using System.Numerics;

namespace Puck.Maths;

/// <summary>
/// The <see cref="BigInteger"/>/<see cref="Rational"/> derivation behind <see cref="CurvatureSpline.Compile"/> —
/// never on a per-tick or per-frame path. Solves the curvature-first quadratic system of Wittens' "Making Curvature
/// Front and Center" for the two tangent lengths, isolates every real root of the general branch's quartic exactly
/// (a Sturm sequence over <see cref="Rational"/> coefficients — no float, no iteration-order dependence), then
/// CERTIFIES two downstream decisions the isolation alone does not decide: whether an isolated root's (l0, l1) pair
/// is admissible, and — when more than one is — which minimizes l0² + l1². Both certify by refining the isolating
/// interval (never searching for a different root) until the decision holds for the WHOLE interval or a bounded
/// budget is spent, at which point compile refuses rather than guess across a boundary or an unresolved tie; see
/// <see cref="CertificationRefinementBudget"/>. The single-root branches (one or both curvatures zero) need no such
/// certification — their (l0, l1) is an exact closed-form rational, decided outright against the same bounds. Every
/// compiled raw rounds ONCE, from a representative point of its final certified interval, to
/// <see cref="CurvatureSpline.CoefficientFractionBitCount"/> — safe because that interval is always many orders of
/// magnitude narrower than the Q32 rounding grid (the same remarks), not because the point is the algebraic root
/// itself: <c>SinCosExact</c> and <c>ExactSqrt</c> are themselves guard-precision roundings of transcendental and
/// irrational values, so no step in this pipeline is exact in the sense of carrying an unrounded real number
/// end to end.
/// </summary>
internal static class CurvatureSplineExactMath {
    // Bisection/root-isolation working precision (fraction bits past the decimal point); far past the Q32 the caller
    // rounds to, so which endpoint of a fully isolated bracket is read makes no difference to the final rounding.
    private const int GuardBits = 96;

    // Below this width an isolating bracket is certified to contain exactly one root and refinement stops.
    private static readonly Rational GuardWidth = new(BigInteger.One, (BigInteger.One << GuardBits));
    // The nudge applied to a bisection query point that lands exactly on a root, so root isolation never has to
    // special-case an exact hit — astronomically small relative to GuardWidth, so it never perturbs which bracket a
    // genuine root lands in.
    private static readonly Rational RootNudge = new(BigInteger.One, (BigInteger.One << 200));
    private static readonly Rational RationalZero = new(BigInteger.Zero, BigInteger.One);
    private static readonly Rational RationalTwo = Rational.Two;
    private static readonly Rational OneHalf = new(BigInteger.One, (2 * BigInteger.One));
    private static readonly Rational ThreeHalves = new((3 * BigInteger.One), (2 * BigInteger.One));
    private static readonly Rational NineHalves = new((9 * BigInteger.One), (2 * BigInteger.One));
    private static readonly Rational TwentySevenEighths = new((27 * BigInteger.One), (8 * BigInteger.One));
    private static readonly Rational TwoThirds = new((2 * BigInteger.One), (3 * BigInteger.One));
    private static readonly Rational OneThird = new(BigInteger.One, (3 * BigInteger.One));

    /// <summary>Compiles one cubic-Bézier segment between two authored knots: solves the curvature-first system for
    /// the tangent lengths, rounds every control point and derivative point once from the exact geometry, builds the
    /// Simpson arc-length table, and derives the linear elevation grade.</summary>
    internal static CurvatureSplineSegment CompileSegment(CurvatureSplineKnot start, CurvatureSplineKnot end, int segmentIndex) {
        var p0X = ExactQ16(value: start.X);
        var p0Z = ExactQ16(value: start.Z);
        var p3X = ExactQ16(value: end.X);
        var p3Z = ExactQ16(value: end.Z);

        var chordX = (p3X - p0X);
        var chordZ = (p3Z - p0Z);
        var chordLengthSquared = ((chordX * chordX) + (chordZ * chordZ));

        if (SignOf(value: (chordLengthSquared - Square(value: ExactQ16(value: CurvatureSpline.MinChordLength)))) < 0) {
            throw new CurvatureSplineException(detail: $"the planar chord is shorter than {CurvatureSpline.MinChordLength}.", refusal: CurvatureSplineRefusal.ZeroLengthChord, segmentIndex: segmentIndex);
        }

        var (sin0, cos0) = SecondOrderExactMath.SinCosExact(numerator: start.TangentYaw.Value, denominator: (1L << FixedQ4816.FractionBitCount));
        var (sin1, cos1) = SecondOrderExactMath.SinCosExact(numerator: end.TangentYaw.Value, denominator: (1L << FixedQ4816.FractionBitCount));
        var t0X = new Rational(cos0, (BigInteger.One << SecondOrderExactMath.GuardFractionBitCount));
        var t0Z = new Rational(sin0, (BigInteger.One << SecondOrderExactMath.GuardFractionBitCount));
        var t1X = new Rational(cos1, (BigInteger.One << SecondOrderExactMath.GuardFractionBitCount));
        var t1Z = new Rational(sin1, (BigInteger.One << SecondOrderExactMath.GuardFractionBitCount));

        var s0 = Cross2(ax: t0X, az: t0Z, bx: chordX, bz: chordZ);
        var s1 = Cross2(ax: t1X, az: t1Z, bx: chordX, bz: chordZ);
        var w = Cross2(ax: t0X, az: t0Z, bx: t1X, bz: t1Z);
        var kappa0 = ExactQ16(value: start.Curvature);
        var kappa1 = ExactQ16(value: end.Curvature);
        var chordLengthSquaredReal = (chordLengthSquared); // real units squared (Cx, Cz already real-valued rationals)
        var searchHigh = (((Abs(value: chordX) + Abs(value: chordZ)) * new Rational(((BigInteger)CurvatureSpline.MaxTangentChordRatio), BigInteger.One)) + Rational.One);

        var (tangent0, tangent1) = SolveTangentLengths(
            chordLengthSquared: chordLengthSquaredReal,
            kappa0: kappa0,
            kappa1: kappa1,
            s0: s0,
            s1: s1,
            searchHigh: searchHigh,
            segmentIndex: segmentIndex,
            w: w
        );

        var p1X = (p0X + (tangent0 * t0X));
        var p1Z = (p0Z + (tangent0 * t0Z));
        var p2X = (p3X - (tangent1 * t1X));
        var p2Z = (p3Z - (tangent1 * t1Z));

        // Derivative control points, from the EXACT (pre-rounding) geometry — one rounding each below, never two.
        var d0X = (RationalThree * (p1X - p0X));
        var d0Z = (RationalThree * (p1Z - p0Z));
        var d1X = (RationalThree * (p2X - p1X));
        var d1Z = (RationalThree * (p2Z - p1Z));
        var d2X = (RationalThree * (p3X - p2X));
        var d2Z = (RationalThree * (p3Z - p2Z));
        var e0X = (RationalTwo * (d1X - d0X));
        var e0Z = (RationalTwo * (d1Z - d0Z));
        var e1X = (RationalTwo * (d2X - d1X));
        var e1Z = (RationalTwo * (d2Z - d1Z));

        CheckInteriorCusp(a0X: d0X, a0Z: d0Z, a1X: d1X, a1Z: d1Z, a2X: d2X, a2Z: d2Z, segmentIndex: segmentIndex);

        var table = BuildArcTable(d0X: d0X, d0Z: d0Z, d1X: d1X, d1Z: d1Z, d2X: d2X, d2Z: d2Z, segmentIndex: segmentIndex);
        var lengthRaw = table[^1];

        var y0Raw = (start.Elevation.Value << (CurvatureSpline.CoefficientFractionBitCount - FixedQ4816.FractionBitCount));
        var y1Raw = (end.Elevation.Value << (CurvatureSpline.CoefficientFractionBitCount - FixedQ4816.FractionBitCount));

        if (!FixedPointRounding.TryRoundRational(denominator: lengthRaw, fractionBitCount: CurvatureSpline.CoefficientFractionBitCount, numerator: (y1Raw - y0Raw), result: out var gradeRaw)) {
            throw new CurvatureSplineException(detail: "the elevation grade does not fit the Q32 coefficient carrier.", refusal: CurvatureSplineRefusal.CarrierOverflow, segmentIndex: segmentIndex);
        }

        return new() {
            D0X = RoundQ32(detail: "the derivative control point D0.x", segmentIndex: segmentIndex, value: d0X),
            D0Z = RoundQ32(detail: "the derivative control point D0.z", segmentIndex: segmentIndex, value: d0Z),
            D1X = RoundQ32(detail: "the derivative control point D1.x", segmentIndex: segmentIndex, value: d1X),
            D1Z = RoundQ32(detail: "the derivative control point D1.z", segmentIndex: segmentIndex, value: d1Z),
            D2X = RoundQ32(detail: "the derivative control point D2.x", segmentIndex: segmentIndex, value: d2X),
            D2Z = RoundQ32(detail: "the derivative control point D2.z", segmentIndex: segmentIndex, value: d2Z),
            E0X = RoundQ32(detail: "the second-derivative control point E0.x", segmentIndex: segmentIndex, value: e0X),
            E0Z = RoundQ32(detail: "the second-derivative control point E0.z", segmentIndex: segmentIndex, value: e0Z),
            E1X = RoundQ32(detail: "the second-derivative control point E1.x", segmentIndex: segmentIndex, value: e1X),
            E1Z = RoundQ32(detail: "the second-derivative control point E1.z", segmentIndex: segmentIndex, value: e1Z),
            ArcTable = table,
            GradeRaw = gradeRaw,
            LengthRaw = lengthRaw,
            P0X = RoundQ32(detail: "the shared knot P0.x", segmentIndex: segmentIndex, value: p0X),
            P0Z = RoundQ32(detail: "the shared knot P0.z", segmentIndex: segmentIndex, value: p0Z),
            P1X = RoundQ32(detail: "the derived control point P1.x", segmentIndex: segmentIndex, value: p1X),
            P1Z = RoundQ32(detail: "the derived control point P1.z", segmentIndex: segmentIndex, value: p1Z),
            P2X = RoundQ32(detail: "the derived control point P2.x", segmentIndex: segmentIndex, value: p2X),
            P2Z = RoundQ32(detail: "the derived control point P2.z", segmentIndex: segmentIndex, value: p2Z),
            P3X = RoundQ32(detail: "the shared knot P3.x", segmentIndex: segmentIndex, value: p3X),
            P3Z = RoundQ32(detail: "the shared knot P3.z", segmentIndex: segmentIndex, value: p3Z),
            StationRaw = 0L, // filled in by the caller once every segment's LengthRaw is known.
            Tangent0LengthRaw = RoundQ32(detail: "the derived tangent length l0", segmentIndex: segmentIndex, value: tangent0),
            Tangent1LengthRaw = RoundQ32(detail: "the derived tangent length l1", segmentIndex: segmentIndex, value: tangent1),
            Y0Raw = y0Raw,
            Y1Raw = y1Raw,
        };
    }

    // The number of EXTRA bisections IsolateRoots' own GuardWidth (2^-96) isolation may be refined by when a
    // downstream decision (an admissibility bound, the minimum-l0²+l1² tie) is not yet decided at that width — never
    // to find a NEW root (exactly one is already certified inside the bracket), only to narrow where inside it a
    // boundary or a tie sits. 64 further halvings reaches 2^-160, a margin no engineered-adversarial admissibility
    // or tie case in this suite has exhausted; a case that does is refused rather than guessed at.
    private const int CertificationRefinementBudget = 64;

    private enum AdmissibilityVerdict { Admissible, Inadmissible, Uncertain }

    // Solves E0: (3/2)κ0·l0² + w·l1 − s0 = 0, E1: (3/2)κ1·l1² + w·l0 + s1 = 0 for the admissible (l0, l1) pair,
    // exactly per the branch table derived from the two equations, and picks deterministically among multiple
    // admissible roots by minimizing l0² + l1² (tie-broken by the smaller l0, which the ascending isolation order
    // already produces first).
    private static (Rational L0, Rational L1) SolveTangentLengths(Rational chordLengthSquared, Rational kappa0, Rational kappa1, Rational s0, Rational s1, int segmentIndex, Rational searchHigh, Rational w) {
        var kappa0Zero = RationalIsZero(value: kappa0);
        var kappa1Zero = RationalIsZero(value: kappa1);
        var wZero = RationalIsZero(value: w);

        if (!wZero) {
            if (!kappa0Zero && !kappa1Zero) {
                var c0 = ((((ThreeHalves * kappa1) * s0) * s0) + ((s1 * w) * w));
                var c1 = ((w * w) * w);
                var c2 = -(((NineHalves * kappa0) * kappa1) * s0);
                var c4 = (((TwentySevenEighths * kappa0) * kappa0) * kappa1);
                var quartic = new[] { c0, c1, c2, RationalZero, c4 };
                var brackets = IsolateRoots(polynomial: quartic, lo: ExactQ16(value: CurvatureSpline.MinTangentLength), hi: searchHigh);
                var best = default((Rational Lo, Rational Hi, Rational FLo, Rational FHi)?);

                // Recomputes an (Lo, Hi, FLo, FHi) tuple's l0²+l1² objective interval after RefineBracket narrows it.
                (Rational Lo, Rational Hi, Rational FLo, Rational FHi) Recompute(Rational lo, Rational hi) {
                    var (l1Lo, l1Hi) = L1Range(hi: hi, kappa0: kappa0, lo: lo, s0: s0, w: w);
                    var (l0SqLo, l0SqHi) = SquareRange(hi: hi, lo: lo);
                    var (l1SqLo, l1SqHi) = SquareRange(hi: l1Hi, lo: l1Lo);

                    return (lo, hi, (l0SqLo + l1SqLo), (l0SqHi + l1SqHi));
                }

                foreach (var (isolatedLo, isolatedHi) in brackets) {
                    var (verdict, lo, hi) = CertifyAdmissibility(chordLengthSquared: chordLengthSquared, hi: isolatedHi, kappa0: kappa0, lo: isolatedLo, quartic: quartic, s0: s0, w: w);

                    if (verdict == AdmissibilityVerdict.Inadmissible) { continue; }
                    if (verdict == AdmissibilityVerdict.Uncertain) {
                        throw new CurvatureSplineException(detail: $"a tangent-length root lies within {CertificationRefinementBudget} extra bisections of an authoring bound; refusing rather than choosing across the boundary.", refusal: CurvatureSplineRefusal.CurvatureUnreachable, segmentIndex: segmentIndex);
                    }

                    var candidate = Recompute(hi: hi, lo: lo);

                    if (best is null) {
                        best = candidate;
                        continue;
                    }

                    // `best` was visited first (ascending l0). Certainly smaller (candidate.FHi < best.FLo) replaces
                    // it; certainly not smaller (candidate.FLo ≥ best.FHi) keeps it; otherwise the two objective
                    // intervals overlap, and refining WHICHEVER is wider — the one still carrying most of the shared
                    // uncertainty — separates them or exhausts the budget. On exhaustion the two roots' minimum
                    // l0²+l1² values are tied at guard precision: the documented policy keeps `best`, the smaller-l0
                    // root the ascending isolation order visited first, rather than an unproved exact tie rule.
                    for (var attempt = 0; ; ++attempt) {
                        if (SignOf(value: (candidate.FHi - best.Value.FLo)) < 0) {
                            best = candidate;
                            break;
                        }
                        if ((SignOf(value: (candidate.FLo - best.Value.FHi)) >= 0) || (attempt >= CertificationRefinementBudget)) {
                            break;
                        }

                        var bestWidth = (best.Value.Hi - best.Value.Lo);
                        var candidateWidth = (candidate.Hi - candidate.Lo);

                        if (SignOf(value: (bestWidth - candidateWidth)) >= 0) {
                            var (refinedLo, refinedHi) = RefineBracket(polynomial: quartic, lo: best.Value.Lo, hi: best.Value.Hi);

                            best = Recompute(hi: refinedHi, lo: refinedLo);
                        } else {
                            var (refinedLo, refinedHi) = RefineBracket(hi: candidate.Hi, lo: candidate.Lo, polynomial: quartic);

                            candidate = Recompute(hi: refinedHi, lo: refinedLo);
                        }
                    }
                }

                if (best is null) {
                    throw new CurvatureSplineException(detail: "no admissible tangent-length pair solves the curvature system within the authoring bounds.", refusal: CurvatureSplineRefusal.CurvatureUnreachable, segmentIndex: segmentIndex);
                }

                // The certified bracket is always far narrower than one Q32 raw (CertificationRefinementBudget's own
                // remarks): even its worst case, 2^-160, is 2^128 narrower than the 2^-32 rounding grid every raw
                // this solve feeds narrows to, so every point inside it — the midpoint, same as an uncertified
                // bracket's representative point — rounds identically. That margin is what makes representing the
                // certified root by its midpoint safe, not a claim that the midpoint IS the algebraic root.
                var winningL0 = ((best.Value.Lo + best.Value.Hi) * OneHalf);
                var winningL1 = ((s0 - (((ThreeHalves * kappa0) * winningL0) * winningL0)) / w);

                return (winningL0, winningL1);
            }
            if (kappa0Zero && kappa1Zero) {
                return AdmitOrRefuse(chordLengthSquared: chordLengthSquared, l0: -(s1 / w), l1: (s0 / w), segmentIndex: segmentIndex);
            }
            if (kappa0Zero) { // kappa1 != 0
                var l1 = (s0 / w);
                var l0 = ((-s1 - (((ThreeHalves * kappa1) * l1) * l1)) / w);

                return AdmitOrRefuse(chordLengthSquared: chordLengthSquared, l0: l0, l1: l1, segmentIndex: segmentIndex);
            }
            { // kappa1Zero, kappa0 != 0
                var l0 = -(s1 / w);
                var l1 = ((s0 - (((ThreeHalves * kappa0) * l0) * l0)) / w);

                return AdmitOrRefuse(chordLengthSquared: chordLengthSquared, l0: l0, l1: l1, segmentIndex: segmentIndex);
            }
        }

        // w == 0: the two equations decouple, one per side. The canonical |C|/3 completion (an authored straight
        // segment) needs the chord length regardless of which side's curvature is zero, so it is always formed here.
        var chordLength = ExactSqrt(value: chordLengthSquared);

        Rational tangent0;

        if (!kappa0Zero) {
            var product = (s0 * kappa0);

            if (SignOf(value: product) <= 0) {
                throw new CurvatureSplineException(detail: "w = 0 and κ0 ≠ 0, but s0·κ0 is not strictly positive, so no real tangent length solves the start curvature.", refusal: CurvatureSplineRefusal.TangentCurvatureInconsistent, segmentIndex: segmentIndex);
            }

            tangent0 = ExactSqrt(value: (TwoThirds * (s0 / kappa0)));
        } else if (!RationalIsZero(value: s0)) {
            throw new CurvatureSplineException(detail: "w = 0 and κ0 = 0, but s0 ≠ 0 — the start tangent and chord geometry cannot meet at zero curvature.", refusal: CurvatureSplineRefusal.TangentCurvatureInconsistent, segmentIndex: segmentIndex);
        } else {
            tangent0 = (chordLength * OneThird);
        }

        Rational tangent1;

        if (!kappa1Zero) {
            var product = (s1 * kappa1);

            if (SignOf(value: product) >= 0) {
                throw new CurvatureSplineException(detail: "w = 0 and κ1 ≠ 0, but s1·κ1 is not strictly negative, so no real tangent length solves the end curvature.", refusal: CurvatureSplineRefusal.TangentCurvatureInconsistent, segmentIndex: segmentIndex);
            }

            tangent1 = ExactSqrt(value: -(TwoThirds * (s1 / kappa1)));
        } else if (!RationalIsZero(value: s1)) {
            throw new CurvatureSplineException(detail: "w = 0 and κ1 = 0, but s1 ≠ 0 — the end tangent and chord geometry cannot meet at zero curvature.", refusal: CurvatureSplineRefusal.TangentCurvatureInconsistent, segmentIndex: segmentIndex);
        } else {
            tangent1 = (chordLength * OneThird);
        }

        return AdmitOrRefuse(chordLengthSquared: chordLengthSquared, l0: tangent0, l1: tangent1, segmentIndex: segmentIndex);
    }
    private static (Rational L0, Rational L1) AdmitOrRefuse(Rational l0, Rational l1, Rational chordLengthSquared, int segmentIndex) {
        if (!IsAdmissible(chordLengthSquared: chordLengthSquared, l0: l0, l1: l1)) {
            throw new CurvatureSplineException(detail: "the unique tangent-length solution falls outside the authoring bounds.", refusal: CurvatureSplineRefusal.CurvatureUnreachable, segmentIndex: segmentIndex);
        }

        return (l0, l1);
    }
    private static bool IsAdmissible(Rational l0, Rational l1, Rational chordLengthSquared) {
        var min = ExactQ16(value: CurvatureSpline.MinTangentLength);
        var capSquared = (chordLengthSquared * new Rational((((BigInteger)CurvatureSpline.MaxTangentChordRatio) * CurvatureSpline.MaxTangentChordRatio), BigInteger.One));

        return (
            (SignOf(value: (l0 - min)) >= 0) &&
            (SignOf(value: (l1 - min)) >= 0) &&
            (SignOf(value: ((l0 * l0) - capSquared)) <= 0) &&
            (SignOf(value: ((l1 * l1) - capSquared)) <= 0)
        );
    }
    // l1 as a function of l0 (E0 solved for l1) is monotonic over l0 > 0: its derivative −3·κ0·l0/w has a fixed
    // sign, since κ0 and w are both nonzero constants for one segment's solve. So l1's exact range over [lo, hi] is
    // achieved AT the two endpoints — no interior extremum to miss — and evaluating both and sorting is a tight
    // enclosure, not merely a valid one.
    private static (Rational Lo, Rational Hi) L1Range(Rational lo, Rational hi, Rational s0, Rational kappa0, Rational w) {
        var atLo = ((s0 - (((ThreeHalves * kappa0) * lo) * lo)) / w);
        var atHi = ((s0 - (((ThreeHalves * kappa0) * hi) * hi)) / w);

        return ((SignOf(value: (atHi - atLo)) < 0) ? (atHi, atLo) : (atLo, atHi));
    }
    // The exact range of x² over x ∈ [lo, hi]: monotonic on either side of zero, so the extrema sit at the
    // endpoints; when the interval straddles zero the minimum is the exact zero itself.
    private static (Rational Lo, Rational Hi) SquareRange(Rational lo, Rational hi) {
        if (SignOf(value: lo) >= 0) { return ((lo * lo), (hi * hi)); }
        if (SignOf(value: hi) <= 0) { return ((hi * hi), (lo * lo)); }

        var loSquared = (lo * lo);
        var hiSquared = (hi * hi);

        return (RationalZero, ((SignOf(value: (loSquared - hiSquared)) > 0) ? loSquared : hiSquared));
    }
    // Classifies the FOUR admissibility inequalities (l0 ≥ min, l1 ≥ min, l0² ≤ cap², l1² ≤ cap²) against the
    // interval [l0Lo, l0Hi] × [l1Lo, l1Hi]: Inadmissible when the interval's OWN best case already fails one
    // inequality (so the true root fails it too, wherever inside the interval it sits), Admissible when the
    // interval's OWN worst case already satisfies every inequality, Uncertain — the interval straddles at least one
    // bound — otherwise.
    private static AdmissibilityVerdict ClassifyAdmissibility(Rational l0Lo, Rational l0Hi, Rational l1Lo, Rational l1Hi, Rational chordLengthSquared) {
        var min = ExactQ16(value: CurvatureSpline.MinTangentLength);
        var capSquared = (chordLengthSquared * new Rational((((BigInteger)CurvatureSpline.MaxTangentChordRatio) * CurvatureSpline.MaxTangentChordRatio), BigInteger.One));

        var (l0SqLo, l0SqHi) = SquareRange(hi: l0Hi, lo: l0Lo);
        var (l1SqLo, l1SqHi) = SquareRange(hi: l1Hi, lo: l1Lo);

        if (
            (SignOf(value: (l0Hi - min)) < 0) ||
            (SignOf(value: (l1Hi - min)) < 0) ||
            (SignOf(value: (l0SqLo - capSquared)) > 0) ||
            (SignOf(value: (l1SqLo - capSquared)) > 0)
        ) {
            return AdmissibilityVerdict.Inadmissible;
        }

        return (
            (
                (SignOf(value: (l0Lo - min)) >= 0) &&
                (SignOf(value: (l1Lo - min)) >= 0) &&
                (SignOf(value: (l0SqHi - capSquared)) <= 0) &&
                (SignOf(value: (l1SqHi - capSquared)) <= 0)
            )
            ? AdmissibilityVerdict.Admissible
            : AdmissibilityVerdict.Uncertain
        );
    }
    // Refines an isolated bracket — known, by construction, to contain exactly one root of `polynomial` — by direct
    // bisection on the polynomial's own sign, halving the width each call. Never used to FIND a root; only to narrow
    // where inside an already-certain one an admissibility bound or an objective tie sits.
    private static (Rational Lo, Rational Hi) RefineBracket(Rational[] polynomial, Rational lo, Rational hi) {
        var signAtLo = SignOf(value: Evaluate(coefficients: polynomial, x: lo));
        var mid = NudgeAwayFromRoot(leading: polynomial, negative: false, x: ((lo + hi) * OneHalf));
        var signAtMid = SignOf(value: Evaluate(coefficients: polynomial, x: mid));

        return ((signAtMid == signAtLo) ? (mid, hi) : (lo, mid));
    }
    // Certifies whether the root isolated inside [lo, hi] is admissible, refining the bracket up to
    // CertificationRefinementBudget extra bisections when the interval straddles a bound. Returns Uncertain, with
    // the bracket at whatever width the budget reached, when a bound cannot be decided within it — the caller
    // refuses rather than picking a side.
    private static (AdmissibilityVerdict Verdict, Rational Lo, Rational Hi) CertifyAdmissibility(Rational[] quartic, Rational lo, Rational hi, Rational s0, Rational kappa0, Rational w, Rational chordLengthSquared) {
        for (var attempt = 0; ; ++attempt) {
            var (l1Lo, l1Hi) = L1Range(hi: hi, kappa0: kappa0, lo: lo, s0: s0, w: w);
            var verdict = ClassifyAdmissibility(chordLengthSquared: chordLengthSquared, l0Hi: hi, l0Lo: lo, l1Hi: l1Hi, l1Lo: l1Lo);

            if ((verdict != AdmissibilityVerdict.Uncertain) || (attempt >= CertificationRefinementBudget)) {
                return (verdict, lo, hi);
            }

            (lo, hi) = RefineBracket(hi: hi, lo: lo, polynomial: quartic);
        }
    }
    // Bounds the minimum of |B'(t)|² over t ∈ [0, 1] (a degree-4 polynomial in t, built from the quadratic
    // derivative control points): the endpoints, plus, at every critical point the tangent-length solve's own Sturm
    // isolation finds exactly (applied here to the cubic derivative — no float, no iteration-order dependence), the
    // smallest of the speed evaluated at that bracket's two endpoints and its midpoint. That is a GUARD-PRECISION
    // approximation of the true minimum, not a certified interval bound the way the admissibility/tie decisions
    // above are: a rigorous bound would need a derivative-magnitude bound on this quartic, which has no cheap closed
    // form. The residual is bounded in practice by how much |B'|² can vary across a bracket already isolated to
    // width < 2^-96 (IsolateRoots' own GuardWidth) — for the polynomial coefficients this method builds (bounded by
    // the admitted tangent lengths, themselves capped at 4·chord ≤ 8·MaxCoordinate), that variation is many orders
    // of magnitude below MinSpeedFloor²'s own scale, so sampling three points per bracket rather than one costs
    // nothing observable while remaining conservative: taking the SMALLEST of the three can only lower the reported
    // minimum, never raise it, so this can only refuse a curve the single-sample check would have admitted, never
    // the reverse.
    private static void CheckInteriorCusp(Rational a0X, Rational a0Z, Rational a1X, Rational a1Z, Rational a2X, Rational a2Z, int segmentIndex) {
        // B'x(t) = a0X + t·(2a1X − 2a0X) + t²·(a0X − 2a1X + a2X), and symmetrically for Z.
        var bx1 = (RationalTwo * (a1X - a0X));
        var bx2 = ((a0X - (RationalTwo * a1X)) + a2X);
        var bz1 = (RationalTwo * (a1Z - a0Z));
        var bz2 = ((a0Z - (RationalTwo * a1Z)) + a2Z);

        // |B'|²(t) = (a0X + bx1·t + bx2·t²)² + (a0Z + bz1·t + bz2·t²)², expanded to a degree-4 polynomial in t.
        var c0 = ((a0X * a0X) + (a0Z * a0Z));
        var c1 = (RationalTwo * ((a0X * bx1) + (a0Z * bz1)));
        var c2 = ((((bx1 * bx1) + ((RationalTwo * a0X) * bx2)) + (bz1 * bz1)) + ((RationalTwo * a0Z) * bz2));
        var c3 = (RationalTwo * ((bx1 * bx2) + (bz1 * bz2)));
        var c4 = ((bx2 * bx2) + (bz2 * bz2));
        var speedSquared = new[] { c0, c1, c2, c3, c4 };

        var minimum = Evaluate(coefficients: speedSquared, x: RationalZero);
        var atOne = Evaluate(coefficients: speedSquared, x: Rational.One);

        if (SignOf(value: (atOne - minimum)) < 0) { minimum = atOne; }

        var derivative = DerivativeOf(coefficients: speedSquared);

        if (!IsZeroPolynomial(coefficients: derivative)) {
            foreach (var (lo, hi) in IsolateRoots(polynomial: derivative, lo: RationalZero, hi: Rational.One)) {
                var candidate = Evaluate(coefficients: speedSquared, x: ((lo + hi) * OneHalf));
                var atLo = Evaluate(coefficients: speedSquared, x: lo);
                var atHi = Evaluate(coefficients: speedSquared, x: hi);

                if (SignOf(value: (atLo - candidate)) < 0) { candidate = atLo; }
                if (SignOf(value: (atHi - candidate)) < 0) { candidate = atHi; }
                if (SignOf(value: (candidate - minimum)) < 0) { minimum = candidate; }
            }
        }

        var floorSquared = Square(value: ExactQ16(value: CurvatureSpline.MinSpeedFloor));

        if (SignOf(value: (minimum - floorSquared)) < 0) {
            throw new CurvatureSplineException(detail: "the segment's speed |B'(t)| dips below the speed floor somewhere on [0, 1].", refusal: CurvatureSplineRefusal.InteriorCusp, segmentIndex: segmentIndex);
        }
    }

    // The arc table's error contract: refine the panel count (always a power of two, starting at 64) until two
    // successive doublings agree at every matching cumulative station to within the segment's own scaled bound (a
    // Richardson estimate of the Simpson quadrature error) AND every panel's own midpoint-vs-chord check (a
    // conservative proxy for the linear-in-t interpolation error CompiledCurvatureSpline.InvertArcTable itself
    // commits to within an accepted panel — see MaxLinearizationError) falls under the same bound. Neither check
    // alone covers what the runtime does: the table only ever gets READ by binary search plus a linear
    // interpolation within one panel, so an accurate cumulative total at coarse stations is not sufficient on its
    // own. The bound itself (ScaledErrorBound) is relative to the segment's own arc length, not a fixed absolute
    // Q32 raw — Simpson's own error term scales with the integrand's magnitude, so a bound tight enough to matter on
    // a five-unit segment is unreachable within any sane panel budget on a million-unit one.
    private const int MinSubintervalCount = 64;
    private const int MaxSubintervalCount = (1 << 16);

    private static long[] BuildArcTable(Rational d0X, Rational d0Z, Rational d1X, Rational d1Z, Rational d2X, Rational d2Z, int segmentIndex) {
        var subintervalCount = MinSubintervalCount;

        var (table, speeds) = BuildArcTableAt(coarseSpeeds: null, d0X: d0X, d0Z: d0Z, d1X: d1X, d1Z: d1Z, d2X: d2X, d2Z: d2Z, segmentIndex: segmentIndex, subintervalCount: subintervalCount);

        while (true) {
            var refinedCount = (subintervalCount * 2);

            if (refinedCount > MaxSubintervalCount) {
                throw new CurvatureSplineException(detail: $"the arc-length table's estimated error did not fall under its scaled bound within {MaxSubintervalCount} panels.", refusal: CurvatureSplineRefusal.ArcLengthErrorUnbounded, segmentIndex: segmentIndex);
            }

            var (refinedTable, refinedSpeeds) = BuildArcTableAt(coarseSpeeds: speeds, d0X: d0X, d0Z: d0Z, d1X: d1X, d1Z: d1Z, d2X: d2X, d2Z: d2Z, segmentIndex: segmentIndex, subintervalCount: refinedCount);
            var quadratureError = MaxQuadratureError(coarse: table, fine: refinedTable);
            var linearError = MaxLinearizationError(speeds: refinedSpeeds, subintervalCount: refinedCount);
            var boundRaw = ScaledErrorBoundRaw(lengthRaw: refinedTable[^1]);
            var errorBound = new Rational(boundRaw, (BigInteger.One << CurvatureSpline.CoefficientFractionBitCount));

            if ((quadratureError <= boundRaw) && (SignOf(value: (linearError - errorBound)) <= 0)) {
                return refinedTable;
            }

            table = refinedTable;
            speeds = refinedSpeeds;
            subintervalCount = refinedCount;
        }
    }
    // Composite Simpson over `subintervalCount` subintervals of |B'(t)|, evaluated exactly at t = j/(2·count) from
    // the pre-rounding derivative control points, cumulative sums formed exactly and rounded ONCE each to Q32.
    // Returns the speed samples alongside the table — BuildArcTable's own Richardson/linearization checks read them
    // without re-evaluating |B'(t)|.
    private static (long[] Table, Rational[] Speeds) BuildArcTableAt(int subintervalCount, Rational[]? coarseSpeeds, Rational d0X, Rational d0Z, Rational d1X, Rational d1Z, Rational d2X, Rational d2Z, int segmentIndex) {
        var speeds = new Rational[((2 * subintervalCount) + 1)];

        for (var j = 0; (j <= (2 * subintervalCount)); ++j) {
            // A doubling's even-indexed samples sit exactly on the coarse grid, so the coarse speeds carry over and
            // only the odd half is rooted afresh.
            if ((coarseSpeeds is not null) && (0 == (j & 1))) {
                speeds[j] = coarseSpeeds[(j >> 1)];

                continue;
            }

            var t = new Rational((BigInteger.One * j), (BigInteger.One * (2 * subintervalCount)));
            var oneMinusT = (Rational.One - t);
            var bx = ((((oneMinusT * oneMinusT) * d0X) + (((RationalTwo * oneMinusT) * t) * d1X)) + ((t * t) * d2X));
            var bz = ((((oneMinusT * oneMinusT) * d0Z) + (((RationalTwo * oneMinusT) * t) * d1Z)) + ((t * t) * d2Z));

            speeds[j] = ExactSqrt(value: ((bx * bx) + (bz * bz)));
        }

        var table = new long[(subintervalCount + 1)];
        var cumulative = RationalZero;
        var panelTimesSix = new Rational(((subintervalCount * 6) * BigInteger.One), BigInteger.One);

        table[0] = 0L;

        for (var j = 0; (j < subintervalCount); ++j) {
            var increment = (((speeds[(2 * j)] + (RationalFour * speeds[((2 * j) + 1)])) + speeds[((2 * j) + 2)]) / panelTimesSix);

            cumulative += increment;
            table[(j + 1)] = RoundQ32(detail: $"the arc-length table entry at t = {(j + 1)}/{subintervalCount}", segmentIndex: segmentIndex, value: cumulative);
        }

        for (var j = 1; (j < table.Length); ++j) {
            if (table[j] <= table[(j - 1)]) {
                throw new CurvatureSplineException(detail: "the arc-length table failed to advance strictly — the segment's speed underflowed the Q32 raw carrier.", refusal: CurvatureSplineRefusal.InteriorCusp, segmentIndex: segmentIndex);
            }
        }

        return (table, speeds);
    }
    // The error bound BuildArcTable's Richardson/linearization checks compare against, Q32 raw: relative to the
    // segment's own (already computed) arc length, floored at ArcLengthMinimumErrorBoundRaw so a very short segment
    // is never asked for an unreachable absolute precision.
    private static long ScaledErrorBoundRaw(long lengthRaw) {
        var scaled = (lengthRaw >> CurvatureSpline.ArcLengthRelativeErrorShift);

        return ((scaled > CurvatureSpline.ArcLengthMinimumErrorBoundRaw) ? scaled : CurvatureSpline.ArcLengthMinimumErrorBoundRaw);
    }
    // The largest disagreement between a coarser table's own station and the finer table's matching station (every
    // other entry, since the finer table has exactly double the panels) — a Richardson estimate of the Simpson
    // quadrature error the coarser table carries at every cumulative station, not merely its final total.
    private static long MaxQuadratureError(long[] coarse, long[] fine) {
        var max = 0L;

        for (var j = 0; (j < coarse.Length); ++j) {
            var diff = Math.Abs(value: (fine[(2 * j)] - coarse[j]));

            if (diff > max) { max = diff; }
        }

        return max;
    }
    // A per-panel proxy for how far CompiledCurvatureSpline.InvertArcTable's own linear-in-t interpolation can drift
    // from the true arc-length parameterization within one panel: the gap between the panel's midpoint speed and
    // the average of its endpoint speeds, scaled by the panel's own t-width — zero for a panel with genuinely
    // constant speed, shrinking toward zero as the panel narrows for a smooth segment. A conservative engineering
    // estimate, not a proven tight bound (the standard Simpson error term depends on a fourth derivative this
    // integrand — a square root of a quadratic — does not carry a cheap closed form for); the scaled bound's own
    // floor (ArcLengthMinimumErrorBoundRaw) is sized so refining a short segment past it buys nothing observable at
    // the Q16 scale a caller ever reads.
    private static Rational MaxLinearizationError(Rational[] speeds, int subintervalCount) {
        var deltaT = new Rational(BigInteger.One, (BigInteger.One * subintervalCount));
        var max = RationalZero;

        for (var j = 0; (j < subintervalCount); ++j) {
            var left = speeds[(2 * j)];
            var mid = speeds[((2 * j) + 1)];
            var right = speeds[((2 * j) + 2)];
            var estimate = Abs(value: (((((RationalTwo * mid) - left) - right) * deltaT) * OneThird));

            if (SignOf(value: (estimate - max)) > 0) { max = estimate; }
        }

        return max;
    }
    private static long RoundQ32(Rational value, int segmentIndex, string detail) {
        if (!FixedPointRounding.TryRoundRational(numerator: value.Numerator, denominator: value.Denominator, fractionBitCount: CurvatureSpline.CoefficientFractionBitCount, result: out var raw)) {
            throw new CurvatureSplineException(detail: $"{detail} does not fit the Q32 coefficient carrier.", refusal: CurvatureSplineRefusal.CarrierOverflow, segmentIndex: segmentIndex);
        }

        return raw;
    }
    private static Rational ExactQ16(FixedQ4816 value) =>
        new(value.Value, (BigInteger.One << FixedQ4816.FractionBitCount));
    private static Rational Cross2(Rational ax, Rational az, Rational bx, Rational bz) =>
        ((ax * bz) - (az * bx));
    private static Rational Square(Rational value) =>
        (value * value);
    private static Rational Abs(Rational value) =>
        ((SignOf(value: value) < 0) ? -value : value);
    private static int SignOf(Rational value) =>
        (value.Numerator.Sign * value.Denominator.Sign);
    private static bool RationalIsZero(Rational value) =>
        value.Numerator.IsZero;

    private static readonly Rational RationalThree = new((3 * BigInteger.One), BigInteger.One);
    private static readonly Rational RationalFour = new((4 * BigInteger.One), BigInteger.One);

    // The one guard-scale square root the exact chain uses (§1.6's "guard-scale integer sqrt"): a deterministic,
    // no-float floor(√value · 2^GuardBits), never narrowed until the caller's own closing rounding.
    private static Rational ExactSqrt(Rational value) {
        var numerator = (value.Numerator * value.Denominator.Sign);
        var denominator = BigInteger.Abs(value: value.Denominator);
        var scaled = ((numerator << (2 * GuardBits)) / denominator);
        var root = BigIntegerFunctions.SquareRoot(value: scaled);

        return new Rational(root, (BigInteger.One << GuardBits));
    }
    // --- Exact rational polynomial arithmetic and Sturm-sequence real-root isolation, shared by the tangent-length
    // quartic and the interior-cusp cubic. Coefficients are ascending by degree; DegreeOf reports −1 for the zero
    // polynomial so callers never confuse it with a nonzero constant. ---

    private static Rational Evaluate(Rational[] coefficients, Rational x) {
        var result = RationalZero;

        for (var i = (coefficients.Length - 1); (i >= 0); --i) {
            result = ((result * x) + coefficients[i]);
        }

        return result;
    }
    private static int DegreeOf(Rational[] coefficients) {
        for (var i = (coefficients.Length - 1); (i >= 0); --i) {
            if (!coefficients[i].Numerator.IsZero) { return i; }
        }

        return -1;
    }
    private static bool IsZeroPolynomial(Rational[] coefficients) =>
        (DegreeOf(coefficients: coefficients) < 0);
    private static Rational[] DerivativeOf(Rational[] coefficients) {
        var degree = DegreeOf(coefficients: coefficients);

        if (degree <= 0) { return [RationalZero]; }

        var result = new Rational[degree];

        for (var i = 1; (i <= degree); ++i) {
            result[(i - 1)] = (coefficients[i] * new Rational((i * BigInteger.One), BigInteger.One));
        }

        return result;
    }
    private static Rational[] RemainderOf(Rational[] dividend, Rational[] divisor) {
        var divisorDegree = DegreeOf(coefficients: divisor);
        var leadDivisor = divisor[divisorDegree];
        var remainder = ((Rational[])dividend.Clone());

        while (true) {
            var remainderDegree = DegreeOf(coefficients: remainder);

            if ((remainderDegree < 0) || (remainderDegree < divisorDegree)) { break; }

            var scale = (remainder[remainderDegree] / leadDivisor);
            var shift = (remainderDegree - divisorDegree);

            for (var i = 0; (i <= divisorDegree); ++i) {
                remainder[(shift + i)] = (remainder[(shift + i)] - (scale * divisor[i]));
            }
        }

        return remainder;
    }
    private static List<Rational[]> BuildSturmSequence(Rational[] polynomial) {
        var sequence = new List<Rational[]> { polynomial, DerivativeOf(coefficients: polynomial) };

        while (!IsZeroPolynomial(coefficients: sequence[^1])) {
            var remainder = RemainderOf(dividend: sequence[^2], divisor: sequence[^1]);
            var negated = new Rational[remainder.Length];

            for (var i = 0; (i < remainder.Length); ++i) { negated[i] = -remainder[i]; }

            sequence.Add(item: negated);
        }

        return sequence;
    }
    private static int SignVariationsAt(List<Rational[]> sequence, Rational x) {
        var variations = 0;
        var previousSign = 0;

        foreach (var polynomial in sequence) {
            var sign = SignOf(value: Evaluate(coefficients: polynomial, x: x));

            if (sign == 0) { continue; }
            if ((previousSign != 0) && (sign != previousSign)) { ++variations; }

            previousSign = sign;
        }

        return variations;
    }
    private static Rational NudgeAwayFromRoot(Rational[] leading, Rational x, bool negative) {
        var step = (negative ? -RootNudge : RootNudge);
        var guard = 0;

        while (SignOf(value: Evaluate(coefficients: leading, x: x)) == 0) {
            x = (x + step);

            if (++guard > 64) { break; } // a polynomial identically zero at every nudge cannot occur for our inputs.
        }

        return x;
    }
    private static bool IntervalNarrowerThanGuard(Rational lo, Rational hi) =>
        (SignOf(value: ((hi - lo) - GuardWidth)) < 0);
    // Isolates every real root of `polynomial` within (lo, hi] to width < 2^-GuardBits, as disjoint ascending
    // brackets each certified (by the Sturm sign-variation count) to contain exactly one root. Deterministic: query
    // points that land exactly on a root are nudged by a fixed, tiny epsilon rather than special-cased, so the same
    // sequence of Sturm evaluations runs for a given polynomial and search interval every time.
    private static List<(Rational Lo, Rational Hi)> IsolateRoots(Rational[] polynomial, Rational lo, Rational hi) {
        var sequence = BuildSturmSequence(polynomial: polynomial);
        var results = new List<(Rational, Rational)>();

        if (IsZeroPolynomial(coefficients: sequence[0])) { return results; }

        var boundedLo = NudgeAwayFromRoot(leading: sequence[0], x: lo, negative: true);
        var boundedHi = NudgeAwayFromRoot(leading: sequence[0], x: hi, negative: false);
        var stack = new Stack<(Rational Lo, Rational Hi)>();

        stack.Push(item: (boundedLo, boundedHi));

        while (stack.Count > 0) {
            var (a, b) = stack.Pop();
            var count = (SignVariationsAt(sequence: sequence, x: a) - SignVariationsAt(sequence: sequence, x: b));

            if (count <= 0) { continue; }

            if (count == 1) {
                var narrowLo = a;
                var narrowHi = b;

                while (!IntervalNarrowerThanGuard(hi: narrowHi, lo: narrowLo)) {
                    var mid = NudgeAwayFromRoot(leading: sequence[0], x: ((narrowLo + narrowHi) * OneHalf), negative: false);
                    var leftCount = (SignVariationsAt(sequence: sequence, x: narrowLo) - SignVariationsAt(sequence: sequence, x: mid));

                    if (leftCount >= 1) { narrowHi = mid; } else { narrowLo = mid; }
                }

                results.Add(item: (narrowLo, narrowHi));
                continue;
            }

            var split = NudgeAwayFromRoot(leading: sequence[0], x: ((a + b) * OneHalf), negative: false);

            stack.Push(item: (split, b));
            stack.Push(item: (a, split));
        }

        return results;
    }
}
