using System.Diagnostics;
using System.Numerics;

namespace Puck.Maths;

/// <summary>
/// Scale-free symmetric linear-solve, -invert and -apply kernels for the 2×2 and 3×3 systems a deterministic
/// rigid-body solver forms when it inverts an effective-mass matrix, <c>K·x = rhs</c> for symmetric <c>K</c>, and when
/// it drives the resulting inverse inertia against an angular impulse (<see cref="TryApplySymmetric3"/>). Every operand is
/// a raw carrier at whatever scale the caller is using; the solve is transient and never stored, so there is no
/// persisted format for a caller to confuse it with — the reason this stays an <c>internal static</c> kernel family
/// rather than a wrapping value type, the opposite choice from <see cref="FixedQ1648"/>, which exists precisely
/// because its quantity IS stored. Every kernel here follows <see cref="FixedVectorMath"/>'s shape: precondition by
/// one common power of two, accumulate the whole expression exactly in sign-plus-<see cref="UInt128"/> magnitude
/// (<see cref="FusedArithmetic"/>), and round the returned value exactly once
/// (<see cref="FusedArithmetic.TryDivideMagnitudeRounded"/>).
/// </summary>
/// <remarks>
/// <para><b>The bit budget.</b> Symmetric <c>K = [[a,b,c],[b,d,e],[c,e,f]]</c>; its determinant is the sum of six raw
/// triple products, <c>adf − ae² − b²f + 2bce − c²d</c>. With every preconditioned entry bounded by <c>2^k</c> in
/// magnitude, the widest triple product is bounded by <c>2^3k</c> and the six-term sum by <c>6·2^3k &lt; 2^(3k+3)</c>
/// (<c>6 &lt; 2³</c>). <see cref="Int128"/>'s usable magnitude tops out at <c>2^127 − 1</c>, strictly below
/// <c>2^127</c>, so the requirement is <c>3k + 3 &lt; 127</c>, i.e. <c>k ≤ 41</c> (<c>k = 42</c> already needs
/// <c>2^129</c>). The 2×2 determinant is only two products, <c>ad − b²</c>: worst case (K indefinite, so the two
/// terms add rather than cancel) the sum is bounded by <c>2·2^2k = 2^(2k+1)</c>, and the same strict ceiling gives
/// <c>2k + 1 &lt; 127</c>, i.e. <c>k ≤ 62</c> (not 63 — <c>k = 63</c> lands the bound at exactly <c>2^127</c>, one
/// past what <see cref="Int128"/> can hold).
/// </para>
/// <para>Preconditioning targets the bit one below each of those budgets
/// (<see cref="Symmetric3TargetLeadingBit"/> = 40, <see cref="Symmetric2TargetLeadingBit"/> = 61), because a
/// right-shift's ties-to-even rounding can carry the shifted magnitude up by one more bit — the same margin
/// <see cref="FixedVectorMath.DirectionShift"/> reserves below its own budget for the same reason. <b>Do not reuse
/// <see cref="FixedVectorMath.DirectionShift"/>'s bit 45 here.</b> That constant is sized for a sum of four squares
/// (bounded by <c>4·2^2k</c>, i.e. <c>2k</c> growth), not a sum of triple products (<c>3k</c> growth): at bit 45 a
/// 3×3 determinant needs <c>3·46 + 3 = 141</c> bits — 14 more than <see cref="Int128"/> has, and it would overflow
/// silently rather than refuse.</para>
/// <para><b>The solve numerator is scale-invariant under one shared shift — when that shift is exact.</b>
/// Preconditioning the matrix entries and the right-hand side by the same power of two <c>S</c> leaves the ratio
/// <c>adj(K)·rhs / det(K)</c> exactly unchanged, for any <c>S</c>: <c>det</c> is degree-3 homogeneous in the entries
/// (scaling every entry by <c>2^S</c> scales <c>det</c> by <c>2^3S</c>), the 3×3 adjugate is degree-2 (<c>2^2S</c>),
/// and the right-hand side supplies the missing degree-1 factor (<c>2^S</c>) — so the numerator <c>adj(K)·rhs</c>
/// also scales by <c>2^3S</c>, and the two <c>2^3S</c> factors cancel exactly in the ratio (the 2×2 case is the same
/// argument one degree down: adjugate degree 1, det degree 2, rhs degree 1). Solve therefore needs no correction
/// beyond the caller's own <c>outputFractionShift</c>. <b>The homogeneity argument assumes the scaling BY <c>2^S</c>
/// is exact</b> — true whenever <c>S ≥ 0</c> (a pure, lossless left shift), which is exactly the operating envelope
/// (every group operand's own magnitude already strictly below <c>2^(target+1)</c> — at that power of two the
/// leading bit already sits one past the target, so <see cref="Symmetric2Shift"/> / <see cref="Symmetric3Shift"/>
/// compute <c>S = -1</c>, not zero) that covers any realistic
/// effective-mass or velocity entry at any Q-anything scale. When an operand's own magnitude reaches or exceeds the
/// target — forcing <c>S &lt; 0</c>, a rounding right-shift, before any real work starts — that rounding is not
/// undone by the cancellation, so the ratio is only approximately preserved: the same bounded-precision contract
/// <see cref="FixedVectorMath.Normalize(long, long, long)"/> already carries outside its own preconditioned band. Solve never
/// overflows and never answers by more than that one preconditioning rounding's worth outside its envelope; it is
/// exact to one rounding of the caller's requested scale strictly inside it.</para>
/// <para><b>Invert has no right-hand side to supply that missing degree</b>, so its raw ratio
/// <c>adj(K)/det(K)</c> carries a leftover factor of <c>2^(−S)</c> relative to the true inverse. Invert folds that
/// into the fraction-bit count it asks the division for (<c>outputFractionShift + S</c>) and refuses rather than
/// answering when that combined count would be negative: entries so large that even after being shifted down for
/// the determinant's own overflow safety, the requested output scale has no bits left to represent the (necessarily
/// tiny) answer. This is a conservative, narrow envelope — it never answers wrongly, only declines in a corner where
/// the true inverse legitimately underflows toward the requested scale's resolution. Effective-mass entries at a
/// realistic (Q48.16-like) scale keep <c>S</c> comfortably non-negative and never approach it.</para>
/// <para><b>Singularity and overflow policy.</b> Every member returns <see langword="false"/>, with every
/// <see langword="out"/> parameter set to zero, when the matrix is exactly singular, OR when the correctly rounded
/// result's magnitude does not fit the signed 64-bit output raw. Singularity is decided against the caller's own raw
/// entries, never against the preconditioned ones alone: a lossy right-shift (the corner above) rounds each entry
/// independently and does not in general preserve a rank-deficient matrix's rank, so checking only the preconditioned
/// determinant can turn an exactly singular raw matrix into an apparently nonsingular preconditioned one and fabricate
/// a finite answer for a system that has none. The 2×2 family checks its raw determinant unconditionally — <c>ad-b²</c>
/// is exact for any <see langword="long"/> <c>a</c>, <c>b</c>, <c>d</c>, comfortably inside the sign-plus-<see cref="UInt128"/>
/// magnitude this type already forms products in — so the check costs nothing extra in the common case. The 3×3
/// family runs its own exact (<see cref="BigInteger"/>) raw-determinant check only when the shared shift is negative:
/// whenever it is non-negative the preconditioning is an exact multiplication by <c>2^(3·shift)</c>, which preserves
/// the determinant's zero-ness exactly, so the preconditioned check alone is already exact and the wider check would
/// buy nothing. Both singularity and the output-width refusal are checked before any output is trusted; there is no
/// silent wrap and no silent truncation anywhere in this type.</para>
/// </remarks>
internal static class FixedSymmetricSolve {
    /// <summary>The pre-rounding shift target for the 2×2 family. After <see cref="FixedVectorMath.ScaleRaw"/>,
    /// every preconditioned entry has magnitude at most <c>2⁶²</c> (one bit above this constant, reserved for a
    /// rounding carry), which keeps the two-term determinant and the two-term solve numerator inside
    /// <see cref="Int128"/>'s magnitude. See the type's remarks for the derivation.</summary>
    internal const int Symmetric2TargetLeadingBit = 61;

    /// <summary>The pre-rounding shift target for the 3×3 family. After <see cref="FixedVectorMath.ScaleRaw"/>,
    /// every preconditioned entry has magnitude at most <c>2⁴¹</c> (one bit above this constant, reserved for a
    /// rounding carry), which keeps the six-term triple-product determinant and the three-term solve numerator
    /// inside <see cref="Int128"/>'s magnitude. Do not reuse <see cref="FixedVectorMath.DirectionShift"/>'s bit 45 —
    /// see the type's remarks.</summary>
    internal const int Symmetric3TargetLeadingBit = 40;

    /// <summary>Solves the symmetric 2×2 system <c>[[a,b],[b,d]]·(x,y) = (rhsX,rhsY)</c> for raw operands at any
    /// shared caller scale, rounding the result to <paramref name="outputFractionShift"/> extra bits below the
    /// caller's own scale (16 reproduces a <see cref="FixedQ4816"/>-scaled answer from Q-anything inputs).</summary>
    /// <param name="a">The (0,0) entry.</param>
    /// <param name="b">The (0,1) = (1,0) entry.</param>
    /// <param name="d">The (1,1) entry.</param>
    /// <param name="rhsX">The right-hand side's first component.</param>
    /// <param name="rhsY">The right-hand side's second component.</param>
    /// <param name="outputFractionShift">The non-negative number of extra bits the quotient is rounded to below the
    /// caller's own scale.</param>
    /// <param name="x">The first solution component on success; zero on refusal.</param>
    /// <param name="y">The second solution component on success; zero on refusal.</param>
    /// <returns><see langword="false"/> when the matrix is exactly singular or a result does not fit the raw
    /// carrier; both <paramref name="x"/> and <paramref name="y"/> are zero in that case.</returns>
    internal static bool TrySolveSymmetric2(long a, long b, long d, long rhsX, long rhsY, int outputFractionShift, out long x, out long y) {
        Debug.Assert(condition: (outputFractionShift >= 0), message: "TrySolveSymmetric2 requires a non-negative output fraction shift.");

        var groupMax = Max(FusedArithmetic.RawMagnitude(value: a), FusedArithmetic.RawMagnitude(value: b), FusedArithmetic.RawMagnitude(value: d),
            FusedArithmetic.RawMagnitude(value: rhsX), FusedArithmetic.RawMagnitude(value: rhsY));

        if (groupMax == 0UL) {
            x = 0L;
            y = 0L;
            return false;
        }

        // The RAW (unscaled) determinant is the sole authority on singularity — exact and unconditional here, never
        // merely the preconditioned one, so a lossy right-shift below cannot round an exactly singular raw matrix into
        // an apparently nonsingular preconditioned one and fabricate a finite answer for a system that has none. See
        // the type's remarks.
        var rawDeterminant = FusedArithmetic.AddProducts(firstLeft: a, firstRight: d, secondLeft: b, secondRight: b, subtractSecond: true);

        if (rawDeterminant.Magnitude == UInt128.Zero) {
            x = 0L;
            y = 0L;
            return false;
        }

        var shift = Symmetric2Shift(rawMagnitude: groupMax);
        var sa = FixedVectorMath.ScaleRaw(value: a, shift: shift);
        var sb = FixedVectorMath.ScaleRaw(value: b, shift: shift);
        var sd = FixedVectorMath.ScaleRaw(value: d, shift: shift);
        var srx = FixedVectorMath.ScaleRaw(value: rhsX, shift: shift);
        var sry = FixedVectorMath.ScaleRaw(value: rhsY, shift: shift);

        var det = FusedArithmetic.AddProducts(firstLeft: sa, firstRight: sd, secondLeft: sb, secondRight: sb, subtractSecond: true);
        var nx = FusedArithmetic.AddProducts(firstLeft: sd, firstRight: srx, secondLeft: sb, secondRight: sry, subtractSecond: true);
        var ny = FusedArithmetic.AddProducts(firstLeft: sa, firstRight: sry, secondLeft: sb, secondRight: srx, subtractSecond: true);

        // Neither output is trusted until BOTH round successfully — the contract refuses with every output at zero,
        // never leaving an earlier-computed component behind when a later one overflows.
        var okX = TryFinishRatio(numerator: nx, denominator: det, fractionBitCount: outputFractionShift, result: out var rx);
        var okY = TryFinishRatio(numerator: ny, denominator: det, fractionBitCount: outputFractionShift, result: out var ry);

        if (!okX || !okY) {
            x = 0L;
            y = 0L;
            return false;
        }

        x = rx;
        y = ry;
        return true;
    }

    /// <summary>Solves the symmetric 3×3 system <c>[[a,b,c],[b,d,e],[c,e,f]]·(x,y,z) = (rhsX,rhsY,rhsZ)</c> for raw
    /// operands at any shared caller scale. See <see cref="TrySolveSymmetric2"/> for the shared contract.</summary>
    /// <param name="a">The (0,0) entry.</param>
    /// <param name="b">The (0,1) = (1,0) entry.</param>
    /// <param name="c">The (0,2) = (2,0) entry.</param>
    /// <param name="d">The (1,1) entry.</param>
    /// <param name="e">The (1,2) = (2,1) entry.</param>
    /// <param name="f">The (2,2) entry.</param>
    /// <param name="rhsX">The right-hand side's first component.</param>
    /// <param name="rhsY">The right-hand side's second component.</param>
    /// <param name="rhsZ">The right-hand side's third component.</param>
    /// <param name="outputFractionShift">The non-negative number of extra bits the quotient is rounded to below the
    /// caller's own scale.</param>
    /// <param name="x">The first solution component on success; zero on refusal.</param>
    /// <param name="y">The second solution component on success; zero on refusal.</param>
    /// <param name="z">The third solution component on success; zero on refusal.</param>
    /// <returns><see langword="false"/> when the matrix is exactly singular or a result does not fit the raw
    /// carrier; every <see langword="out"/> parameter is zero in that case.</returns>
    internal static bool TrySolveSymmetric3(
        long a,
        long b,
        long c,
        long d,
        long e,
        long f,
        long rhsX,
        long rhsY,
        long rhsZ,
        int outputFractionShift,
        out long x,
        out long y,
        out long z
    ) {
        Debug.Assert(condition: (outputFractionShift >= 0), message: "TrySolveSymmetric3 requires a non-negative output fraction shift.");

        var groupMax = Max(
            FusedArithmetic.RawMagnitude(value: a), FusedArithmetic.RawMagnitude(value: b), FusedArithmetic.RawMagnitude(value: c),
            FusedArithmetic.RawMagnitude(value: d), FusedArithmetic.RawMagnitude(value: e), FusedArithmetic.RawMagnitude(value: f),
            FusedArithmetic.RawMagnitude(value: rhsX), FusedArithmetic.RawMagnitude(value: rhsY), FusedArithmetic.RawMagnitude(value: rhsZ)
        );

        if (groupMax == 0UL) {
            x = 0L;
            y = 0L;
            z = 0L;
            return false;
        }

        var shift = Symmetric3Shift(rawMagnitude: groupMax);

        // Below the lossless band (shift >= 0) the preconditioning is an exact multiplication, which preserves the
        // determinant's zero-ness exactly, so the preconditioned check below is already exact. Only the lossy
        // right-shift corner (shift < 0) needs the wider, exact raw check — see the type's remarks.
        if ((shift < 0) && RawDeterminant3IsZero(a: a, b: b, c: c, d: d, e: e, f: f)) {
            x = 0L;
            y = 0L;
            z = 0L;
            return false;
        }

        var sa = FixedVectorMath.ScaleRaw(value: a, shift: shift);
        var sb = FixedVectorMath.ScaleRaw(value: b, shift: shift);
        var sc = FixedVectorMath.ScaleRaw(value: c, shift: shift);
        var sd = FixedVectorMath.ScaleRaw(value: d, shift: shift);
        var se = FixedVectorMath.ScaleRaw(value: e, shift: shift);
        var sf = FixedVectorMath.ScaleRaw(value: f, shift: shift);
        var srx = FixedVectorMath.ScaleRaw(value: rhsX, shift: shift);
        var sry = FixedVectorMath.ScaleRaw(value: rhsY, shift: shift);
        var srz = FixedVectorMath.ScaleRaw(value: rhsZ, shift: shift);

        var det = Determinant3(a: sa, b: sb, c: sc, d: sd, e: se, f: sf);
        var adjugate = Adjugate3(a: sa, b: sb, c: sc, d: sd, e: se, f: sf);

        var nx = Accumulate(Accumulate(ScaleByRaw(term: adjugate.C11, value: srx), ScaleByRaw(term: adjugate.C12, value: sry), subtract: false), ScaleByRaw(term: adjugate.C13, value: srz), subtract: false);
        var ny = Accumulate(Accumulate(ScaleByRaw(term: adjugate.C12, value: srx), ScaleByRaw(term: adjugate.C22, value: sry), subtract: false), ScaleByRaw(term: adjugate.C23, value: srz), subtract: false);
        var nz = Accumulate(Accumulate(ScaleByRaw(term: adjugate.C13, value: srx), ScaleByRaw(term: adjugate.C23, value: sry), subtract: false), ScaleByRaw(term: adjugate.C33, value: srz), subtract: false);

        // Neither output is trusted until ALL THREE round successfully — the contract refuses with every output at
        // zero, never leaving an earlier-computed component behind when a later one overflows.
        var okX = TryFinishRatio(numerator: nx, denominator: det, fractionBitCount: outputFractionShift, result: out var rx);
        var okY = TryFinishRatio(numerator: ny, denominator: det, fractionBitCount: outputFractionShift, result: out var ry);
        var okZ = TryFinishRatio(numerator: nz, denominator: det, fractionBitCount: outputFractionShift, result: out var rz);

        if (!okX || !okY || !okZ) {
            x = 0L;
            y = 0L;
            z = 0L;
            return false;
        }

        x = rx;
        y = ry;
        z = rz;
        return true;
    }

    /// <summary>Inverts the symmetric 2×2 matrix <c>[[a,b],[b,d]]</c>, returning its three distinct entries. See the
    /// type's remarks for why Invert (unlike Solve) can refuse at a magnitude combination Solve would still answer.</summary>
    /// <param name="a">The (0,0) entry.</param>
    /// <param name="b">The (0,1) = (1,0) entry.</param>
    /// <param name="d">The (1,1) entry.</param>
    /// <param name="outputFractionShift">The non-negative number of extra bits the quotient is rounded to below the
    /// caller's own scale.</param>
    /// <param name="invA">The inverse's (0,0) entry on success; zero on refusal.</param>
    /// <param name="invB">The inverse's (0,1) = (1,0) entry on success; zero on refusal.</param>
    /// <param name="invD">The inverse's (1,1) entry on success; zero on refusal.</param>
    /// <returns><see langword="false"/> when the matrix is exactly singular, a result does not fit the raw carrier,
    /// or the entries are large enough that <paramref name="outputFractionShift"/> plus the internal preconditioning
    /// shift would be negative (see remarks); every <see langword="out"/> parameter is zero in that case.</returns>
    internal static bool TryInvertSymmetric2(long a, long b, long d, int outputFractionShift, out long invA, out long invB, out long invD) {
        Debug.Assert(condition: (outputFractionShift >= 0), message: "TryInvertSymmetric2 requires a non-negative output fraction shift.");

        var groupMax = Max(FusedArithmetic.RawMagnitude(value: a), FusedArithmetic.RawMagnitude(value: b), FusedArithmetic.RawMagnitude(value: d));

        if (groupMax == 0UL) {
            invA = 0L;
            invB = 0L;
            invD = 0L;
            return false;
        }

        // The RAW (unscaled) determinant is the sole authority on singularity — see TrySolveSymmetric2's own check
        // and the type's remarks.
        var rawDeterminant = FusedArithmetic.AddProducts(firstLeft: a, firstRight: d, secondLeft: b, secondRight: b, subtractSecond: true);

        if (rawDeterminant.Magnitude == UInt128.Zero) {
            invA = 0L;
            invB = 0L;
            invD = 0L;
            return false;
        }

        var shift = Symmetric2Shift(rawMagnitude: groupMax);
        var fractionBitCount = (outputFractionShift + shift);

        if (fractionBitCount < 0) {
            invA = 0L;
            invB = 0L;
            invD = 0L;
            return false;
        }

        var sa = FixedVectorMath.ScaleRaw(value: a, shift: shift);
        var sb = FixedVectorMath.ScaleRaw(value: b, shift: shift);
        var sd = FixedVectorMath.ScaleRaw(value: d, shift: shift);

        var det = FusedArithmetic.AddProducts(firstLeft: sa, firstRight: sd, secondLeft: sb, secondRight: sb, subtractSecond: true);

        // No output is trusted until ALL THREE round successfully — see TrySolveSymmetric2's own fix.
        var okA = TryFinishRatio(numerator: SignedPair(value: sd), denominator: det, fractionBitCount: fractionBitCount, result: out var ra);
        var okB = TryFinishRatio(numerator: NegatedSignedPair(value: sb), denominator: det, fractionBitCount: fractionBitCount, result: out var rb);
        var okD = TryFinishRatio(numerator: SignedPair(value: sa), denominator: det, fractionBitCount: fractionBitCount, result: out var rd);

        if (!okA || !okB || !okD) {
            invA = 0L;
            invB = 0L;
            invD = 0L;
            return false;
        }

        invA = ra;
        invB = rb;
        invD = rd;
        return true;
    }

    /// <summary>Inverts the symmetric 3×3 matrix <c>[[a,b,c],[b,d,e],[c,e,f]]</c>, returning its six distinct
    /// entries. See <see cref="TryInvertSymmetric2"/> and the type's remarks for the refusal envelope.</summary>
    /// <param name="a">The (0,0) entry.</param>
    /// <param name="b">The (0,1) = (1,0) entry.</param>
    /// <param name="c">The (0,2) = (2,0) entry.</param>
    /// <param name="d">The (1,1) entry.</param>
    /// <param name="e">The (1,2) = (2,1) entry.</param>
    /// <param name="f">The (2,2) entry.</param>
    /// <param name="outputFractionShift">The non-negative number of extra bits the quotient is rounded to below the
    /// caller's own scale.</param>
    /// <param name="invA">The inverse's (0,0) entry on success; zero on refusal.</param>
    /// <param name="invB">The inverse's (0,1) = (1,0) entry on success; zero on refusal.</param>
    /// <param name="invC">The inverse's (0,2) = (2,0) entry on success; zero on refusal.</param>
    /// <param name="invD">The inverse's (1,1) entry on success; zero on refusal.</param>
    /// <param name="invE">The inverse's (1,2) = (2,1) entry on success; zero on refusal.</param>
    /// <param name="invF">The inverse's (2,2) entry on success; zero on refusal.</param>
    /// <returns><see langword="false"/> under the same conditions as <see cref="TryInvertSymmetric2"/>; every
    /// <see langword="out"/> parameter is zero in that case.</returns>
    internal static bool TryInvertSymmetric3(
        long a,
        long b,
        long c,
        long d,
        long e,
        long f,
        int outputFractionShift,
        out long invA,
        out long invB,
        out long invC,
        out long invD,
        out long invE,
        out long invF
    ) {
        Debug.Assert(condition: (outputFractionShift >= 0), message: "TryInvertSymmetric3 requires a non-negative output fraction shift.");

        var groupMax = Max(
            FusedArithmetic.RawMagnitude(value: a), FusedArithmetic.RawMagnitude(value: b), FusedArithmetic.RawMagnitude(value: c),
            FusedArithmetic.RawMagnitude(value: d), FusedArithmetic.RawMagnitude(value: e), FusedArithmetic.RawMagnitude(value: f)
        );

        if (groupMax == 0UL) {
            invA = 0L;
            invB = 0L;
            invC = 0L;
            invD = 0L;
            invE = 0L;
            invF = 0L;
            return false;
        }

        var shift = Symmetric3Shift(rawMagnitude: groupMax);

        // Below the lossless band (shift >= 0) the preconditioned determinant's zero-ness is already exact — see
        // TrySolveSymmetric3's own check and the type's remarks.
        if ((shift < 0) && RawDeterminant3IsZero(a: a, b: b, c: c, d: d, e: e, f: f)) {
            invA = 0L;
            invB = 0L;
            invC = 0L;
            invD = 0L;
            invE = 0L;
            invF = 0L;
            return false;
        }

        var fractionBitCount = (outputFractionShift + shift);

        if (fractionBitCount < 0) {
            invA = 0L;
            invB = 0L;
            invC = 0L;
            invD = 0L;
            invE = 0L;
            invF = 0L;
            return false;
        }

        var sa = FixedVectorMath.ScaleRaw(value: a, shift: shift);
        var sb = FixedVectorMath.ScaleRaw(value: b, shift: shift);
        var sc = FixedVectorMath.ScaleRaw(value: c, shift: shift);
        var sd = FixedVectorMath.ScaleRaw(value: d, shift: shift);
        var se = FixedVectorMath.ScaleRaw(value: e, shift: shift);
        var sf = FixedVectorMath.ScaleRaw(value: f, shift: shift);

        var det = Determinant3(a: sa, b: sb, c: sc, d: sd, e: se, f: sf);
        var adjugate = Adjugate3(a: sa, b: sb, c: sc, d: sd, e: se, f: sf);

        // No output is trusted until ALL SIX round successfully — see TrySolveSymmetric2's own fix.
        var okA = TryFinishRatio(numerator: adjugate.C11, denominator: det, fractionBitCount: fractionBitCount, result: out var ra);
        var okB = TryFinishRatio(numerator: adjugate.C12, denominator: det, fractionBitCount: fractionBitCount, result: out var rb);
        var okC = TryFinishRatio(numerator: adjugate.C13, denominator: det, fractionBitCount: fractionBitCount, result: out var rc);
        var okD = TryFinishRatio(numerator: adjugate.C22, denominator: det, fractionBitCount: fractionBitCount, result: out var rd);
        var okE = TryFinishRatio(numerator: adjugate.C23, denominator: det, fractionBitCount: fractionBitCount, result: out var re);
        var okF = TryFinishRatio(numerator: adjugate.C33, denominator: det, fractionBitCount: fractionBitCount, result: out var rf);

        if (!okA || !okB || !okC || !okD || !okE || !okF) {
            invA = 0L;
            invB = 0L;
            invC = 0L;
            invD = 0L;
            invE = 0L;
            invF = 0L;
            return false;
        }

        invA = ra;
        invB = rb;
        invC = rc;
        invD = rd;
        invE = re;
        invF = rf;
        return true;
    }

    /// <summary>Applies the symmetric 2×2 matrix <c>[[a,b],[b,d]]</c> to the vector <c>(vX,vY)</c>, rounding each
    /// component to <paramref name="fractionBitsOut"/> exactly once.</summary>
    /// <param name="a">The (0,0) entry.</param>
    /// <param name="b">The (0,1) = (1,0) entry.</param>
    /// <param name="d">The (1,1) entry.</param>
    /// <param name="vX">The vector's first component.</param>
    /// <param name="vY">The vector's second component.</param>
    /// <param name="fractionBitsMatrix">The matrix entries' fraction bit count.</param>
    /// <param name="fractionBitsVector">The vector components' fraction bit count.</param>
    /// <param name="fractionBitsOut">The result components' fraction bit count.</param>
    /// <param name="x">The first result component on success; zero on refusal.</param>
    /// <param name="y">The second result component on success; zero on refusal.</param>
    /// <returns><see langword="false"/> when either correctly rounded component does not fit the signed 64-bit raw;
    /// both <see langword="out"/> parameters are zero in that case.</returns>
    /// <remarks>No preconditioning, and none is needed: each component is a sum of two raw products, bounded by
    /// <c>2·2^126 = 2^127</c>, which the sign-plus-<see cref="UInt128"/> accumulator holds exactly — the growth this
    /// type's determinants precondition against is the triple product's, not a matrix-times-vector's. The two scales
    /// are independent because the motivating caller's are: an inverse inertia sits at a resolution-leaning scale and
    /// an angular impulse at a range-leaning one.</remarks>
    internal static bool TryApplySymmetric2(
        long a,
        long b,
        long d,
        long vX,
        long vY,
        int fractionBitsMatrix,
        int fractionBitsVector,
        int fractionBitsOut,
        out long x,
        out long y
    ) {
        var shift = FusedArithmetic.MixedScaleShift(fractionBitsOut: fractionBitsOut, first: fractionBitsMatrix, second: fractionBitsVector);
        var nx = FusedArithmetic.AddProducts(firstLeft: a, firstRight: vX, secondLeft: b, secondRight: vY);
        var ny = FusedArithmetic.AddProducts(firstLeft: b, firstRight: vX, secondLeft: d, secondRight: vY);

        // No output is trusted until BOTH round successfully — the same all-or-nothing contract Solve and Invert carry.
        var okX = TryFinishScaled(value: nx, shift: shift, result: out var rx);
        var okY = TryFinishScaled(value: ny, shift: shift, result: out var ry);

        if (!okX || !okY) {
            x = 0L;
            y = 0L;
            return false;
        }

        x = rx;
        y = ry;
        return true;
    }

    /// <summary>Applies the symmetric 3×3 matrix <c>[[a,b,c],[b,d,e],[c,e,f]]</c> to the vector <c>(vX,vY,vZ)</c>,
    /// rounding each component exactly once. See <see cref="TryApplySymmetric2"/> for the shared contract; each
    /// component here is a sum of three raw products, bounded by <c>3·2^126</c>, still inside
    /// <see cref="UInt128"/>.</summary>
    /// <param name="a">The (0,0) entry.</param>
    /// <param name="b">The (0,1) = (1,0) entry.</param>
    /// <param name="c">The (0,2) = (2,0) entry.</param>
    /// <param name="d">The (1,1) entry.</param>
    /// <param name="e">The (1,2) = (2,1) entry.</param>
    /// <param name="f">The (2,2) entry.</param>
    /// <param name="vX">The vector's first component.</param>
    /// <param name="vY">The vector's second component.</param>
    /// <param name="vZ">The vector's third component.</param>
    /// <param name="fractionBitsMatrix">The matrix entries' fraction bit count.</param>
    /// <param name="fractionBitsVector">The vector components' fraction bit count.</param>
    /// <param name="fractionBitsOut">The result components' fraction bit count.</param>
    /// <param name="x">The first result component on success; zero on refusal.</param>
    /// <param name="y">The second result component on success; zero on refusal.</param>
    /// <param name="z">The third result component on success; zero on refusal.</param>
    /// <returns><see langword="false"/> when any correctly rounded component does not fit the signed 64-bit raw; every
    /// <see langword="out"/> parameter is zero in that case.</returns>
    internal static bool TryApplySymmetric3(
        long a,
        long b,
        long c,
        long d,
        long e,
        long f,
        long vX,
        long vY,
        long vZ,
        int fractionBitsMatrix,
        int fractionBitsVector,
        int fractionBitsOut,
        out long x,
        out long y,
        out long z
    ) {
        var shift = FusedArithmetic.MixedScaleShift(fractionBitsOut: fractionBitsOut, first: fractionBitsMatrix, second: fractionBitsVector);
        var nx = Accumulate(accumulator: FusedArithmetic.AddProducts(firstLeft: a, firstRight: vX, secondLeft: b, secondRight: vY), term: FusedArithmetic.Product(left: c, right: vZ), subtract: false);
        var ny = Accumulate(accumulator: FusedArithmetic.AddProducts(firstLeft: b, firstRight: vX, secondLeft: d, secondRight: vY), term: FusedArithmetic.Product(left: e, right: vZ), subtract: false);
        var nz = Accumulate(accumulator: FusedArithmetic.AddProducts(firstLeft: c, firstRight: vX, secondLeft: e, secondRight: vY), term: FusedArithmetic.Product(left: f, right: vZ), subtract: false);

        // See TryApplySymmetric2's own note: no output is trusted until all three round successfully.
        var okX = TryFinishScaled(value: nx, shift: shift, result: out var rx);
        var okY = TryFinishScaled(value: ny, shift: shift, result: out var ry);
        var okZ = TryFinishScaled(value: nz, shift: shift, result: out var rz);

        if (!okX || !okY || !okZ) {
            x = 0L;
            y = 0L;
            z = 0L;
            return false;
        }

        x = rx;
        y = ry;
        z = rz;
        return true;
    }

    private static int Symmetric2Shift(ulong rawMagnitude) =>
        (Symmetric2TargetLeadingBit - (63 - BitOperations.LeadingZeroCount(value: rawMagnitude)));
    private static int Symmetric3Shift(ulong rawMagnitude) =>
        (Symmetric3TargetLeadingBit - (63 - BitOperations.LeadingZeroCount(value: rawMagnitude)));

    private static ulong Max(ulong v0, ulong v1, ulong v2) =>
        Math.Max(val1: v0, val2: Math.Max(val1: v1, val2: v2));
    private static ulong Max(ulong v0, ulong v1, ulong v2, ulong v3) =>
        Math.Max(val1: Math.Max(val1: v0, val2: v1), val2: Math.Max(val1: v2, val2: v3));
    private static ulong Max(ulong v0, ulong v1, ulong v2, ulong v3, ulong v4) =>
        Math.Max(val1: Max(v0: v0, v1: v1, v2: v2, v3: v3), val2: v4);
    private static ulong Max(ulong v0, ulong v1, ulong v2, ulong v3, ulong v4, ulong v5) =>
        Math.Max(val1: Max(v0: v0, v1: v1, v2: v2), val2: Max(v0: v3, v1: v4, v2: v5));
    private static ulong Max(ulong v0, ulong v1, ulong v2, ulong v3, ulong v4, ulong v5, ulong v6, ulong v7, ulong v8) =>
        Math.Max(val1: Max(v0: v0, v1: v1, v2: v2, v3: v3, v4: v4), val2: Max(v0: v5, v1: v6, v2: v7, v3: v8));

    /// <summary>The exact zero-ness of the 3×3 symmetric determinant <c>adf − ae² − b²f + 2bce − c²d</c> at the
    /// caller's own raw entries, before any preconditioning. Six triple products of full-width <see cref="long"/>
    /// factors reach roughly <c>2^189</c> in the worst case — far past what the <see cref="UInt128"/>-magnitude
    /// scheme <see cref="TripleProduct"/> uses for the (already-preconditioned, at most <c>2^3k</c>) budget this
    /// type's other kernels stay inside — so this one check alone widens to <see cref="BigInteger"/>. Called only
    /// from the rare lossy-right-shift corner (see the type's remarks); the common, realistic-scale path never
    /// reaches it.</summary>
    private static bool RawDeterminant3IsZero(long a, long b, long c, long d, long e, long f) {
        BigInteger ba = a, bb = b, bc = c, bd = d, be = e, bf = f;
        var determinant = ((((ba * bd * bf) - (ba * be * be)) - (bb * bb * bf)) + (2 * bb * bc * be) - (bc * bc * bd));

        return determinant.IsZero;
    }

    /// <summary>The exact triple product <c>left·middle·right</c> as sign plus <see cref="UInt128"/> magnitude. The
    /// first pairing forms an ordinary raw product (<see cref="FusedArithmetic.Product"/>, magnitude at most
    /// <c>2^2k</c> for this family's preconditioned operands); widening that intermediate's UInt128 magnitude by the
    /// third raw factor is exact whenever the true product fits <see cref="UInt128"/> — true here by the type's own
    /// budget (<c>2^3k</c>, comfortably under <c>2^128</c>) even though .NET's <see cref="UInt128"/> multiply itself
    /// only guarantees a correct truncated-to-128-bit result in general.</summary>
    private static (bool Negative, UInt128 Magnitude) TripleProduct(long left, long middle, long right) {
        var pair = FusedArithmetic.Product(left: left, right: middle);
        var magnitude = (pair.Magnitude * (UInt128)FusedArithmetic.RawMagnitude(value: right));

        return ((pair.Negative ^ (right < 0L)), magnitude);
    }

    /// <summary>Adds (or subtracts) an already-formed signed magnitude term into a running accumulator — the one
    /// sign-magnitude combine every multi-term sum in this file funnels through, mirroring
    /// <see cref="FusedArithmetic.CombineSigned"/>'s own role for <see cref="FusedArithmetic.AddProducts"/>.</summary>
    private static (bool Negative, UInt128 Magnitude) Accumulate((bool Negative, UInt128 Magnitude) accumulator, (bool Negative, UInt128 Magnitude) term, bool subtract) =>
        FusedArithmetic.CombineSigned(firstNegative: accumulator.Negative, firstMagnitude: accumulator.Magnitude, secondNegative: (term.Negative ^ subtract), secondMagnitude: term.Magnitude);

    /// <summary>Scales an already-formed signed magnitude term (a 2×2 cofactor) by one more raw factor (a
    /// right-hand-side component) — the adjugate-times-vector step of Cramer's rule, exact by the same widening
    /// argument as <see cref="TripleProduct"/>.</summary>
    private static (bool Negative, UInt128 Magnitude) ScaleByRaw((bool Negative, UInt128 Magnitude) term, long value) =>
        ((term.Negative ^ (value < 0L)), (term.Magnitude * (UInt128)FusedArithmetic.RawMagnitude(value: value)));

    private static (bool Negative, UInt128 Magnitude) SignedPair(long value) =>
        ((value < 0L), (UInt128)FusedArithmetic.RawMagnitude(value: value));
    private static (bool Negative, UInt128 Magnitude) NegatedSignedPair(long value) =>
        ((value > 0L), (UInt128)FusedArithmetic.RawMagnitude(value: value));

    /// <summary>The 3×3 symmetric determinant <c>adf − ae² − b²f + 2bce − c²d</c> as six exact signed triple
    /// products folded through one running <see cref="Accumulate"/> chain — see the type's remarks for the bit
    /// budget this six-term sum is sized against.</summary>
    private static (bool Negative, UInt128 Magnitude) Determinant3(long a, long b, long c, long d, long e, long f) {
        var det = TripleProduct(left: a, middle: d, right: f);

        det = Accumulate(accumulator: det, term: TripleProduct(left: a, middle: e, right: e), subtract: true);
        det = Accumulate(accumulator: det, term: TripleProduct(left: b, middle: b, right: f), subtract: true);

        var bce = TripleProduct(left: b, middle: c, right: e);

        det = Accumulate(accumulator: det, term: (bce.Negative, (bce.Magnitude << 1)), subtract: false);
        det = Accumulate(accumulator: det, term: TripleProduct(left: c, middle: c, right: d), subtract: true);

        return det;
    }

    /// <summary>The symmetric 3×3 adjugate's six distinct cofactors, each an exact two-term product difference
    /// formed directly by <see cref="FusedArithmetic.AddProducts"/> (magnitude at most <c>2^(2k+1)</c> for this
    /// family's preconditioned operands — comfortably inside the six-term determinant's own, larger budget).</summary>
    private static (
        (bool Negative, UInt128 Magnitude) C11,
        (bool Negative, UInt128 Magnitude) C12,
        (bool Negative, UInt128 Magnitude) C13,
        (bool Negative, UInt128 Magnitude) C22,
        (bool Negative, UInt128 Magnitude) C23,
        (bool Negative, UInt128 Magnitude) C33
    ) Adjugate3(long a, long b, long c, long d, long e, long f) => (
        C11: FusedArithmetic.AddProducts(firstLeft: d, firstRight: f, secondLeft: e, secondRight: e, subtractSecond: true),
        C12: FusedArithmetic.AddProducts(firstLeft: c, firstRight: e, secondLeft: b, secondRight: f, subtractSecond: true),
        C13: FusedArithmetic.AddProducts(firstLeft: b, firstRight: e, secondLeft: c, secondRight: d, subtractSecond: true),
        C22: FusedArithmetic.AddProducts(firstLeft: a, firstRight: f, secondLeft: c, secondRight: c, subtractSecond: true),
        C23: FusedArithmetic.AddProducts(firstLeft: b, firstRight: c, secondLeft: a, secondRight: e, subtractSecond: true),
        C33: FusedArithmetic.AddProducts(firstLeft: a, firstRight: d, secondLeft: b, secondRight: b, subtractSecond: true)
    );

    /// <summary>Rounds a signed <c>numerator / denominator</c> ratio to <paramref name="fractionBitCount"/> extra
    /// bits exactly once (<see cref="FusedArithmetic.TryDivideMagnitudeRounded"/>), then refuses rather than
    /// narrowing silently when the rounded magnitude does not fit the signed 64-bit result — the one place every
    /// solve and invert output in this type crosses back from the wide sign-magnitude world to a raw
    /// <see langword="long"/>.</summary>
    private static bool TryFinishRatio(
        (bool Negative, UInt128 Magnitude) numerator,
        (bool Negative, UInt128 Magnitude) denominator,
        int fractionBitCount,
        out long result
    ) {
        if (!FusedArithmetic.TryDivideMagnitudeRounded(numeratorMagnitude: numerator.Magnitude, denominatorMagnitude: denominator.Magnitude, fractionBitCount: fractionBitCount, quotient: out var magnitude)) {
            result = 0L;
            return false;
        }

        return FusedArithmetic.TryNarrowSignedMagnitude(negative: (numerator.Negative ^ denominator.Negative), magnitude: magnitude, result: out result);
    }

    /// <summary>Rounds an already-accumulated signed magnitude by one power of two exactly once
    /// (<see cref="FusedArithmetic.ScaleMagnitudeToNearest"/>), then refuses rather than narrowing silently — the
    /// apply family's counterpart to <see cref="TryFinishRatio"/>, which finishes a ratio rather than a scaling.</summary>
    private static bool TryFinishScaled((bool Negative, UInt128 Magnitude) value, long shift, out long result) {
        var scaled = FusedArithmetic.ScaleMagnitudeToNearest(magnitude: value.Magnitude, shift: shift);

        if (scaled.Overflowed) {
            result = 0L;
            return false;
        }

        return FusedArithmetic.TryNarrowSignedMagnitude(negative: value.Negative, magnitude: scaled.Magnitude, result: out result);
    }
}
