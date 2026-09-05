using System.Numerics;
using System.Runtime.CompilerServices;

namespace Puck.Maths;

public static partial class UnsignedNumberFunctions {
    /// <summary>Exchanges the two components of an elegant pair without decoding them.</summary>
    /// <typeparam name="T">The unsigned binary integer carrier.</typeparam>
    /// <param name="value">The encoded pair.</param>
    /// <returns>The encoded pair with its components exchanged.</returns>
    /// <exception cref="OverflowException">The exchanged pair does not fit the carrier.</exception>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static T ElegantSwap<T>(this T value) where T : IBinaryInteger<T>, IUnsignedNumber<T> {
        var maximum = value.SquareRoot();
        var center = checked((maximum * (maximum + T.One)));
        // Do not form 2*center: that intermediate can overflow even when the result fits.
        return ((value >= center) ? (center - (value - center)) : checked((center + (center - value))));
    }
    /// <summary>Adds the same nonnegative amount to both components of an elegant pair.</summary>
    /// <typeparam name="T">The unsigned binary integer carrier.</typeparam>
    /// <param name="value">The encoded pair.</param>
    /// <param name="amount">The amount added to each component.</param>
    /// <returns>The translated encoding, computed from its shell and displacement from the diagonal.</returns>
    /// <exception cref="OverflowException">The translated pair does not fit the carrier.</exception>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static T ElegantTranslate<T>(this T value, T amount) where T : IBinaryInteger<T>, IUnsignedNumber<T> {
        var maximum = value.SquareRoot();
        var center = checked((maximum * (maximum + T.One)));
        var distance = ((value >= center) ? (value - center) : (center - value));
        var target = checked((maximum + amount));

        return ElegantRecenter(maximum: target, distance: distance, above: (value >= center) ^ T.IsOddInteger(value: amount));
    }
    /// <summary>Multiplies both components of an elegant pair by a nonnegative integer.</summary>
    /// <typeparam name="T">The unsigned binary integer carrier.</typeparam>
    /// <param name="value">The encoded pair.</param>
    /// <param name="factor">The scale applied to each component.</param>
    /// <returns>The scaled encoding. Zero maps every pair to zero.</returns>
    /// <exception cref="OverflowException">The scaled pair does not fit the carrier.</exception>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static T ElegantScale<T>(this T value, T factor) where T : IBinaryInteger<T>, IUnsignedNumber<T> {
        if ((factor == T.Zero) || (value == T.Zero)) { return T.Zero; }
        var maximum = value.SquareRoot();

        if (T.IsOddInteger(value: factor) || T.IsEvenInteger(value: maximum)) {
            // The shell orientation is unchanged, so all terms are nonnegative and checked independently.
            return checked(((factor * value) + ((factor * (factor - T.One)) * (maximum * maximum))));
        }
        var center = checked((maximum * (maximum + T.One)));
        var distance = ((value >= center) ? (value - center) : (center - value));
        var target = checked((maximum * factor));

        return ElegantRecenter(maximum: target, distance: checked((distance * factor)),
            above: (value >= center) ^ (T.IsOddInteger(value: maximum) != T.IsOddInteger(value: target)));
    }
    /// <summary>Gets the larger component of an elegant pair without decoding the pair.</summary>
    /// <typeparam name="T">The unsigned binary integer carrier.</typeparam>
    /// <param name="value">The encoded pair.</param>
    /// <returns>The maximum component, equal to the integer square root of the index.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static T ElegantMaximum<T>(this T value) where T : IBinaryInteger<T>, IUnsignedNumber<T> => value.SquareRoot();
    /// <summary>Gets the absolute difference between the components of an elegant pair.</summary>
    /// <typeparam name="T">The unsigned binary integer carrier.</typeparam>
    /// <param name="value">The encoded pair.</param>
    /// <returns>The unsigned component difference.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static T ElegantDifference<T>(this T value) where T : IBinaryInteger<T>, IUnsignedNumber<T> {
        var maximum = value.SquareRoot();

        return ElegantDifference(maximum: maximum, value: value);
    }
    /// <summary>Gets the smaller component of an elegant pair without decoding the pair.</summary>
    /// <typeparam name="T">The unsigned binary integer carrier.</typeparam>
    /// <param name="value">The encoded pair.</param>
    /// <returns>The minimum component.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static T ElegantMinimum<T>(this T value) where T : IBinaryInteger<T>, IUnsignedNumber<T> {
        var maximum = value.SquareRoot();

        return (maximum - ElegantDifference(maximum: maximum, value: value));
    }
    /// <summary>Gets the sum of the components of an elegant pair without decoding the pair.</summary>
    /// <typeparam name="T">The unsigned binary integer carrier.</typeparam>
    /// <param name="value">The encoded pair.</param>
    /// <returns>The component sum.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static T ElegantSum<T>(this T value) where T : IBinaryInteger<T>, IUnsignedNumber<T> {
        var maximum = value.SquareRoot();

        return ((maximum << 1) - ElegantDifference(maximum: maximum, value: value));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static T ElegantDifference<T>(T value, T maximum) where T : IBinaryInteger<T>, IUnsignedNumber<T> {
        var center = (maximum * (maximum + T.One));

        return ((value >= center) ? (value - center) : (center - value));
    }
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static T ElegantRecenter<T>(T maximum, T distance, bool above) where T : IBinaryInteger<T>, IUnsignedNumber<T> {
        var center = checked((maximum * checked((maximum + T.One))));

        return (above ? checked((center + distance)) : checked((center - distance)));
    }
}
