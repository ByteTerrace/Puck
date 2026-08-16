namespace Puck.Maths;

/// <summary>
/// The Hilbert space-filling curve: a bijection between a 1D distance along the curve and a 2D grid point that preserves
/// locality — consecutive distances always map to grid neighbours, so nearby points stay near in the linear order.
/// Exact integer arithmetic, so <see cref="Encode(int, uint, uint)"/> and <see cref="Decode(int, ulong)"/> are perfect
/// inverses on every machine.
/// </summary>
/// <remarks>
/// The grid is <c>2^order</c> square and a distance runs over <c>[0, 4^order)</c>. Use it to lay out 2D data in 1D
/// with spatial locality — cache-coherent chunk and tile ordering, spatial hashing, texture swizzling — where nearby
/// cells must stay near in the linear order. Contrast <see cref="BinaryIntegerFunctions.BitwisePair{TSource, TResult}"/>,
/// the Morton (Z-order) curve, which is cheaper but jumps across the grid at every power-of-two boundary. Valid range:
/// <c>order</c> in <c>[1, 31]</c>, coordinates in <c>[0, 2^order)</c>.
/// </remarks>
public static class HilbertCurve {
    /// <summary>Reflects a point into the canonical orientation of its quadrant, the recursive step both directions share.</summary>
    /// <param name="side">The side length of the current sub-square.</param>
    /// <param name="x">The x coordinate to transform.</param>
    /// <param name="y">The y coordinate to transform.</param>
    /// <param name="quadrantX">The x quadrant bit at this level.</param>
    /// <param name="quadrantY">The y quadrant bit at this level.</param>
    private static void Rotate(uint side, ref uint x, ref uint y, uint quadrantX, uint quadrantY) {
        if (quadrantY == 0U) {
            if (quadrantX == 1U) {
                x = ((side - 1U) - x);
                y = ((side - 1U) - y);
            }

            (x, y) = (y, x);
        }
    }
    /// <summary>Validates the order and returns the grid side <c>2^order</c>, the bound both directions measure against.</summary>
    /// <param name="order">The curve order, which must lie in <c>[1, 31]</c>.</param>
    /// <returns>The grid side length.</returns>
    /// <remarks>The order is checked before any shift, because C# masks a shift count to the operand's width — an
    /// unvalidated order of 32 would silently become a shift of zero, collapsing every input onto the origin instead
    /// of throwing. Validating first keeps the curve's bijection honest: an out-of-range order, coordinate, or
    /// distance is refused rather than silently aliased to a valid one.</remarks>
    private static uint ValidOrderBound(int order) {
        ArgumentOutOfRangeException.ThrowIfLessThan(
            value: order,
            other: 1,
            paramName: nameof(order)
        );
        ArgumentOutOfRangeException.ThrowIfGreaterThan(
            value: order,
            other: 31,
            paramName: nameof(order)
        );

        return (1U << order);
    }

    /// <summary>Returns the grid point at a distance along the curve — the inverse of <see cref="Encode(int, uint, uint)"/>.</summary>
    /// <param name="order">The curve order; the grid is <c>2^order</c> square. In <c>[1, 31]</c>.</param>
    /// <param name="distance">The distance along the curve, in <c>[0, 4^order)</c>.</param>
    /// <returns>The grid point at that distance.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="order"/> is outside <c>[1, 31]</c>, or
    /// <paramref name="distance"/> is outside <c>[0, 4^order)</c>.</exception>
    public static (uint X, uint Y) Decode(int order, ulong distance) {
        var bound = ValidOrderBound(order: order);

        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(
            value: distance,
            other: (1UL << (order << 1)),
            paramName: nameof(distance)
        );

        var x = 0U;
        var y = 0U;

        for (var side = 1U; (side < bound); side <<= 1) {
            var quadrantX = 1U & ((uint)(distance >> 1));
            var quadrantY = 1U & ((uint)(distance ^ quadrantX));

            Rotate(
                quadrantX: quadrantX,
                quadrantY: quadrantY,
                side: side,
                x: ref x,
                y: ref y
            );
            x += (side * quadrantX);
            y += (side * quadrantY);
            distance >>= 2;
        }

        return (x, y);
    }
    /// <summary>Returns the distance along the curve at a grid point.</summary>
    /// <param name="order">The curve order; the grid is <c>2^order</c> square. In <c>[1, 31]</c>.</param>
    /// <param name="x">The point's x coordinate, in <c>[0, 2^order)</c>.</param>
    /// <param name="y">The point's y coordinate, in <c>[0, 2^order)</c>.</param>
    /// <returns>The point's distance along the curve, in <c>[0, 4^order)</c>; consecutive distances are grid neighbours.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="order"/> is outside <c>[1, 31]</c>, or a
    /// coordinate is outside <c>[0, 2^order)</c>.</exception>
    public static ulong Encode(int order, uint x, uint y) {
        var bound = ValidOrderBound(order: order);

        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(
            value: x,
            other: bound,
            paramName: nameof(x)
        );
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(
            value: y,
            other: bound,
            paramName: nameof(y)
        );

        var distance = 0UL;

        // Encode reflects against the FULL grid each level (decode reflects against the growing sub-square) — the two
        // directions are asymmetric, and only this order recovers the curve. The quadrant bits are extracted by shift
        // (not a compare) so the hot loop stays branchless apart from the shared reflection.
        for (var level = (order - 1); (level >= 0); --level) {
            var quadrantX = (x >> level) & 1U;
            var quadrantY = (y >> level) & 1U;
            var span = (1U << level);

            distance += ((((ulong)span) * span) * ((3U * quadrantX) ^ quadrantY));
            Rotate(
                quadrantX: quadrantX,
                quadrantY: quadrantY,
                side: bound,
                x: ref x,
                y: ref y
            );
        }

        return distance;
    }
}
