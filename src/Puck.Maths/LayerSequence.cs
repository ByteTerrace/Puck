using System.Runtime.CompilerServices;

namespace Puck.Maths;

/// <summary>The position of an index within a <see cref="LayerSequence"/>: the layer that contains it and the zero-based offset from that layer's first index.</summary>
/// <param name="Layer">The layer containing the index; layer <c>0</c> is the core.</param>
/// <param name="Offset">The zero-based offset of the index within <paramref name="Layer"/>.</param>
public readonly record struct LayerLocation(long Layer, long Offset);

/// <summary>The saturating position of an index against a <see cref="LayerSequence"/>: the layer clamps at the sequence's last layer, and indices beyond capacity report how far past the boundary they lie.</summary>
/// <param name="Layer">The layer containing the index, clamped to <see cref="LayerSequence.MaxLayer"/> for indices beyond capacity.</param>
/// <param name="Overflow">Zero for an in-range index; otherwise one more than the distance from the last valid index, growing linearly.</param>
/// <param name="Depth">Zero for an index at or below the sequence's continuous vertex; otherwise the magnitude of the imaginary component of the layer equation's complex root, in layers, growing with the square root of the excess.</param>
public readonly record struct LayerProjection(long Layer, long Overflow, long Depth);

/// <summary>
/// Describes a layered index space — a core of <see cref="Seed"/> indices wrapped by layers whose sizes form an
/// arithmetic progression starting at <see cref="Start"/> and changing by <see cref="Step"/> — and answers
/// index-to-layer queries in constant time by inverting the quadratic prefix sum instead of walking it.
/// </summary>
/// <remarks>
/// <para>
/// Layer <c>n</c> (for <c>n ≥ 1</c>) holds <c>Start + (n − 1)·Step</c> indices, so the total through layer <c>n</c>
/// is the quadratic <c>Count(n) = Seed + Start·n + Step·n·(n − 1)/2</c> — the generalized figurate numbers.
/// <see cref="LayerOf(long)"/> inverts that closed form with pure integer arithmetic (an <see cref="Int128"/>
/// discriminant, an exact integer square root, and a floor division), so the result is deterministic on every
/// platform and never approximated through floating point.
/// </para>
/// <para>
/// A negative <see cref="Step"/> yields a BOUNDED sequence: layer sizes shrink to zero and the total index count
/// tops out at <see cref="Capacity"/>. <see cref="Project(long)"/> is the saturating query for that regime — indices
/// beyond capacity clamp to <see cref="MaxLayer"/> and report linear <see cref="LayerProjection.Overflow"/> and
/// square-root <see cref="LayerProjection.Depth"/> excess channels instead of failing.
/// </para>
/// </remarks>
public readonly record struct LayerSequence {
    private const long MaximumSeed = ((1L << 62) - 1L);
    private const long MaximumStart = ((1L << 62) - 1L);
    private const long MaximumStepMagnitude = (1L << 32);

    private readonly Int128 m_capacity;
    private readonly long m_maxLayer;

    private LayerSequence(long start, long step, long seed) {
        Seed = seed;
        Start = start;
        Step = step;

        m_maxLayer = ((0L > step)
            ? ((0L == start)
                ? 0L
                : (1L + ((start - 1L) / (-step))))
            : (((0L == step) && (0L == start))
                ? 0L
                : long.MaxValue));

        if (long.MaxValue == m_maxLayer) {
            m_capacity = Int128.MaxValue;
        } else {
            var layerCount = ((Int128)m_maxLayer);

            m_capacity = ((((Int128)seed) + (((Int128)start) * layerCount)) + (((Int128)step) * ((layerCount * (layerCount - Int128.One)) / 2)));
        }
    }

    /// <summary>Gets the number of indices in layer <c>0</c> — the core the layers wrap.</summary>
    public long Seed { get; }
    /// <summary>Gets the number of indices in layer <c>1</c>.</summary>
    public long Start { get; }
    /// <summary>Gets the change in layer size from each layer to the next; negative values bound the sequence.</summary>
    public long Step { get; }

    /// <summary>Gets a value indicating whether the sequence holds finitely many indices.</summary>
    public bool IsBounded =>
        (long.MaxValue != m_maxLayer);
    /// <summary>Gets the last layer that can contain an index, or <see cref="long.MaxValue"/> when the sequence is unbounded.</summary>
    public long MaxLayer =>
        m_maxLayer;
    /// <summary>Gets the total number of indices the sequence can contain, saturating at <see cref="long.MaxValue"/> when the true capacity is unbounded or exceeds <see cref="long"/>.</summary>
    public long Capacity =>
        ((m_capacity > ((Int128)long.MaxValue))
        ? long.MaxValue
        : ((long)m_capacity));

    private Int128 CapacityLimit =>
        m_capacity;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Int128 FloorDivide(Int128 numerator, Int128 denominator) {
        var quotient = (numerator / denominator);
        var remainder = (numerator - (quotient * denominator));

        return (quotient - ((remainder != Int128.Zero) && (Int128.IsNegative(value: remainder) != Int128.IsNegative(value: denominator))).As<Int128>());
    }
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private Int128 CountUnchecked(Int128 layerCount) =>
        ((((Int128)Seed) + (((Int128)Start) * layerCount)) + (((Int128)Step) * ((layerCount * (layerCount - Int128.One)) / 2)));
    private long LocateLayer(long index) {
        if (0L == Step) {
            // Start >= 1 here: a bounded flat sequence never reaches this path. The containing layer itself must
            // remain representable, just like Count and LayerSize require their long result to remain representable.
            return checked(1L + ((index - Seed) / Start));
        }

        var b = ((2 * ((Int128)Start)) - Step);
        var discriminant = ((b * b) + ((((Int128)Step) * (index - Seed)) * 8)); // non-negative for every in-range index
        Int128 layer;

        if (
            (discriminant <= ((Int128)ulong.MaxValue)) &&
            (b <= ((Int128)long.MaxValue))
        ) {
            // the common regime: a hardware-seeded 64-bit root and a single 64-bit division
            var root = ((long)((ulong)discriminant).SquareRoot());
            var numerator = (root - ((long)b));
            var denominator = (2L * Step);
            var quotient = (numerator / denominator);
            var remainder = (numerator - (quotient * denominator));

            layer = (quotient - ((remainder != 0L) && ((remainder < 0L) != (denominator < 0L))).As<long>());
        } else {
            var root = ((Int128)((UInt128)discriminant).SquareRoot());

            layer = FloorDivide(
                numerator: (root - b),
                denominator: (2 * ((Int128)Step))
            );
        }

        // A positive step floors exactly; a negative divisor can land one layer high and a single step settles it.
        // Both facts are verified by the exhaustive sweeps' raw-closed-form checks.
        if (
            (0L > Step) &&
            (CountUnchecked(layerCount: layer) > index)
        ) { layer -= Int128.One; }

        return (((long)layer) + 1L);
    }
    private void ThrowIfOutOfRange(long index) {
        if (0L > index) {
            throw new ArgumentOutOfRangeException(
                paramName: nameof(index),
                message: "index must be non-negative"
            );
        }

        if (
            (index >= Seed) &&
            (CapacityLimit <= index)
        ) {
            throw new ArgumentOutOfRangeException(
                paramName: nameof(index),
                message: "index lies beyond the capacity of this bounded sequence; use Project for a saturating query"
            );
        }
    }

    /// <summary>Creates a validated <see cref="LayerSequence"/> from its three constants.</summary>
    /// <param name="start">The size of layer <c>1</c>; it must lie in <c>[0, 2⁶²)</c>.</param>
    /// <param name="step">The change in size from each layer to the next; its magnitude must not exceed <c>2³²</c>. Negative values bound the sequence.</param>
    /// <param name="seed">The size of the core (layer <c>0</c>); it must lie in <c>[0, 2⁶²)</c>.</param>
    /// <returns>The sequence described by the given constants.</returns>
    /// <exception cref="ArgumentOutOfRangeException">A constant lies outside its documented range.</exception>
    /// <remarks>The ranges guarantee that every query's <see cref="Int128"/> intermediate fits without overflow for any non-negative <see cref="long"/> index.</remarks>
    public static LayerSequence Create(long start, long step, long seed) {
        if (
            (0L > start) ||
            (MaximumStart < start)
        ) {
            throw new ArgumentOutOfRangeException(
                paramName: nameof(start),
                message: "start must lie in [0, 2^62)"
            );
        }

        if (
            (0L > seed) ||
            (MaximumSeed < seed)
        ) {
            throw new ArgumentOutOfRangeException(
                paramName: nameof(seed),
                message: "seed must lie in [0, 2^62)"
            );
        }

        if (
            (MaximumStepMagnitude < step) ||
            ((-MaximumStepMagnitude) > step)
        ) {
            throw new ArgumentOutOfRangeException(
                paramName: nameof(step),
                message: "the magnitude of step must not exceed 2^32"
            );
        }

        return new LayerSequence(
            start: start,
            step: step,
            seed: seed
        );
    }

    /// <summary>Gets the sequence of triangular numbers: layer sizes <c>1, 2, 3, …</c> with no core.</summary>
    public static LayerSequence Triangular { get; } = new LayerSequence(
        start: 1L,
        step: 1L,
        seed: 0L
    );
    /// <summary>Gets the sequence of pronic numbers <c>n·(n + 1)</c>: layer sizes <c>2, 4, 6, …</c> with no core.</summary>
    public static LayerSequence Pronic { get; } = new LayerSequence(
        start: 2L,
        step: 2L,
        seed: 0L
    );
    /// <summary>Gets the sequence of square numbers: layer sizes <c>1, 3, 5, …</c> with no core — the corner-expanding grid.</summary>
    public static LayerSequence Square { get; } = new LayerSequence(
        start: 1L,
        step: 2L,
        seed: 0L
    );
    /// <summary>Gets the sequence of centered square numbers: a single-index core wrapped by layers of <c>4, 8, 12, …</c> — the taxicab rings of a grid.</summary>
    public static LayerSequence CenteredSquare { get; } = new LayerSequence(
        start: 4L,
        step: 4L,
        seed: 1L
    );
    /// <summary>Gets the sequence of centered hexagonal numbers: a single-index core wrapped by layers of <c>6, 12, 18, …</c> — the rings of a honeycomb.</summary>
    public static LayerSequence CenteredHexagonal { get; } = new LayerSequence(
        start: 6L,
        step: 6L,
        seed: 1L
    );

    /// <summary>Creates the centered polygonal sequence for a polygon with <paramref name="sides"/> sides: a single-index core wrapped by layers of <c>k, 2k, 3k, …</c>.</summary>
    /// <param name="sides">The number of polygon sides; it must be at least <c>3</c>.</param>
    /// <returns>The centered <paramref name="sides"/>-gonal sequence.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="sides"/> is less than <c>3</c> or exceeds the <see cref="Create(long, long, long)"/> step range.</exception>
    public static LayerSequence Centered(long sides) {
        if (3L > sides) {
            throw new ArgumentOutOfRangeException(
                paramName: nameof(sides),
                message: "a polygon has at least 3 sides"
            );
        }

        return Create(
            start: sides,
            step: sides,
            seed: 1L
        );
    }
    /// <summary>Creates a flat sequence: every layer holds exactly <paramref name="size"/> indices around a core of <paramref name="seed"/> — ordinary linear indexing.</summary>
    /// <param name="size">The size of every layer; it must lie in <c>[0, 2⁶²)</c>.</param>
    /// <param name="seed">The size of the core (layer <c>0</c>); it must lie in <c>[0, 2⁶²)</c>.</param>
    /// <returns>The flat sequence with the given layer size and core.</returns>
    /// <exception cref="ArgumentOutOfRangeException">A constant lies outside its documented range.</exception>
    public static LayerSequence Linear(long size, long seed) =>
        Create(
        start: size,
        step: 0L,
        seed: seed
    );
    /// <summary>Creates the polygonal (corner-expanding) sequence for a polygon with <paramref name="sides"/> sides: layer sizes <c>1, k − 1, 2·(k − 2) + 1, …</c> growing by <c>k − 2</c>.</summary>
    /// <param name="sides">The number of polygon sides; it must be at least <c>3</c>.</param>
    /// <returns>The <paramref name="sides"/>-gonal number sequence (triangular for <c>3</c>, square for <c>4</c>).</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="sides"/> is less than <c>3</c> or exceeds the <see cref="Create(long, long, long)"/> step range.</exception>
    public static LayerSequence Polygonal(long sides) {
        if (3L > sides) {
            throw new ArgumentOutOfRangeException(
                paramName: nameof(sides),
                message: "a polygon has at least 3 sides"
            );
        }

        return Create(
            start: 1L,
            step: (sides - 2L),
            seed: 0L
        );
    }

    /// <summary>Computes the total number of indices in the core and the first <paramref name="layerCount"/> layers.</summary>
    /// <param name="layerCount">The number of layers to include; it must be non-negative, and at most <see cref="MaxLayer"/> when the sequence is bounded.</param>
    /// <returns><c>Seed + Start·n + Step·n·(n − 1)/2</c> for <c>n = <paramref name="layerCount"/></c>.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="layerCount"/> is negative, or exceeds <see cref="MaxLayer"/> on a bounded sequence.</exception>
    /// <exception cref="OverflowException">The total exceeds <see cref="long.MaxValue"/>.</exception>
    public long Count(long layerCount) {
        if (
            (0L > layerCount) ||
            (IsBounded && (MaxLayer < layerCount))
        ) {
            throw new ArgumentOutOfRangeException(
                paramName: nameof(layerCount),
                message: "layerCount must be non-negative, and at most MaxLayer when the sequence is bounded"
            );
        }

        checked {
            var n = ((Int128)layerCount);

            return ((long)((((Int128)Seed) + (((Int128)Start) * n)) + (((Int128)Step) * ((n * (n - Int128.One)) / 2))));
        }
    }
    /// <summary>Returns the layer that contains <paramref name="index"/>, in constant time.</summary>
    /// <param name="index">The index to locate; it must be non-negative, and below <see cref="Capacity"/> when the sequence is bounded.</param>
    /// <returns><c>0</c> when <paramref name="index"/> lies in the core; otherwise the unique layer <c>n ≥ 1</c> with <c>Count(n − 1) ≤ <paramref name="index"/> &lt; Count(n)</c>.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="index"/> is negative, or lies beyond the capacity of a bounded sequence.</exception>
    /// <exception cref="OverflowException">The containing layer exceeds <see cref="long.MaxValue"/>.</exception>
    /// <remarks>
    /// Inverts the quadratic prefix sum directly: the largest <c>n</c> with <c>Count(n) ≤ x</c> is
    /// <c>⌊(√((2·Start − Step)² + 8·Step·(x − Seed)) − (2·Start − Step)) / (2·Step)⌋</c>, evaluated entirely in
    /// integer arithmetic. With the exact floor square root the division floors exactly for a positive
    /// <see cref="Step"/>; a negative <see cref="Step"/> can land one layer high and settles with a single
    /// comparison — both verified by exhaustive sweeps against an incremental reference.
    /// </remarks>
    public long LayerOf(long index) {
        ThrowIfOutOfRange(index: index);

        return ((Seed > index)
            ? 0L
            : LocateLayer(index: index));
    }
    /// <summary>Returns the number of indices in <paramref name="layer"/>.</summary>
    /// <param name="layer">The layer to measure; it must be non-negative, and at most <see cref="MaxLayer"/> when the sequence is bounded.</param>
    /// <returns><see cref="Seed"/> for layer <c>0</c>; otherwise <c>Start + (n − 1)·Step</c>.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="layer"/> is negative, or exceeds <see cref="MaxLayer"/> on a bounded sequence.</exception>
    /// <exception cref="OverflowException">The size exceeds <see cref="long.MaxValue"/>.</exception>
    public long LayerSize(long layer) {
        if (
            (0L > layer) ||
            (IsBounded && (MaxLayer < layer))
        ) {
            throw new ArgumentOutOfRangeException(
                paramName: nameof(layer),
                message: "layer must be non-negative, and at most MaxLayer when the sequence is bounded"
            );
        }

        return ((0L == layer)
            ? Seed
            : checked((long)(((Int128)Start) + (((Int128)Step) * (layer - 1L)))));
    }
    /// <summary>Returns the layer that contains <paramref name="index"/> and the offset within it, in constant time.</summary>
    /// <param name="index">The index to locate; it must be non-negative, and below <see cref="Capacity"/> when the sequence is bounded.</param>
    /// <returns>The containing layer and the zero-based offset of <paramref name="index"/> from that layer's first index.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="index"/> is negative, or lies beyond the capacity of a bounded sequence.</exception>
    /// <exception cref="OverflowException">The containing layer exceeds <see cref="long.MaxValue"/>.</exception>
    public LayerLocation Locate(long index) {
        ThrowIfOutOfRange(index: index);

        if (Seed > index) {
            return new LayerLocation(
            Layer: 0L,
            Offset: index
        );
        }

        var layer = LocateLayer(index: index);

        return new LayerLocation(
            Layer: layer,
            Offset: (index - ((long)CountUnchecked(layerCount: (layer - 1L))))
        );
    }
    /// <summary>Locates <paramref name="index"/> without a capacity limit: in-range indices resolve exactly, and indices beyond a bounded sequence's capacity clamp to <see cref="MaxLayer"/> and report their excess.</summary>
    /// <param name="index">The index to project; it must be non-negative.</param>
    /// <returns>
    /// <c>(LayerOf(index), 0, 0)</c> for an in-range index. Beyond capacity, the layer locks at <see cref="MaxLayer"/>,
    /// <see cref="LayerProjection.Overflow"/> counts indices past the boundary linearly (saturating at
    /// <see cref="long.MaxValue"/>), and
    /// <see cref="LayerProjection.Depth"/> reports the imaginary component of the layer equation's complex root —
    /// zero until the index passes the sequence's continuous vertex, then growing with the square root of the excess.
    /// </returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="index"/> is negative.</exception>
    /// <exception cref="OverflowException">The containing layer exceeds <see cref="long.MaxValue"/>.</exception>
    public LayerProjection Project(long index) {
        if (0L > index) {
            throw new ArgumentOutOfRangeException(
                paramName: nameof(index),
                message: "index must be non-negative"
            );
        }

        if (Seed > index) {
            return new LayerProjection(
            Layer: 0L,
            Overflow: 0L,
            Depth: 0L
        );
        }

        var capacity = CapacityLimit;

        if (index < capacity) {
            return new LayerProjection(
            Layer: LocateLayer(index: index),
            Overflow: 0L,
            Depth: 0L
        );
        }

        var depth = 0L;

        if (0L > Step) {
            var b = ((2 * ((Int128)Start)) - Step);
            var discriminant = ((b * b) + ((((Int128)Step) * (index - Seed)) * 8));

            if (Int128.IsNegative(value: discriminant)) {
                depth = ((long)(((UInt128)(-discriminant)).SquareRoot() / ((UInt128)(2L * (-Step)))));
            }
        }

        var overflow = ((index - capacity) + 1);

        return new LayerProjection(
            Layer: MaxLayer,
            Overflow: ((overflow > ((Int128)long.MaxValue))
                ? long.MaxValue
                : ((long)overflow)),
            Depth: depth
        );
    }
}
