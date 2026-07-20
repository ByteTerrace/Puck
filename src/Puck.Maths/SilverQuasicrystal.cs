namespace Puck.Maths;

/// <summary>
/// An infinite, aperiodic, never-repeating point set — a quasicrystal — built by the cut-and-project method from the
/// silver-ratio ring <c>ℤ[√2]</c>. This is the one-dimensional silver-mean case (the Pell chain): the points of
/// <c>ℤ[√2]</c> whose Galois conjugate falls inside a length-two window, which along the line space into exactly two
/// tile lengths in the ratio <c>δ = 1 + √2</c>, arranged as the silver-mean word. Membership and traversal are exact
/// integer arithmetic, so the structure never drifts and never repeats — the same region regenerates identically on
/// every machine with no stored data.
/// </summary>
/// <remarks>
/// A point is the ring element <c>a + b·√2</c>, addressed by the integer pair <c>(a, b)</c>. <see cref="Contains(int, int)"/>
/// is the exact acceptance test; <see cref="Next(int, int)"/> and <see cref="Previous(int, int)"/> walk the chain in
/// physical order in O(1) (each step adds √2 or 2 + √2 in the ring); <see cref="StartsLongTile(int, int)"/> reports which
/// of the two tiles begins at a point; and <see cref="Position(int, int)"/> gives the fixed-point coordinate on the line
/// (the one approximate value — the combinatorial structure above it is exact). The silver ratio here is the same
/// <c>δ = 1 + √2</c> that codes the all-twos continued fraction of <see cref="ContinuedFraction"/>. This is the eight-fold
/// sibling of <see cref="GoldenQuasicrystal"/> (the golden Fibonacci chain); the same construction in higher dimensions
/// yields the Ammann–Beenker (8-fold) tiling, and this is its line.
/// </remarks>
public static class SilverQuasicrystal {
    // round(√2 · 2^16): the square root of two in Q48.16, for the fixed-point physical position only.
    private const long SquareRootTwoRawQ16 = 92682L;

    /// <summary>Determines whether the ring element <c>a + b·√2</c> is a point of the quasicrystal.</summary>
    /// <param name="a">The integer part of the ring element.</param>
    /// <param name="b">The coefficient of √2.</param>
    /// <returns><see langword="true"/> when the element's conjugate lies in the acceptance window <c>[0, 2)</c>.</returns>
    public static bool Contains(int a, int b) =>
        // Accept when the Galois conjugate a − b·√2 lies in [0, 2): the projection into internal space hits the window.
        ((SignSqrt2(rational: a, coefficient: -b) >= 0) && (SignSqrt2(rational: (a - 2), coefficient: -b) < 0));
    /// <summary>Determines whether the longer of the two tiles begins at the point <c>a + b·√2</c>.</summary>
    /// <param name="a">The integer part of the point.</param>
    /// <param name="b">The coefficient of √2.</param>
    /// <returns><see langword="true"/> when the tile starting here has length 2 + √2 (the long tile); otherwise it has length √2.</returns>
    public static bool StartsLongTile(int a, int b) =>
        // The long tile starts when the conjugate is below √2, so the next accepted point is 2 + √2 further along.
        (SignSqrt2(rational: a, coefficient: (-b - 1)) < 0);
    /// <summary>Returns the next point along the line — the far end of the tile that starts at <c>a + b·√2</c>.</summary>
    /// <param name="a">The integer part of a point of the quasicrystal.</param>
    /// <param name="b">The coefficient of √2.</param>
    /// <returns>The next point, reached by adding 2 + √2 (a long tile) or √2 (a short tile); itself a point of the quasicrystal.</returns>
    public static (int A, int B) Next(int a, int b) =>
        (StartsLongTile(a: a, b: b) ? (a + 2, b + 1) : (a, b + 1));
    /// <summary>Returns the previous point along the line — the near end of the tile that ends at <c>a + b·√2</c>.</summary>
    /// <param name="a">The integer part of a point of the quasicrystal.</param>
    /// <param name="b">The coefficient of √2.</param>
    /// <returns>The preceding point of the quasicrystal.</returns>
    public static (int A, int B) Previous(int a, int b) =>
        // Of the two candidate predecessors, the true one is a point of the quasicrystal that steps forward to (a, b);
        // a non-member can also step here, so membership must be checked, not just the forward step.
        ((Contains(a: (a - 2), b: (b - 1)) && (Next(a: (a - 2), b: (b - 1)) == (a, b))) ? (a - 2, b - 1) : (a, b - 1));
    /// <summary>Returns the position of the point <c>a + b·√2</c> along the line.</summary>
    /// <param name="a">The integer part of the point.</param>
    /// <param name="b">The coefficient of √2.</param>
    /// <returns>The fixed-point coordinate <c>a + b·√2</c> — the one approximate value; membership and traversal are exact.</returns>
    public static FixedQ4816 Position(int a, int b) =>
        (FixedQ4816.FromInteger(value: a) + (FixedQ4816.FromInteger(value: b) * FixedQ4816.FromRawBits(value: SquareRootTwoRawQ16)));

    /// <summary>Returns the sign of the real number <c>rational + coefficient·√2</c>, exactly, by integer arithmetic.</summary>
    /// <param name="rational">The integer term.</param>
    /// <param name="coefficient">The coefficient of √2.</param>
    /// <returns><c>-1</c>, <c>0</c>, or <c>1</c> as the value is negative, zero, or positive.</returns>
    private static int SignSqrt2(long rational, long coefficient) {
        if ((rational == 0L) && (coefficient == 0L)) { return 0; }
        if ((rational >= 0L) && (coefficient >= 0L)) { return 1; }
        if ((rational <= 0L) && (coefficient <= 0L)) { return -1; }

        // Opposite signs: compare rational² against 2·coefficient² at full width, then read off by the sign of rational.
        var comparison = ((Int128)rational * rational).CompareTo(((Int128)2 * coefficient) * coefficient);

        return ((rational > 0L)
            ? ((comparison > 0) ? 1 : ((comparison < 0) ? -1 : 0))
            : ((comparison > 0) ? -1 : ((comparison < 0) ? 1 : 0)));
    }
}
