using System.Numerics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace Puck.Maths;

/// <summary>
/// Generates uniformly distributed, cryptographically secure unsigned integers of an arbitrary binary integer width.
/// </summary>
/// <remarks>
/// All randomness is drawn from <see cref="RandomNumberGenerator"/>, so the results are suitable for security-sensitive
/// work. Bounded draws use rejection sampling rather than a plain modulo reduction, which keeps the output exactly
/// uniform across the requested interval and free of modulo bias.
/// </remarks>
public static class SecureRandom {
    /// <summary>Returns a uniformly distributed value in the half-open interval <c>[0, <paramref name="exclusiveHigh"/>)</c>.</summary>
    /// <typeparam name="T">The unsigned binary integer type to produce.</typeparam>
    /// <param name="exclusiveHigh">The exclusive upper bound of the interval. Must be greater than zero.</param>
    /// <returns>A random value greater than or equal to zero and strictly less than <paramref name="exclusiveHigh"/>.</returns>
    /// <remarks>
    /// Candidates are drawn until one falls within the largest multiple of <paramref name="exclusiveHigh"/> that the
    /// width of <typeparamref name="T"/> can represent, so every value in the interval is equally likely.
    /// </remarks>
    private static T NextUInt<T>(T exclusiveHigh) where T : struct, IBinaryInteger<T>, IUnsignedNumber<T> {
        var range = (T.AllBitsSet - (((T.AllBitsSet % exclusiveHigh) + T.One) % exclusiveHigh));
        T result;

        do {
            result = NextUInt<T>();
        } while (result > range);

        return (result % exclusiveHigh);
    }

    /// <summary>Returns a uniformly distributed value spanning the entire range of <typeparamref name="T"/>.</summary>
    /// <typeparam name="T">The unsigned binary integer type to produce.</typeparam>
    /// <returns>A random value in which every bit pattern of <typeparamref name="T"/> is equally likely.</returns>
    public static T NextUInt<T>() where T : struct, IBinaryInteger<T>, IUnsignedNumber<T> {
        var result = T.Zero;

        RandomNumberGenerator.Fill(data: MemoryMarshal.AsBytes(span: new Span<T>(reference: ref result)));

        return result;
    }
    /// <summary>Returns a uniformly distributed value in the inclusive interval <c>[<paramref name="minimum"/>, <paramref name="maximum"/>]</c>.</summary>
    /// <typeparam name="T">The unsigned binary integer type to produce.</typeparam>
    /// <param name="maximum">The inclusive upper bound of the interval.</param>
    /// <param name="minimum">The inclusive lower bound of the interval.</param>
    /// <returns>A random value greater than or equal to <paramref name="minimum"/> and less than or equal to <paramref name="maximum"/>.</returns>
    /// <remarks>When the interval spans the full range of <typeparamref name="T"/>, a single unbounded draw is returned directly.</remarks>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="maximum"/> is less than <paramref name="minimum"/>.</exception>
    public static T NextUInt<T>(T maximum, T minimum) where T : struct, IBinaryInteger<T>, IUnsignedNumber<T> {
        // Guard the inverted interval: an unsigned (maximum - minimum) would wrap to a huge span and silently draw an
        // unrelated value, so reject it rather than honour a range that was never asked for.
        if (maximum < minimum) {
            throw new ArgumentOutOfRangeException(paramName: nameof(maximum), actualValue: maximum, message: "The maximum must be greater than or equal to the minimum.");
        }

        var range = (maximum - minimum);

        return ((range != T.AllBitsSet)
            ? (NextUInt(exclusiveHigh: (range + T.One)) + minimum)
            : NextUInt<T>());
    }
}
