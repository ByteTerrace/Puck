using System.Runtime.InteropServices;
using Puck.Maths;

namespace Puck.Physics;

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
/// <summary>Colliders with their <see cref="FixedStaticCollider.WorldBox"/> bounds and <see cref="FixedColliderFrame"/> certificates,
/// computed from the colliders themselves: the only form in which <see cref="FixedStaticContactSolver"/> takes colliders,
/// so every bound it culls by is the collider's own and no caller supplies one. A set is either compiled once over an
/// array or refreshed by its owner with the colliders it recomputes each tick. Its colliders fall in groups, runs that
/// share an owner's key (a moving row's colliders), each with the box its members' bounds lie in, so a query lists its
/// candidates (<see cref="Candidates"/>) by passing over every group its region misses.</summary>
/// <remarks>Invariant: each slot below <c>m_compiled</c> holds the bounds and certificate computed from the collider in
/// that slot. <see cref="Compile"/> and <see cref="RefreshFrom"/> are their only writers, and both keep it: a refresh
/// computes a slot again unless the collider it receives there equals the one already compiled, and a copy takes the
/// bounds with their colliders.</remarks>
public sealed class FixedStaticColliderSet {
    private readonly bool m_refreshable;
    private FixedStaticCollider[] m_colliders;
    private FixedAabb[] m_boxes;
    private FixedColliderFrame[] m_frames;
    private int m_count;
    private int m_compiled;
    // Group g holds slots [m_groupStarts[g], m_groupStarts[g + 1]), keyed by m_groupKeys[g] and bounded by m_groupBoxes[g].
    private int[] m_groupStarts = [0];
    private int[] m_groupKeys = [];
    private FixedAabb[] m_groupBoxes = [];
    private int m_groupCount;

    /// <summary>Initializes a new instance of the <see cref="FixedStaticColliderSet"/> class over an array it keeps.</summary>
    /// <param name="colliders">The colliders, in the order every query visits them; the set reads the array and the
    /// caller must not change it afterwards.</param>
    /// <exception cref="ArgumentNullException"><paramref name="colliders"/> is <see langword="null"/>.</exception>
    public FixedStaticColliderSet(FixedStaticCollider[] colliders) {
        ArgumentNullException.ThrowIfNull(argument: colliders);
        m_colliders = colliders;
        m_boxes = new FixedAabb[colliders.Length];
        m_frames = new FixedColliderFrame[colliders.Length];
        Reserve(count: colliders.Length);
        Compile(colliders: colliders, groups: default);
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
    public ReadOnlySpan<FixedAabb> Boxes => m_boxes.AsSpan(start: 0, length: m_count);
    /// <summary>Gets each collider's oriented frame, at the collider's index.</summary>
    public ReadOnlySpan<FixedColliderFrame> Frames => m_frames.AsSpan(start: 0, length: m_count);
    /// <summary>Gets the number of colliders.</summary>
    public int Count => m_count;

    /// <summary>Replaces the colliders of a set that owns its storage with copies of the given ones and compiles their
    /// bounds, computing them only for a slot whose collider changed; storage grows only past its largest earlier
    /// count.</summary>
    /// <param name="colliders">The colliders, in the order every query visits them.</param>
    /// <param name="groups">Each collider's group key, parallel to <paramref name="colliders"/>, a run of equal keys one
    /// group; empty to hold every collider in one group.</param>
    /// <exception cref="InvalidOperationException">The set was compiled over a caller's array.</exception>
    /// <exception cref="ArgumentException"><paramref name="groups"/> is neither empty nor as long as
    /// <paramref name="colliders"/>.</exception>
    public void Refresh(ReadOnlySpan<FixedStaticCollider> colliders, ReadOnlySpan<int> groups = default) {
        if (!m_refreshable) { throw new InvalidOperationException(message: "A collider set compiled over a caller's array is fixed."); }
        if (!groups.IsEmpty && (groups.Length != colliders.Length)) {
            throw new ArgumentException(message: "A collider set's group keys run parallel to its colliders.", paramName: nameof(groups));
        }
        Reserve(count: colliders.Length);
        Compile(colliders: colliders, groups: groups);
    }
    /// <summary>Replaces the colliders of a set that owns its storage with another set's, less every group whose key a
    /// filter refuses, taking each kept collider's bounds and group as compiled there.</summary>
    /// <param name="source">The set to copy.</param>
    /// <param name="keeps">Returns whether a group key is kept.</param>
    /// <exception cref="InvalidOperationException">The set was compiled over a caller's array.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="source"/> or <paramref name="keeps"/> is
    /// <see langword="null"/>.</exception>
    public void RefreshFrom(FixedStaticColliderSet source, Func<int, bool> keeps) {
        if (!m_refreshable) { throw new InvalidOperationException(message: "A collider set compiled over a caller's array is fixed."); }
        ArgumentNullException.ThrowIfNull(argument: source);
        ArgumentNullException.ThrowIfNull(argument: keeps);
        Reserve(count: source.m_count);

        var count = 0;
        var groups = 0;

        for (var group = 0; (group < source.m_groupCount); group++) {
            if (!keeps(arg: source.m_groupKeys[group])) { continue; }
            var start = source.m_groupStarts[group];
            var length = (source.m_groupStarts[(group + 1)] - start);

            source.m_colliders.AsSpan(start: start, length: length).CopyTo(destination: m_colliders.AsSpan(start: count));
            source.m_boxes.AsSpan(start: start, length: length).CopyTo(destination: m_boxes.AsSpan(start: count));
            source.m_frames.AsSpan(start: start, length: length).CopyTo(destination: m_frames.AsSpan(start: count));
            m_groupStarts[groups] = count;
            m_groupKeys[groups] = source.m_groupKeys[group];
            m_groupBoxes[groups] = source.m_groupBoxes[group];
            groups++;
            count += length;
        }
        m_groupStarts[groups] = count;
        m_groupCount = groups;
        m_count = count;
        m_compiled = Math.Max(val1: m_compiled, val2: count);
    }
    /// <summary>Lists the colliders whose bounds meet a region grown by the contact skin, ascending and distinct, for a
    /// query whose body stays inside <paramref name="region"/>: a group whose box misses the region is passed over
    /// whole. The solver checks every collider once the body's bounds leave the region, so the region trades only
    /// speed, never the result.</summary>
    /// <param name="region">The region a query's body stays inside.</param>
    /// <param name="skin">The contact skin a collider acts across.</param>
    /// <param name="scratch">The caller's list the indices are written into; the result reads it until the next call.</param>
    /// <returns>The candidates, or the default value (every collider) for an empty set.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="scratch"/> is <see langword="null"/>.</exception>
    public FixedColliderCandidates Candidates(FixedAabb region, FixedQ4816 skin, List<int> scratch) {
        ArgumentNullException.ThrowIfNull(argument: scratch);
        if (m_count == 0) { return default; }

        var reach = region.Grow(margin: skin);

        scratch.Clear();
        for (var group = 0; (group < m_groupCount); group++) {
            if (!m_groupBoxes[group].Intersects(other: in reach)) { continue; }
            for (var index = m_groupStarts[group]; (index < m_groupStarts[(group + 1)]); index++) {
                if (m_boxes[index].Intersects(other: in reach)) { scratch.Add(item: index); }
            }
        }
        return new FixedColliderCandidates(indices: CollectionsMarshal.AsSpan(list: scratch), region: region);
    }

    // Grows storage to hold a count; new collider storage holds no compiled slot.
    private void Reserve(int count) {
        if (m_colliders.Length < count) {
            m_colliders = new FixedStaticCollider[count];
            m_boxes = new FixedAabb[count];
            m_frames = new FixedColliderFrame[count];
            m_compiled = 0;
        }
        if (m_groupStarts.Length <= count) {
            m_groupStarts = new int[(count + 1)];
            m_groupKeys = new int[count];
            m_groupBoxes = new FixedAabb[count];
        }
    }
    private void Compile(ReadOnlySpan<FixedStaticCollider> colliders, ReadOnlySpan<int> groups) {
        var count = colliders.Length;
        var group = -1;

        for (var index = 0; (index < count); index++) {
            if ((index >= m_compiled) || (m_colliders[index] != colliders[index])) {
                m_colliders[index] = colliders[index];
                m_boxes[index] = colliders[index].WorldBox();
                m_frames[index] = FixedColliderFrame.Of(collider: in colliders[index]);
            }
            var key = (groups.IsEmpty ? 0 : groups[index]);

            if ((group < 0) || (key != m_groupKeys[group])) {
                group++;
                m_groupStarts[group] = index;
                m_groupKeys[group] = key;
                m_groupBoxes[group] = FixedAabb.Empty;
            }
            m_groupBoxes[group] = m_groupBoxes[group].Enclose(other: m_boxes[index]);
        }
        m_groupCount = (group + 1);
        m_groupStarts[m_groupCount] = count;
        m_count = count;
        m_compiled = Math.Max(val1: m_compiled, val2: count);
    }
}
