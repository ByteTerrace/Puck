using System.Runtime.CompilerServices;

namespace Puck.Maths;

/// <summary>Orders values by a primary key and breaks a tie with a secondary key.</summary>
/// <remarks>
/// A deterministic sort needs a total order, and a total order over records usually means a primary key with a unique
/// secondary key behind it. The secondary comparison runs only when the primary keys tie, and each result is the key's
/// own <see cref="IComparable{T}.CompareTo"/> value, returned unchanged.
/// </remarks>
public static class LexicographicOrder {
    /// <summary>Compares two records by their primary keys, and by their secondary keys when the primary keys tie.</summary>
    /// <typeparam name="TPrimary">The primary key type.</typeparam>
    /// <typeparam name="TSecondary">The secondary key type.</typeparam>
    /// <param name="leftPrimary">The first record's primary key.</param>
    /// <param name="rightPrimary">The second record's primary key.</param>
    /// <param name="leftSecondary">The first record's secondary key.</param>
    /// <param name="rightSecondary">The second record's secondary key.</param>
    /// <returns>The primary comparison when it is non-zero; otherwise the secondary comparison.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int Compare<TPrimary, TSecondary>(TPrimary leftPrimary, TPrimary rightPrimary, TSecondary leftSecondary, TSecondary rightSecondary)
        where TPrimary : IComparable<TPrimary>
        where TSecondary : IComparable<TSecondary> {
        var order = leftPrimary.CompareTo(other: rightPrimary);

        return ((order != 0)
            ? order
            : leftSecondary.CompareTo(other: rightSecondary)
        );
    }
}
