namespace Puck.Maths;

/// <summary>Compares sequences by their elements, in order, as a dictionary or set key: an array, a read-only list, or
/// (without allocating, through an alternate lookup) a span. Elements compare through their own
/// <see cref="IEquatable{T}"/>, so a string sequence compares ordinally. The hash combines element hashes with
/// <see cref="HashCode"/>, which is seeded per process: it serves in-memory tables and is never persisted or fed to
/// simulation state.</summary>
/// <typeparam name="T">The element type.</typeparam>
public sealed class SequenceEqualityComparer<T> :
    IEqualityComparer<T[]>,
    IEqualityComparer<IReadOnlyList<T>>,
    IAlternateEqualityComparer<ReadOnlySpan<T>, T[]>
    where T : IEquatable<T> {
    /// <summary>Gets the shared comparer.</summary>
    public static SequenceEqualityComparer<T> Instance { get; } = new();

    /// <summary>Copies a span into a new array, the key an alternate lookup adds on a miss.</summary>
    /// <param name="alternate">The elements.</param>
    /// <returns>A new array holding <paramref name="alternate"/>.</returns>
    public T[] Create(ReadOnlySpan<T> alternate) =>
        alternate.ToArray();
    /// <summary>Determines whether two arrays hold equal elements in the same order.</summary>
    /// <param name="x">The first array, or <see langword="null"/>.</param>
    /// <param name="y">The second array, or <see langword="null"/>.</param>
    /// <returns>Whether both are the same array, or both are non-null with equal elements.</returns>
    public bool Equals(T[]? x, T[]? y) => (
        ReferenceEquals(objA: x, objB: y) ||
        ((x is not null) && (y is not null) && x.AsSpan().SequenceEqual(other: y))
    );
    /// <summary>Determines whether two read-only lists hold equal elements in the same order.</summary>
    /// <param name="x">The first list, or <see langword="null"/>.</param>
    /// <param name="y">The second list, or <see langword="null"/>.</param>
    /// <returns>Whether both are the same list, or both are non-null with equal elements.</returns>
    public bool Equals(IReadOnlyList<T>? x, IReadOnlyList<T>? y) {
        if (ReferenceEquals(objA: x, objB: y)) { return true; }
        if ((x is null) || (y is null) || (x.Count != y.Count)) { return false; }
        for (var index = 0; (index < x.Count); index++) {
            if (!EqualityComparer<T>.Default.Equals(x: x[index], y: y[index])) { return false; }
        }
        return true;
    }
    /// <summary>Determines whether a span and an array hold equal elements in the same order.</summary>
    /// <param name="alternate">The span.</param>
    /// <param name="other">The array.</param>
    /// <returns>Whether the elements are equal in order.</returns>
    public bool Equals(ReadOnlySpan<T> alternate, T[] other) =>
        alternate.SequenceEqual(other: other);
    /// <summary>Hashes an array by its elements.</summary>
    /// <param name="obj">The array.</param>
    /// <returns>A hash equal for arrays that compare equal, and for the same elements as a span or list.</returns>
    public int GetHashCode(T[] obj) =>
        GetHashCode(alternate: obj.AsSpan());
    /// <summary>Hashes a read-only list by its elements.</summary>
    /// <param name="obj">The list.</param>
    /// <returns>A hash equal for lists that compare equal, and for the same elements as an array or span.</returns>
    public int GetHashCode(IReadOnlyList<T> obj) {
        var hash = new HashCode();

        for (var index = 0; (index < obj.Count); index++) {
            hash.Add(value: obj[index]);
        }

        return hash.ToHashCode();
    }
    /// <summary>Hashes a span by its elements.</summary>
    /// <param name="alternate">The span.</param>
    /// <returns>A hash equal for sequences that compare equal, array or list included.</returns>
    public int GetHashCode(ReadOnlySpan<T> alternate) {
        var hash = new HashCode();

        foreach (var element in alternate) {
            hash.Add(value: element);
        }

        return hash.ToHashCode();
    }
}
