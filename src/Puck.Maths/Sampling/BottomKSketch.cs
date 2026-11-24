namespace Puck.Maths;

/// <summary>
/// A bottom-k sketch of a set of 64-bit element words: the <see cref="Capacity"/> smallest distinct values of each
/// element's <see cref="InvertibleBitMix.Mix64"/>. Two sketches estimate how much two sets share
/// (<see cref="Resemblance"/>, the Jaccard index) without either set, at a fixed size whatever the sets hold. The values
/// are pure integer arithmetic, so the same set gives the same sketch on every machine and run.
/// </summary>
/// <remarks>
/// The estimate is Broder's: of the <c>k</c> smallest mixed values of the union, the share both sets hold. Its standard
/// error is about <c>sqrt(J(1 - J) / k)</c>, so a capacity of 16 tells sets that are nearly the same from sets that
/// barely overlap, and is no measure of a small difference.
/// </remarks>
public sealed class BottomKSketch {
    /// <summary>The capacity a sketch is made with unless its maker names one.</summary>
    public const int DefaultCapacity = 16;

    private readonly ulong[] m_values;

    /// <summary>Initializes a new instance of the <see cref="BottomKSketch"/> class.</summary>
    /// <param name="capacity">The most values the sketch keeps, at least one.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="capacity"/> is less than one.</exception>
    public BottomKSketch(int capacity = DefaultCapacity) {
        ArgumentOutOfRangeException.ThrowIfLessThan(value: capacity, other: 1);
        m_values = new ulong[capacity];
    }

    /// <summary>Gets the most values the sketch keeps.</summary>
    public int Capacity => m_values.Length;
    /// <summary>Gets how many values the sketch holds: the set's distinct elements, up to <see cref="Capacity"/>.</summary>
    public int Count { get; private set; }
    /// <summary>Gets the values held, ascending.</summary>
    public ReadOnlySpan<ulong> Values => m_values.AsSpan(start: 0, length: Count);

    /// <summary>Adds an element; adding one the sketch already holds changes nothing.</summary>
    /// <param name="element">The element word.</param>
    public void Add(ulong element) {
        var value = InvertibleBitMix.Mix64(value: element);
        var at = Array.BinarySearch(array: m_values, index: 0, length: Count, value: value);

        if (at >= 0) { return; }
        at = ~at;
        if (at >= Capacity) { return; }
        if (Count < Capacity) { Count++; }
        Array.Copy(sourceArray: m_values, sourceIndex: at, destinationArray: m_values, destinationIndex: (at + 1), length: (Count - at - 1));
        m_values[at] = value;
    }
    /// <summary>Estimates the Jaccard index of the two sets two sketches describe: of the smallest values of their union,
    /// up to the smaller capacity, the share both hold.</summary>
    /// <param name="left">One sketch's values, ascending.</param>
    /// <param name="right">The other's values, ascending.</param>
    /// <param name="capacity">The capacity both were made with.</param>
    /// <returns>The estimate in [0, 1]; zero when either set is empty.</returns>
    public static double Resemblance(ReadOnlySpan<ulong> left, ReadOnlySpan<ulong> right, int capacity) {
        if (left.IsEmpty || right.IsEmpty) { return 0.0; }

        var (i, j, taken, shared) = (0, 0, 0, 0);

        while ((taken < capacity) && ((i < left.Length) || (j < right.Length))) {
            if ((j >= right.Length) || ((i < left.Length) && (left[i] < right[j]))) {
                i++;
            } else if ((i >= left.Length) || (right[j] < left[i])) {
                j++;
            } else {
                shared++;
                i++;
                j++;
            }
            taken++;
        }

        return (shared / (double)taken);
    }
}
