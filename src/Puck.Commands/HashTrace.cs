namespace Puck.Commands;

/// <summary>Helpers for comparing the per-tick state-hash traces a determinism/replay check produces.</summary>
public static class HashTrace {
    /// <summary>Returns the first tick at which two traces differ, or <c>-1</c> when they are identical (length included).</summary>
    /// <param name="left">The first trace.</param>
    /// <param name="right">The second trace.</param>
    /// <returns>The index of the first divergence, the shorter length when one is a prefix of the other, or <c>-1</c> when equal.</returns>
    /// <exception cref="ArgumentNullException">Either trace is <see langword="null"/>.</exception>
    public static int FirstDivergence(ulong[] left, ulong[] right) {
        ArgumentNullException.ThrowIfNull(argument: left);
        ArgumentNullException.ThrowIfNull(argument: right);

        var count = Math.Min(val1: left.Length, val2: right.Length);

        for (var index = 0; (index < count); index++) {
            if (left[index] != right[index]) {
                return index;
            }
        }

        return ((left.Length == right.Length) ? -1 : count);
    }
}
