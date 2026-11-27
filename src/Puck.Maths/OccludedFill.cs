namespace Puck.Maths;

/// <summary>Occluded fills over a 64-cell bit word: how far a set of sliders reaches along one step through empty cells,
/// computed in logarithmic rounds by a Kogge-Stone prefix, with no lookup table and no per-cell loop.</summary>
/// <remarks>A step is a fixed signed bit distance: cell <c>c</c>'s neighbour is cell <c>c + step</c>. A word whose step
/// would wrap (a grid's east edge onto the next rank's west edge) excludes the wrapped cells from
/// <c>landing</c>, the cells a step may arrive on, so no round carries a bit across the edge. The result is exact, and the
/// same on every host.</remarks>
public static class OccludedFill {
    /// <summary>Returns the cells the sliders attack along one step: every cell reached through a run of empty cells,
    /// and the first occupied cell that ends each run. A slider's own cell is in the result only when another slider
    /// reaches it.</summary>
    /// <param name="sliders">The cells the runs start from, one bit per cell.</param>
    /// <param name="empty">The cells a run passes through; any other cell ends a run that arrives on it.</param>
    /// <param name="step">The signed bit distance of one step, between -63 and 63 and nonzero.</param>
    /// <param name="landing">The cells a step may arrive on: every cell whose cell <c>step</c> back is a cell with a
    /// neighbour that way.</param>
    /// <returns>The attacked cells, a subset of <paramref name="landing"/>.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="step"/> is zero or outside -63 to 63.</exception>
    public static ulong Slide(ulong sliders, ulong empty, int step, ulong landing) {
        ArgumentOutOfRangeException.ThrowIfZero(value: step);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(value: Math.Abs(value: step), other: 63);

        var generated = sliders;
        var propagating = (empty & landing);

        // Round k joins runs of 2^k steps: a cell is generated when a generated cell lies 2^k steps back and every cell
        // between is propagating, and propagating cells come to mean runs of 2^(k+1) empty landings. Rounds stop at the
        // word's width or once no run remains.
        for (var distance = step; ((Math.Abs(value: distance) < 64) && (propagating != 0UL)); distance *= 2) {
            generated |= (propagating & Shift(word: generated, distance: distance));
            propagating &= Shift(word: propagating, distance: distance);
        }

        return (Shift(word: generated, distance: step) & landing);
    }

    private static ulong Shift(ulong word, int distance) => ((distance >= 0) ? (word << distance) : (word >> -distance));
}
