using Puck.Maths;

namespace Puck.Physics;

/// <summary>A world-axis box about a static collider that the collider's exact query never pushes a body outside of:
/// six raw Q48.16 words, read without touching the collider record.</summary>
/// <param name="Center">The box's world center: the collider's <see cref="FixedStaticCollider.Center"/>.</param>
/// <param name="Extent">The collider's padded <see cref="FixedStaticCollider.WorldHalfExtent"/>, or a negative half-extent
/// for a collider without bounds.</param>
public readonly record struct FixedColliderBox(FixedVector3 Center, FixedVector3 Extent) {
    private static readonly FixedVector3 NoBounds = new(X: -FixedQ4816.One, Y: -FixedQ4816.One, Z: -FixedQ4816.One);

    /// <summary>Gets whether the collider has bounds; a half-space or an unbounded field has none and may meet any
    /// body.</summary>
    public bool IsBounded => (Extent.X >= FixedQ4816.Zero);

    /// <summary>Creates the box of one collider.</summary>
    /// <param name="collider">The collider.</param>
    /// <returns>The collider's centre and padded world half-extent, or a box without bounds when the collider has
    /// none.</returns>
    public static FixedColliderBox Of(in FixedStaticCollider collider) =>
        new(Center: collider.Center, Extent: (collider.WorldHalfExtent() ?? NoBounds));
}
/// <summary>An oriented box about a turned static collider's centre: the collider's unit axes in world space and its
/// padded half-extent along each, outside which the collider's exact query never pushes a body. A collider whose
/// world-axis box is already as tight (a sphere, an axis-aligned box, a box turned onto the world axes), or that has no
/// finite frame (a half-space) or reports unproved contact (a field), has none.</summary>
/// <param name="AxisX">The collider's local X axis in world space.</param>
/// <param name="AxisY">The collider's local Y axis in world space.</param>
/// <param name="AxisZ">The collider's local Z axis in world space.</param>
/// <param name="Reach">The collider's padded half-extent along each of its own axes, or a negative value when the
/// collider has no frame.</param>
public readonly record struct FixedColliderFrame(FixedVector3 AxisX, FixedVector3 AxisY, FixedVector3 AxisZ, FixedVector3 Reach) {
    private static readonly FixedColliderFrame None = new(AxisX: default, AxisY: default, AxisZ: default,
        Reach: new FixedVector3(X: -FixedQ4816.One, Y: -FixedQ4816.One, Z: -FixedQ4816.One));
    // Covers the solid's own rounding in a contact query, beyond the padding already in Reach.
    private static readonly FixedQ4816 SeparationMargin = (FixedQ4816.One / FixedQ4816.FromInteger(value: 256));
    // The certificate projects onto rounded axes while the exact query rotates into its own rounded frame; each errs by
    // a few units of the axes' last place times the distance projected. One part in 1024 of the L1 distance and body
    // extent is more than ten times both together.
    private const int RelativeMarginShift = 10;

    /// <summary>Gets whether the collider has a frame; a collider without one is never separated by it.</summary>
    public bool IsOriented => (Reach.X >= FixedQ4816.Zero);

    /// <summary>Creates the frame of one collider.</summary>
    /// <param name="collider">The collider.</param>
    /// <returns>The collider's world axes and padded local half-extent, or a frame that separates nothing when the
    /// collider has none.</returns>
    public static FixedColliderFrame Of(in FixedStaticCollider collider) {
        if (collider.Kind is not (FixedStaticColliderKind.OrientedBox or FixedStaticColliderKind.RoundedBox or FixedStaticColliderKind.ChamferedBox
            or FixedStaticColliderKind.Superellipsoid or FixedStaticColliderKind.Cylinder)) {
            return None;
        }
        var axisX = collider.Rotation.Rotate(vector: FixedVector3.UnitX);
        var axisY = collider.Rotation.Rotate(vector: FixedVector3.UnitY);
        var axisZ = collider.Rotation.Rotate(vector: FixedVector3.UnitZ);

        if (FixedStaticCollider.IsWorldAxis(axis: axisX) && FixedStaticCollider.IsWorldAxis(axis: axisY) &&
            FixedStaticCollider.IsWorldAxis(axis: axisZ)) {
            return None;
        }
        var padding = FixedStaticCollider.BoundsPadding;

        return new FixedColliderFrame(AxisX: axisX, AxisY: axisY, AxisZ: axisZ,
            Reach: (collider.LocalHalfExtent() + new FixedVector3(X: padding, Y: padding, Z: padding)));
    }

    /// <summary>Returns whether a body's world-axis bounds lie further than the contact skin outside this box along one
    /// of its axes, so the collider's exact query would find no push; a collider without a frame separates nothing.</summary>
    /// <param name="offset">The body bounds' world center less the collider's center.</param>
    /// <param name="bodyExtent">The body bounds' world half-extent.</param>
    /// <param name="skin">The contact skin.</param>
    /// <returns><see langword="true"/> when the collider cannot push the body.</returns>
    public bool Separates(in FixedVector3 offset, in FixedVector3 bodyExtent, FixedQ4816 skin) {
        if (!IsOriented) { return false; }
        var spread = ((((Int128.Abs(value: offset.X.Value) + Int128.Abs(value: offset.Y.Value)) + Int128.Abs(value: offset.Z.Value)) +
            ((bodyExtent.X.Value + bodyExtent.Y.Value) + bodyExtent.Z.Value)) >> RelativeMarginShift);
        var slack = ((skin + SeparationMargin) + FixedQ4816.FromRawBits(value: ((long)Int128.Min(x: spread, y: (long.MaxValue >> 2)))));

        return (Gap(axis: AxisX, reach: Reach.X, offset: in offset, bodyExtent: in bodyExtent) > slack) ||
            (Gap(axis: AxisY, reach: Reach.Y, offset: in offset, bodyExtent: in bodyExtent) > slack) ||
            (Gap(axis: AxisZ, reach: Reach.Z, offset: in offset, bodyExtent: in bodyExtent) > slack);

        // How far the body's box lies beyond the collider's slab along one axis: the box's projected half-width is the
        // absolute axis against its half-extent.
        static FixedQ4816 Gap(FixedVector3 axis, FixedQ4816 reach, in FixedVector3 offset, in FixedVector3 bodyExtent) =>
            (FixedQ4816.Abs(value: FixedVector3.Dot(left: axis, right: offset)) - (reach + FixedVector3.Dot(left: FixedVector3.Abs(value: axis), right: bodyExtent)));
    }
}
/// <summary>Colliders with their <see cref="FixedColliderBox"/> bounds and <see cref="FixedColliderFrame"/> certificates,
/// computed from the colliders themselves: the only form in which <see cref="FixedStaticContactSolver"/> takes colliders,
/// so every bound it culls by is the collider's own and no caller supplies one. A set is either compiled once over an
/// array or refreshed by its owner with the colliders it recomputes each tick.</summary>
public sealed class FixedStaticColliderSet {
    private readonly bool m_refreshable;
    private FixedStaticCollider[] m_colliders;
    private FixedColliderBox[] m_boxes;
    private FixedColliderFrame[] m_frames;
    private int m_count;

    /// <summary>Initializes a new instance of the <see cref="FixedStaticColliderSet"/> class over an array it keeps.</summary>
    /// <param name="colliders">The colliders, in the order every query visits them; the set reads the array and the
    /// caller must not change it afterwards.</param>
    /// <exception cref="ArgumentNullException"><paramref name="colliders"/> is <see langword="null"/>.</exception>
    public FixedStaticColliderSet(FixedStaticCollider[] colliders) {
        ArgumentNullException.ThrowIfNull(argument: colliders);
        m_colliders = colliders;
        m_boxes = new FixedColliderBox[colliders.Length];
        m_frames = new FixedColliderFrame[colliders.Length];
        Compile(count: colliders.Length);
    }
    /// <summary>Initializes a new, empty instance of the <see cref="FixedStaticColliderSet"/> class that owns its storage
    /// and that <see cref="Refresh"/> fills.</summary>
    public FixedStaticColliderSet() {
        m_colliders = [];
        m_boxes = [];
        m_frames = [];
        m_refreshable = true;
    }

    /// <summary>Gets the set of no colliders.</summary>
    public static FixedStaticColliderSet Empty { get; } = new(colliders: []);

    /// <summary>Gets the colliders, in query order.</summary>
    public ReadOnlySpan<FixedStaticCollider> Colliders => m_colliders.AsSpan(start: 0, length: m_count);
    /// <summary>Gets each collider's bounds, at the collider's index.</summary>
    public ReadOnlySpan<FixedColliderBox> Boxes => m_boxes.AsSpan(start: 0, length: m_count);
    /// <summary>Gets each collider's oriented frame, at the collider's index.</summary>
    public ReadOnlySpan<FixedColliderFrame> Frames => m_frames.AsSpan(start: 0, length: m_count);
    /// <summary>Gets the number of colliders.</summary>
    public int Count => m_count;

    /// <summary>Replaces the colliders of a set that owns its storage with copies of the given ones and compiles their
    /// bounds; storage grows only past its largest earlier count.</summary>
    /// <param name="colliders">The colliders, in the order every query visits them.</param>
    /// <exception cref="InvalidOperationException">The set was compiled over a caller's array.</exception>
    public void Refresh(ReadOnlySpan<FixedStaticCollider> colliders) {
        if (!m_refreshable) { throw new InvalidOperationException(message: "A collider set compiled over a caller's array is fixed."); }
        if (m_colliders.Length < colliders.Length) {
            m_colliders = new FixedStaticCollider[colliders.Length];
            m_boxes = new FixedColliderBox[colliders.Length];
            m_frames = new FixedColliderFrame[colliders.Length];
        }
        colliders.CopyTo(destination: m_colliders);
        Compile(count: colliders.Length);
    }

    private void Compile(int count) {
        for (var index = 0; (index < count); index++) {
            m_boxes[index] = FixedColliderBox.Of(collider: in m_colliders[index]);
            m_frames[index] = FixedColliderFrame.Of(collider: in m_colliders[index]);
        }
        m_count = count;
    }
}
