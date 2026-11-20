namespace Puck.Maths;

/// <summary>A closed world-axis box over <see cref="FixedQ4816"/> coordinates, held as its least and greatest corners,
/// and the one owner of the axis-aligned box queries the simulation asks: overlap, containment, enclosure and growth.
/// </summary>
/// <param name="Minimum">The least corner.</param>
/// <param name="Maximum">The greatest corner; a box whose maximum lies below its minimum on some axis is empty.</param>
/// <remarks>
/// <para>Corners are exact for every box: a box built from a centre and half-extent keeps both (its
/// <see cref="Center"/> and <see cref="Extent"/> read back exactly), and enclosure never rounds. The overlap and
/// containment tests are corner comparisons, which no operand can overflow.</para>
/// <para>Construction and growth saturate at the carrier's extremes rather than wrapping, so a box never covers less
/// than it was asked to. <see cref="Everywhere"/> spans the whole carrier, for a solid without bounds, and every
/// non-empty box meets it; <see cref="Empty"/> meets nothing and is the identity of <see cref="Enclose(in FixedAabb)"/>.
/// The <see langword="default"/> value is the single point at the origin, not <see cref="Empty"/>.</para>
/// </remarks>
public readonly record struct FixedAabb(FixedVector3 Minimum, FixedVector3 Maximum) {
    /// <summary>Gets the empty box: it meets and contains nothing, and enclosing it changes no box.</summary>
    public static FixedAabb Empty { get; } = new(
        Maximum: new FixedVector3(X: FixedQ4816.MinValue, Y: FixedQ4816.MinValue, Z: FixedQ4816.MinValue),
        Minimum: new FixedVector3(X: FixedQ4816.MaxValue, Y: FixedQ4816.MaxValue, Z: FixedQ4816.MaxValue)
    );
    /// <summary>Gets the box spanning the whole carrier: the bounds of a solid without any (a half-space), which every
    /// non-empty box meets.</summary>
    public static FixedAabb Everywhere { get; } = new(
        Maximum: new FixedVector3(X: FixedQ4816.MaxValue, Y: FixedQ4816.MaxValue, Z: FixedQ4816.MaxValue),
        Minimum: new FixedVector3(X: FixedQ4816.MinValue, Y: FixedQ4816.MinValue, Z: FixedQ4816.MinValue)
    );

    /// <summary>Gets the box's centre: the midpoint of its corners on each axis, rounded to nearest with ties to even,
    /// formed without overflow; exact for a box built from a centre and half-extent. Meaningless for an empty box.</summary>
    public FixedVector3 Center => new(
        X: FixedQ4816.FromRawBits(value: Midpoint(low: Minimum.X.Value, high: Maximum.X.Value)),
        Y: FixedQ4816.FromRawBits(value: Midpoint(low: Minimum.Y.Value, high: Maximum.Y.Value)),
        Z: FixedQ4816.FromRawBits(value: Midpoint(low: Minimum.Z.Value, high: Maximum.Z.Value))
    );
    /// <summary>Gets the box's half-extent about <see cref="Center"/>: on each axis the farther corner's distance from
    /// the centre, so the centre and half-extent together cover the box. Exact for a box built from a centre and
    /// half-extent; one raw past half the span when the corners' sum is odd; saturated at
    /// <see cref="FixedQ4816.MaxValue"/> for a span wider than the carrier's half. Meaningless for an empty box.</summary>
    public FixedVector3 Extent => new(
        X: FixedQ4816.FromRawBits(value: HalfSpan(low: Minimum.X.Value, high: Maximum.X.Value)),
        Y: FixedQ4816.FromRawBits(value: HalfSpan(low: Minimum.Y.Value, high: Maximum.Y.Value)),
        Z: FixedQ4816.FromRawBits(value: HalfSpan(low: Minimum.Z.Value, high: Maximum.Z.Value))
    );
    /// <summary>Gets a value indicating whether the box holds no point: its maximum lies below its minimum on some
    /// axis.</summary>
    public bool IsEmpty => ((Maximum.X < Minimum.X) || (Maximum.Y < Minimum.Y) || (Maximum.Z < Minimum.Z));
    /// <summary>Gets a value indicating whether the box spans the whole carrier, as <see cref="Everywhere"/> does.</summary>
    public bool IsEverywhere => (this == Everywhere);

    /// <summary>Creates the box about a centre with a half-extent on each axis.</summary>
    /// <param name="center">The box's centre.</param>
    /// <param name="extent">The box's half-extent on each axis, at least zero; a negative half-extent makes the box
    /// empty on that axis.</param>
    /// <returns>The box from <c>center − extent</c> to <c>center + extent</c>, each corner saturated at the carrier's
    /// extremes.</returns>
    public static FixedAabb FromCenter(FixedVector3 center, FixedVector3 extent) => new(
        Maximum: new FixedVector3(
            X: FixedQ4816.FromRawBits(value: FixedSaturate.ToInt64(value: (((Int128)center.X.Value) + extent.X.Value))),
            Y: FixedQ4816.FromRawBits(value: FixedSaturate.ToInt64(value: (((Int128)center.Y.Value) + extent.Y.Value))),
            Z: FixedQ4816.FromRawBits(value: FixedSaturate.ToInt64(value: (((Int128)center.Z.Value) + extent.Z.Value)))
        ),
        Minimum: new FixedVector3(
            X: FixedQ4816.FromRawBits(value: FixedSaturate.ToInt64(value: (((Int128)center.X.Value) - extent.X.Value))),
            Y: FixedQ4816.FromRawBits(value: FixedSaturate.ToInt64(value: (((Int128)center.Y.Value) - extent.Y.Value))),
            Z: FixedQ4816.FromRawBits(value: FixedSaturate.ToInt64(value: (((Int128)center.Z.Value) - extent.Z.Value)))
        )
    );
    /// <summary>Creates the cube about a centre with one half-extent on every axis: the bounds of a sphere.</summary>
    /// <param name="center">The cube's centre.</param>
    /// <param name="radius">The half-extent on every axis, at least zero.</param>
    /// <returns><see cref="FromCenter"/> of the centre and the radius on all three axes.</returns>
    public static FixedAabb Around(FixedVector3 center, FixedQ4816 radius) =>
        FromCenter(center: center, extent: new FixedVector3(X: radius, Y: radius, Z: radius));
    /// <summary>Creates the world-axis box about an oriented box.</summary>
    /// <param name="center">The oriented box's centre.</param>
    /// <param name="rotation">The unit box-to-world rotation.</param>
    /// <param name="halfExtent">The half-extent along the box's own axes, at least zero.</param>
    /// <returns><see cref="FromCenter"/> of the centre and <see cref="OrientedHalfExtent(in FixedQuaternion, FixedVector3)"/>.</returns>
    public static FixedAabb AboutOriented(FixedVector3 center, in FixedQuaternion rotation, FixedVector3 halfExtent) =>
        FromCenter(center: center, extent: OrientedHalfExtent(halfExtent: halfExtent, rotation: in rotation));
    /// <summary>Returns the world-axis half-extent of a box whose own axes point along three world directions: on each
    /// world axis, <c>|axisX|·h.X + |axisY|·h.Y + |axisZ|·h.Z</c> of that axis's components.</summary>
    /// <param name="axisX">The world image of the box's local +X axis; its length scales the extent.</param>
    /// <param name="axisY">The world image of the box's local +Y axis.</param>
    /// <param name="axisZ">The world image of the box's local +Z axis.</param>
    /// <param name="halfExtent">The half-extent along the box's own axes.</param>
    /// <returns>The half-extent: each product rounded once to nearest with ties to even and the sums exact, wrapping
    /// like the carrier's bare operators.</returns>
    public static FixedVector3 OrientedHalfExtent(FixedVector3 axisX, FixedVector3 axisY, FixedVector3 axisZ, FixedVector3 halfExtent) =>
        (((FixedVector3.Abs(value: axisX) * halfExtent.X) + (FixedVector3.Abs(value: axisY) * halfExtent.Y)) + (FixedVector3.Abs(value: axisZ) * halfExtent.Z));
    /// <summary>Returns the world-axis half-extent of a box turned by a rotation.</summary>
    /// <param name="rotation">The unit box-to-world rotation.</param>
    /// <param name="halfExtent">The half-extent along the box's own axes.</param>
    /// <returns><see cref="OrientedHalfExtent(FixedVector3, FixedVector3, FixedVector3, FixedVector3)"/> of the three unit
    /// axes the rotation turns.</returns>
    public static FixedVector3 OrientedHalfExtent(in FixedQuaternion rotation, FixedVector3 halfExtent) => OrientedHalfExtent(
        axisX: rotation.Rotate(vector: FixedVector3.UnitX),
        axisY: rotation.Rotate(vector: FixedVector3.UnitY),
        axisZ: rotation.Rotate(vector: FixedVector3.UnitZ),
        halfExtent: halfExtent
    );

    /// <summary>Returns whether a point lies in the closed box.</summary>
    /// <param name="point">The point.</param>
    /// <returns><see langword="true"/> when every coordinate lies between the corners, either included.</returns>
    public bool Contains(FixedVector3 point) =>
        ((Minimum.X <= point.X) && (point.X <= Maximum.X) &&
        (Minimum.Y <= point.Y) && (point.Y <= Maximum.Y) &&
        (Minimum.Z <= point.Z) && (point.Z <= Maximum.Z));
    /// <summary>Returns whether another non-empty box lies wholly in this closed box.</summary>
    /// <param name="inner">The box to test, not empty.</param>
    /// <returns><see langword="true"/> when both of <paramref name="inner"/>'s corners lie in this box.</returns>
    public bool Contains(in FixedAabb inner) => (Contains(point: inner.Minimum) && Contains(point: inner.Maximum));
    /// <summary>Returns the smallest box holding this box and another.</summary>
    /// <param name="other">The other box; <see cref="Empty"/> leaves this box unchanged.</param>
    /// <returns>The componentwise least minimum and greatest maximum, exact.</returns>
    public FixedAabb Enclose(in FixedAabb other) => new(
        Maximum: FixedVector3.Max(left: Maximum, right: other.Maximum),
        Minimum: FixedVector3.Min(left: Minimum, right: other.Minimum)
    );
    /// <summary>Returns the smallest box holding this box and a point.</summary>
    /// <param name="point">The point.</param>
    /// <returns>The componentwise least minimum and greatest maximum, exact; the point alone when this box is
    /// <see cref="Empty"/>.</returns>
    public FixedAabb Enclose(FixedVector3 point) => new(
        Maximum: FixedVector3.Max(left: Maximum, right: point),
        Minimum: FixedVector3.Min(left: Minimum, right: point)
    );
    /// <summary>Returns the box grown by a margin on every side.</summary>
    /// <param name="margin">The margin, in metres; a negative margin shrinks the box.</param>
    /// <returns>The box with each corner moved outward by the margin and saturated at the carrier's extremes; an empty
    /// box grown stays empty only while the margin does not close it.</returns>
    public FixedAabb Grow(FixedQ4816 margin) => Grow(margin: new FixedVector3(X: margin, Y: margin, Z: margin));
    /// <summary>Returns the box grown by a margin on each axis.</summary>
    /// <param name="margin">The margin on each axis, in metres; a negative component shrinks the box on that axis.</param>
    /// <returns>The box with each corner moved outward by the margin and saturated at the carrier's extremes.</returns>
    public FixedAabb Grow(FixedVector3 margin) => new(
        Maximum: FixedSaturate.Add(left: Maximum, right: margin),
        Minimum: new FixedVector3(
            X: FixedQ4816.FromRawBits(value: FixedSaturate.ToInt64(value: (((Int128)Minimum.X.Value) - margin.X.Value))),
            Y: FixedQ4816.FromRawBits(value: FixedSaturate.ToInt64(value: (((Int128)Minimum.Y.Value) - margin.Y.Value))),
            Z: FixedQ4816.FromRawBits(value: FixedSaturate.ToInt64(value: (((Int128)Minimum.Z.Value) - margin.Z.Value)))
        )
    );
    /// <summary>Returns whether two closed boxes share a point.</summary>
    /// <param name="other">The other box.</param>
    /// <returns><see langword="true"/> when the boxes overlap or touch on every axis; <see langword="false"/> when
    /// either is empty.</returns>
    public bool Intersects(in FixedAabb other) =>
        ((FixedQ4816.Max(x: Minimum.X, y: other.Minimum.X) <= FixedQ4816.Min(x: Maximum.X, y: other.Maximum.X)) &&
        (FixedQ4816.Max(x: Minimum.Y, y: other.Minimum.Y) <= FixedQ4816.Min(x: Maximum.Y, y: other.Maximum.Y)) &&
        (FixedQ4816.Max(x: Minimum.Z, y: other.Minimum.Z) <= FixedQ4816.Min(x: Maximum.Z, y: other.Maximum.Z)));

    // The floor average (low & high) + ((low ^ high) >> 1) cannot overflow; an odd sum's half rounds to the even
    // neighbour.
    private static long Midpoint(long low, long high) {
        var floor = ((low & high) + ((low ^ high) >> 1));

        return ((((low ^ high) & floor & 1L) != 0L) ? (floor + 1L) : floor);
    }
    // Both distances are at least zero for a non-empty axis, so they fit an unsigned word.
    private static long HalfSpan(long low, long high) {
        var center = Midpoint(low: low, high: high);
        var reach = Math.Max(val1: unchecked((ulong)(center - low)), val2: unchecked((ulong)(high - center)));

        return ((reach > long.MaxValue) ? long.MaxValue : ((long)reach));
    }
}
