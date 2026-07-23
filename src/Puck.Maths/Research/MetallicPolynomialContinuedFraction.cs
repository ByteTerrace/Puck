using System.Numerics;

namespace Puck.Maths;

/// <summary>
/// Exact integer observations of the metallic polynomial continued fraction. For positive integers <c>k</c> and
/// <c>n</c>, let <c>sₙ</c> be the unique positive tail satisfying <c>sₙ = k·n − 1 + n²/sₙ₊₁</c>. Its floor is a
/// quadratic-irrational Beatty term, so it can be evaluated directly without choosing a truncation depth.
/// </summary>
/// <remarks>
/// The returned integer is exact. Evaluation uses arbitrary-width integer arithmetic and an integer square root; it
/// never constructs <c>sₙ</c>, rounds a floating-point metallic mean, or iterates the infinite continued fraction.
/// Consecutive calls provide random access to the associated integer sequence: subtract
/// <c>TailFloor(k, n)</c> from <c>TailFloor(k, n + 1)</c>.
/// </remarks>
public static class MetallicPolynomialContinuedFraction {
    /// <summary>Returns the general positive-tail analysis specialized to <c>(p,q,r,u,v)=(k,−1,1,0,0)</c>.</summary>
    /// <param name="metallicIndex">The positive integer <c>k</c>.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="metallicIndex"/> is not positive.</exception>
    public static PolynomialContinuedFractionAnalysis Analyze(BigInteger metallicIndex) {
        if (metallicIndex <= BigInteger.Zero) {
            throw new ArgumentOutOfRangeException(
                paramName: nameof(metallicIndex),
                message: "the metallic index must be positive"
            );
        }

        return PolynomialContinuedFractionTail.Analyze(
            linear: metallicIndex,
            constant: -1,
            numeratorQuadratic: 1,
            numeratorLinear: 0,
            numeratorConstant: 0
        );
    }

    /// <summary>Returns <c>⌊sₙ⌋</c> for the positive metallic index <paramref name="metallicIndex"/> and tail index <paramref name="tailIndex"/>.</summary>
    /// <param name="metallicIndex">The positive integer <c>k</c> in the tail recurrence.</param>
    /// <param name="tailIndex">The positive integer <c>n</c> selecting the tail.</param>
    /// <returns>The exact floor of the infinite tail. For <see cref="int"/> inputs the result always fits in <see cref="long"/>.</returns>
    /// <exception cref="ArgumentOutOfRangeException">An input is not positive.</exception>
    public static long TailFloor(int metallicIndex, int tailIndex) =>
        checked((long)TailFloor(
            metallicIndex: new BigInteger(value: metallicIndex),
            tailIndex: new BigInteger(value: tailIndex)
        ));

    /// <summary>Returns <c>⌊sₙ⌋</c> for arbitrary-width positive integer indices.</summary>
    /// <param name="metallicIndex">The positive integer <c>k</c> in the tail recurrence.</param>
    /// <param name="tailIndex">The positive integer <c>n</c> selecting the tail.</param>
    /// <returns>The exact floor of the infinite tail.</returns>
    /// <exception cref="ArgumentOutOfRangeException">An input is not positive.</exception>
    public static BigInteger TailFloor(BigInteger metallicIndex, BigInteger tailIndex) {
        if (metallicIndex <= BigInteger.Zero) {
            throw new ArgumentOutOfRangeException(
                paramName: nameof(metallicIndex),
                message: "the metallic index must be positive"
            );
        }
        if (tailIndex <= BigInteger.Zero) {
            throw new ArgumentOutOfRangeException(
                paramName: nameof(tailIndex),
                message: "the tail index must be positive"
            );
        }

        // The theorem gives floor(s_n) = floor(x_n), where
        //   x_n = alpha*n - (1+alpha)/sqrt(K), alpha=(k+sqrt(K))/2, K=k^2+4.
        // Put q=K*n-k-2. Then x_n=(A + q*sqrt(K))/(2K), A=q*k+2(k-2). Folding q into the
        // radicand leaves one irrational square root whose fractional part is below one.
        var discriminant = ((metallicIndex * metallicIndex) + 4);
        var surdCoefficient = ((discriminant * tailIndex) - metallicIndex - 2);
        var rational = ((surdCoefficient * metallicIndex) + (2 * (metallicIndex - 2)));
        var denominator = (2 * discriminant);
        var radicand = ((surdCoefficient * surdCoefficient) * discriminant);
        var rootFloor = BigIntegerFunctions.SquareRoot(value: radicand);
        var candidate = BigIntegerFunctions.FloorDivide(
            numerator: (rational + rootFloor),
            denominator: denominator
        );

        // Replacing the irrational root by its integer floor can lower the final floor by at most one. Decide that
        // single boundary exactly by squaring; K=k^2+4 is never a square for positive k, so equality cannot occur.
        var nextThreshold = (((candidate + 1) * denominator) - rational);

        return ((nextThreshold <= 0) || ((nextThreshold * nextThreshold) < radicand))
            ? (candidate + 1)
            : candidate;
    }
}
