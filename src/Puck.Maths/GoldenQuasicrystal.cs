namespace Puck.Maths;

/// <summary>
/// An infinite, aperiodic, never-repeating point set — a quasicrystal — built by the cut-and-project method from the
/// golden-ratio ring <c>ℤ[φ]</c>. This is the canonical one-dimensional case (the Fibonacci chain): the points of
/// <c>ℤ[φ]</c> whose Galois conjugate falls inside a unit window, which along the line space into exactly two gap
/// lengths in the ratio φ, arranged as the Fibonacci word. Membership and traversal are exact integer arithmetic, so
/// the structure never drifts and never repeats — the same region regenerates identically on every machine with no
/// stored data.
/// </summary>
/// <remarks>
/// A point is the ring element <c>a + b·φ</c>, addressed by the integer pair <c>(a, b)</c>. <see cref="Contains(int, int)"/>
/// is the exact acceptance test; <see cref="Next(int, int)"/> and <see cref="Previous(int, int)"/> walk the chain in
/// physical order in O(1) (each step adds φ or φ² in the ring); <see cref="StartsLongTile(int, int)"/> reports which of
/// the two tiles begins at a point; and <see cref="Position(int, int)"/> gives the fixed-point coordinate on the line
/// (the one approximate value — the combinatorial structure above it is exact). The golden ratio here is the same φ that
/// pairs the rings of <see cref="SymmetryLattice"/>. The same construction in higher dimensions yields the Penrose
/// (5-fold) and Ammann–Beenker (8-fold) tilings; this is their line.
/// </remarks>
public static class GoldenQuasicrystal {
    // round(φ · 2^16): the golden ratio in Q48.16, for the fixed-point physical position only.
    private const long GoldenRatioRawQ16 = 106040L;

    /// <summary>Determines whether the ring element <c>a + b·φ</c> is a point of the quasicrystal.</summary>
    /// <param name="a">The integer part of the ring element.</param>
    /// <param name="b">The coefficient of φ.</param>
    /// <returns><see langword="true"/> when the element's conjugate lies in the acceptance window <c>[0, 1)</c>.</returns>
    public static bool Contains(int a, int b) =>
        // Accept when the Galois conjugate (a + b) − b·φ lies in [0, 1): the projection into internal space hits the window.
        ((SignPhi(rational: (a + b), coefficient: -b) >= 0) && (SignPhi(rational: ((a + b) - 1), coefficient: -b) < 0));
    /// <summary>Determines whether the longer of the two tiles begins at the point <c>a + b·φ</c>.</summary>
    /// <param name="a">The integer part of the point.</param>
    /// <param name="b">The coefficient of φ.</param>
    /// <returns><see langword="true"/> when the tile starting here has length φ² (the long tile); otherwise it has length φ.</returns>
    public static bool StartsLongTile(int a, int b) =>
        // The long tile starts when the conjugate is below φ − 1, so the next accepted point is φ² further along.
        (SignPhi(rational: ((a + b) + 1), coefficient: (-b - 1)) < 0);
    /// <summary>Returns the next point along the line — the far end of the tile that starts at <c>a + b·φ</c>.</summary>
    /// <param name="a">The integer part of a point of the quasicrystal.</param>
    /// <param name="b">The coefficient of φ.</param>
    /// <returns>The next point, reached by adding φ² (a long tile) or φ (a short tile); itself a point of the quasicrystal.</returns>
    public static (int A, int B) Next(int a, int b) =>
        (StartsLongTile(a: a, b: b) ? (a + 1, b + 1) : (a, b + 1));
    /// <summary>Returns the previous point along the line — the near end of the tile that ends at <c>a + b·φ</c>.</summary>
    /// <param name="a">The integer part of a point of the quasicrystal.</param>
    /// <param name="b">The coefficient of φ.</param>
    /// <returns>The preceding point of the quasicrystal.</returns>
    public static (int A, int B) Previous(int a, int b) =>
        // Of the two candidate predecessors, the true one is a point of the quasicrystal that steps forward to (a, b);
        // a non-member can also step here, so membership must be checked, not just the forward step.
        ((Contains(a: (a - 1), b: (b - 1)) && (Next(a: (a - 1), b: (b - 1)) == (a, b))) ? (a - 1, b - 1) : (a, b - 1));
    /// <summary>Returns the position of the point <c>a + b·φ</c> along the line.</summary>
    /// <param name="a">The integer part of the point.</param>
    /// <param name="b">The coefficient of φ.</param>
    /// <returns>The fixed-point coordinate <c>a + b·φ</c> — the one approximate value; membership and traversal are exact.</returns>
    public static FixedQ4816 Position(int a, int b) =>
        (FixedQ4816.FromInteger(value: a) + (FixedQ4816.FromInteger(value: b) * FixedQ4816.FromRawBits(value: GoldenRatioRawQ16)));

    /// <summary>Returns the sign of the real number <c>rational + coefficient·φ</c>, exactly, by integer arithmetic.</summary>
    /// <param name="rational">The integer term.</param>
    /// <param name="coefficient">The coefficient of φ.</param>
    /// <returns><c>-1</c>, <c>0</c>, or <c>1</c> as the value is negative, zero, or positive.</returns>
    private static int SignPhi(long rational, long coefficient) {
        // rational + coefficient·φ = (whole + root·√5) / 2, with whole = 2·rational + coefficient and root = coefficient.
        var whole = ((2L * rational) + coefficient);
        var root = coefficient;

        if ((whole == 0L) && (root == 0L)) { return 0; }
        if ((whole >= 0L) && (root >= 0L)) { return 1; }
        if ((whole <= 0L) && (root <= 0L)) { return -1; }

        // Opposite signs: compare whole² against 5·root² at full width, then read off by the sign of whole.
        var comparison = ((Int128)whole * whole).CompareTo(((Int128)5 * root) * root);

        return ((whole > 0L)
            ? ((comparison > 0) ? 1 : ((comparison < 0) ? -1 : 0))
            : ((comparison > 0) ? -1 : ((comparison < 0) ? 1 : 0)));
    }
}
