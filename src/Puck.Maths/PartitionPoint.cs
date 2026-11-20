namespace Puck.Maths;

/// <summary>Finds where a sorted sequence stops satisfying a test: the boundary of a prefix whose elements all pass
/// and a suffix whose elements all fail. It is the lower-bound and upper-bound search of a sorted run (the first
/// element not below a key, the first element above a key) with the order supplied as the test, and it rests on
/// <see cref="MemoryExtensions.BinarySearch{T, TComparable}(ReadOnlySpan{T}, TComparable)"/> rather than a second
/// hand-written loop.</summary>
public static class PartitionPoint {
    /// <summary>Returns the index of the first element the test rejects.</summary>
    /// <typeparam name="T">The element type.</typeparam>
    /// <typeparam name="TState">The state the test reads, passed by value so the test can be a static lambda.</typeparam>
    /// <param name="span">The elements, partitioned by <paramref name="isBefore"/>: every element it accepts precedes
    /// every element it rejects.</param>
    /// <param name="state">The state handed to each call of <paramref name="isBefore"/>.</param>
    /// <param name="isBefore">Whether an element lies in the prefix.</param>
    /// <returns>The length of the prefix: <c>0</c> when no element is accepted and <see cref="ReadOnlySpan{T}.Length"/>
    /// when every one is. A span the test does not partition gives an unspecified index within it.</returns>
    public static int Of<T, TState>(ReadOnlySpan<T> span, TState state, Func<TState, T, bool> isBefore) {
        ArgumentNullException.ThrowIfNull(argument: isBefore);

        return ~span.BinarySearch(comparable: new Boundary<T, TState>(
            isBefore: isBefore,
            state: state
        ));
    }

    // Never equal to an element, so the search always ends at its insertion point: greater than every element in
    // the prefix, which sends the search right, and less than every element after it.
    private readonly struct Boundary<T, TState>(Func<TState, T, bool> isBefore, TState state) : IComparable<T> {
        public int CompareTo(T? other) => (isBefore(arg1: state, arg2: other!) ? 1 : -1);
    }
}
