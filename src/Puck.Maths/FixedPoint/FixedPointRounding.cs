using System.Numerics;
using System.Runtime.CompilerServices;

namespace Puck.Maths;

/// <summary>
/// The round-half-to-even correction every fixed-point carrier applies after a truncating narrowing.
/// </summary>
internal static class FixedPointRounding {
    /// <summary>Corrects a truncated result to the nearest representable value, with ties resolved to even.</summary>
    /// <typeparam name="T">The binary integer the truncated result and the discarded remainder are carried in.</typeparam>
    /// <param name="truncated">The truncated result; its low bit carries the tie parity.</param>
    /// <param name="remainder">The discarded remainder, in whatever domain the caller measures it.</param>
    /// <param name="threshold">The half-unit <paramref name="remainder"/> ties against, in that same domain.</param>
    /// <returns><paramref name="truncated"/> incremented when <paramref name="remainder"/> exceeds <paramref name="threshold"/>, or ties it with an odd <paramref name="truncated"/>; otherwise <paramref name="truncated"/> unchanged. The increment is branchless and wraps rather than throwing.</returns>
    [MethodImpl(methodImplOptions: MethodImplOptions.AggressiveInlining)]
    internal static T RoundHalfToEven<T>(T truncated, T remainder, T threshold) where T : IBinaryInteger<T> =>
        unchecked((truncated + ((remainder > threshold).As<T>() | ((remainder == threshold).As<T>() & truncated & T.One))));
}
