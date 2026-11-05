using System.Numerics;

namespace Puck.Maths;

/// <summary>
/// The scale-free general (not necessarily symmetric) 3×3 linear solve <c>M·x = rhs</c> by Cramer's rule, for the
/// systems a deterministic solver forms whose matrix has no symmetry to exploit: the Jacobian of an implicit
/// gyroscopic step, <c>I + h(skew(ω)·I − skew(I·ω))</c>, is the case that motivated it. It shares
/// <see cref="FixedSymmetricSolve"/>'s whole contract and machinery (one common power-of-two preconditioning of every
/// operand, exact accumulation in sign-plus-<see cref="UInt128"/> magnitude, one rounding per returned component, and
/// a refusal with every output zero), and differs only in reading nine independent entries instead of six.
/// </summary>
/// <remarks>
/// <para><b>The bit budget.</b> The determinant is formed by expansion along the first row,
/// <c>m00·C00 + m01·C01 + m02·C02</c>, each cofactor an exact two-term product difference. With every preconditioned
/// operand bounded by <c>2^k</c>, a cofactor is bounded by <c>2^(2k+1)</c>, each of the three terms by
/// <c>2^(3k+1)</c> and their sum by <c>3·2^(3k+1) &lt; 2^(3k+3)</c>; each numerator component (a cofactor column
/// dotted with the right-hand side) has the same three-term shape and bound. That is the symmetric 3×3 family's own
/// budget (<c>3k + 3 &lt; 127</c>, <c>k ≤ 41</c>), so this solve preconditions to the same target bit
/// (<see cref="FixedSymmetricSolve.Symmetric3TargetLeadingBit"/>) and shares its scale-invariance argument: the
/// determinant is degree 3 in the entries, the adjugate degree 2 and the right-hand side degree 1, so one shared
/// lossless shift cancels exactly in the ratio, and a lossy right shift (an operand already at or past the target)
/// leaves the answer within that one preconditioning rounding.</para>
/// <para><b>Singularity.</b> Decided against the caller's own raw entries: whenever the shared shift is non-negative
/// the preconditioning is an exact multiplication that preserves the determinant's zero-ness, and in the lossy corner
/// an exact <see cref="BigInteger"/> raw determinant decides it, so a rounded right shift can never turn an exactly
/// singular matrix into a fabricated finite answer.</para>
/// </remarks>
public static class FixedLinearSolve {
    /// <summary>Solves the general 3×3 system <c>[[m00,m01,m02],[m10,m11,m12],[m20,m21,m22]]·(x,y,z) =
    /// (rhsX,rhsY,rhsZ)</c> for raw operands at any shared caller scale, rounding each component once (to nearest,
    /// ties to even) to <paramref name="outputFractionShift"/> extra bits below that scale.</summary>
    /// <param name="m00">Row 0, column 0.</param>
    /// <param name="m01">Row 0, column 1.</param>
    /// <param name="m02">Row 0, column 2.</param>
    /// <param name="m10">Row 1, column 0.</param>
    /// <param name="m11">Row 1, column 1.</param>
    /// <param name="m12">Row 1, column 2.</param>
    /// <param name="m20">Row 2, column 0.</param>
    /// <param name="m21">Row 2, column 1.</param>
    /// <param name="m22">Row 2, column 2.</param>
    /// <param name="rhsX">The right-hand side's first component.</param>
    /// <param name="rhsY">The right-hand side's second component.</param>
    /// <param name="rhsZ">The right-hand side's third component.</param>
    /// <param name="outputFractionShift">The non-negative number of extra bits the quotient is rounded to below the
    /// caller's own scale.</param>
    /// <param name="x">The first solution component on success; zero on refusal.</param>
    /// <param name="y">The second solution component on success; zero on refusal.</param>
    /// <param name="z">The third solution component on success; zero on refusal.</param>
    /// <returns><see langword="false"/> when <paramref name="outputFractionShift"/> is negative, every operand is zero,
    /// the matrix is exactly singular or a result does not fit the raw carrier; every <see langword="out"/> parameter
    /// is zero in that case.</returns>
    public static bool TrySolve3(
        long m00,
        long m01,
        long m02,
        long m10,
        long m11,
        long m12,
        long m20,
        long m21,
        long m22,
        long rhsX,
        long rhsY,
        long rhsZ,
        int outputFractionShift,
        out long x,
        out long y,
        out long z
    ) {
        x = 0L;
        y = 0L;
        z = 0L;

        if (outputFractionShift < 0) {
            return false;
        }

        var groupMax = 0UL;

        foreach (var operand in (ReadOnlySpan<long>)[m00, m01, m02, m10, m11, m12, m20, m21, m22, rhsX, rhsY, rhsZ]) {
            groupMax = Math.Max(
                val1: groupMax,
                val2: FusedArithmetic.RawMagnitude(value: operand)
            );
        }

        if (groupMax == 0UL) {
            return false;
        }

        var shift = FixedSymmetricSolve.Symmetric3Shift(rawMagnitude: groupMax);

        if (
            (shift < 0) &&
            RawDeterminantIsZero(
            m00: m00,
            m01: m01,
            m02: m02,
            m10: m10,
            m11: m11,
            m12: m12,
            m20: m20,
            m21: m21,
            m22: m22
        )
        ) {
            return false;
        }

        var a00 = FixedVectorMath.ScaleRaw(shift: shift, value: m00);
        var a01 = FixedVectorMath.ScaleRaw(shift: shift, value: m01);
        var a02 = FixedVectorMath.ScaleRaw(shift: shift, value: m02);
        var a10 = FixedVectorMath.ScaleRaw(shift: shift, value: m10);
        var a11 = FixedVectorMath.ScaleRaw(shift: shift, value: m11);
        var a12 = FixedVectorMath.ScaleRaw(shift: shift, value: m12);
        var a20 = FixedVectorMath.ScaleRaw(shift: shift, value: m20);
        var a21 = FixedVectorMath.ScaleRaw(shift: shift, value: m21);
        var a22 = FixedVectorMath.ScaleRaw(shift: shift, value: m22);
        var r0 = FixedVectorMath.ScaleRaw(shift: shift, value: rhsX);
        var r1 = FixedVectorMath.ScaleRaw(shift: shift, value: rhsY);
        var r2 = FixedVectorMath.ScaleRaw(shift: shift, value: rhsZ);

        // Cofactor Cij = (-1)^(i+j) · minor(i, j); the adjugate is the cofactor matrix's transpose, so component k of
        // the solution is (C0k·r0 + C1k·r1 + C2k·r2) / det.
        var c00 = Cofactor(p: a11, q: a22, r: a12, s: a21);
        var c01 = Cofactor(p: a12, q: a20, r: a10, s: a22);
        var c02 = Cofactor(p: a10, q: a21, r: a11, s: a20);
        var c10 = Cofactor(p: a02, q: a21, r: a01, s: a22);
        var c11 = Cofactor(p: a00, q: a22, r: a02, s: a20);
        var c12 = Cofactor(p: a01, q: a20, r: a00, s: a21);
        var c20 = Cofactor(p: a01, q: a12, r: a02, s: a11);
        var c21 = Cofactor(p: a02, q: a10, r: a00, s: a12);
        var c22 = Cofactor(p: a00, q: a11, r: a01, s: a10);
        var det = Dot(c0: c00, v0: a00, c1: c01, v1: a01, c2: c02, v2: a02);
        var nx = Dot(c0: c00, v0: r0, c1: c10, v1: r1, c2: c20, v2: r2);
        var ny = Dot(c0: c01, v0: r0, c1: c11, v1: r1, c2: c21, v2: r2);
        var nz = Dot(c0: c02, v0: r0, c1: c12, v1: r1, c2: c22, v2: r2);

        // No output is trusted until all three round; a later overflow never leaves an earlier component behind.
        if (
            !FixedSymmetricSolve.TryFinishRatio(denominator: det, fractionBitCount: outputFractionShift, numerator: nx, result: out var rx) ||
            !FixedSymmetricSolve.TryFinishRatio(denominator: det, fractionBitCount: outputFractionShift, numerator: ny, result: out var ry) ||
            !FixedSymmetricSolve.TryFinishRatio(denominator: det, fractionBitCount: outputFractionShift, numerator: nz, result: out var rz)
        ) {
            return false;
        }

        x = rx;
        y = ry;
        z = rz;
        return true;
    }

    // p·q − r·s, exact.
    private static (bool Negative, UInt128 Magnitude) Cofactor(long p, long q, long r, long s) =>
        FusedArithmetic.AddProducts(
            firstLeft: p,
            firstRight: q,
            secondLeft: r,
            secondRight: s,
            subtractSecond: true
        );
    // c0·v0 + c1·v1 + c2·v2, exact inside the type's bit budget.
    private static (bool Negative, UInt128 Magnitude) Dot(
        (bool Negative, UInt128 Magnitude) c0,
        long v0,
        (bool Negative, UInt128 Magnitude) c1,
        long v1,
        (bool Negative, UInt128 Magnitude) c2,
        long v2
    ) =>
        FixedSymmetricSolve.Accumulate(
            accumulator: FixedSymmetricSolve.Accumulate(
                accumulator: FixedSymmetricSolve.ScaleByRaw(term: c0, value: v0),
                subtract: false,
                term: FixedSymmetricSolve.ScaleByRaw(term: c1, value: v1)
            ),
            subtract: false,
            term: FixedSymmetricSolve.ScaleByRaw(term: c2, value: v2)
        );
    // The exact zero-ness of the raw determinant, before preconditioning: full-width long entries reach about 2^189,
    // past the UInt128 scheme, so this one check widens to BigInteger. Reached only from the lossy-shift corner.
    private static bool RawDeterminantIsZero(long m00, long m01, long m02, long m10, long m11, long m12, long m20, long m21, long m22) {
        BigInteger b00 = m00, b01 = m01, b02 = m02, b10 = m10, b11 = m11, b12 = m12, b20 = m20, b21 = m21, b22 = m22;
        var determinant = (((b00 * ((b11 * b22) - (b12 * b21))) - (b01 * ((b10 * b22) - (b12 * b20)))) + (b02 * ((b10 * b21) - (b11 * b20))));

        return determinant.IsZero;
    }
}
