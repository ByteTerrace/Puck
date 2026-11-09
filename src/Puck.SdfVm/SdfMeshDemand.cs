using System.Numerics;
using Puck.SignedDistance;

namespace Puck.SdfVm;

/// <summary>
/// The demand points one residency's presentation orders its mesh residency by: the eye of every view its latest frame
/// renders (each seat's camera, the operator's, every layout slot's, every camera filmed for a screen), each with the
/// pixels a metre covers one metre in front of it. Everything whose box projects past a pixel at a demand point is
/// demanded, the nearest first by the pixels a metre covers there (<see cref="Priority"/>), so the room a seat stands in
/// lands before the rest and a coarse camera's surroundings rank below a fine view's at the same distance; the bake takes
/// lattices and its off-thread work (<see cref="SdfMeshPathBake"/>) and the mesh region its owed meshes
/// (<see cref="SdfWorldTables"/>) in that order.
/// <para>Presentation alone reads it: nothing here reaches simulation state, and a mesh is the same whichever order it was
/// baked or loaded in. A view another residency renders (<see cref="SdfViewSnapshot.Elsewhere"/>) or a hidden one demands
/// nothing here; a frame with no demand point gives everything priority zero, which leaves the plan's order.</para>
/// <para>Written on the frame's thread; a read from another thread, as a diagnostic's, can see a frame's points
/// mid-update.</para>
/// </summary>
public sealed class SdfMeshDemand {
    /// <summary>The nearest a demand point is counted to a box, in metres: one it stands inside, or nearer than this, ranks
    /// as though this far off, alike with every other such box.</summary>
    public const float NearDistance = 0.25f;

    private Vector3[] m_points = new Vector3[4];
    private float[] m_focal = new float[4];

    /// <summary>Gets how many demand points the latest frame named.</summary>
    public int Count { get; private set; }

    /// <summary>Reads the demand points from a frame's views: each view's eye, and its pixels a metre one metre in front of
    /// it, from its own image's height (<see cref="SdfViewSnapshot.DemandHeight"/>) or else its share of the output's
    /// height, and its field of view. The points keep the views' order, so the first is the primary seat's while that seat
    /// is presented here.</summary>
    /// <param name="frame">The frame.</param>
    /// <param name="height">The output's height in display pixels.</param>
    /// <exception cref="ArgumentNullException"><paramref name="frame"/> is <see langword="null"/>.</exception>
    public void Observe(SdfFrame frame, uint height) {
        ArgumentNullException.ThrowIfNull(argument: frame);

        var views = frame.Views;
        var count = 0;

        if (m_points.Length < views.Count) {
            m_points = new Vector3[views.Count];
            m_focal = new float[views.Count];
        }
        for (var index = 0; (index < views.Count); index++) {
            var view = views[index];
            var camera = view.Camera;
            var tan = camera.TanHalfFieldOfView;

            if (view.Elsewhere || !(view.Region.Height > 0f) || !(tan > 0f) || !float.IsFinite(f: camera.Position.X)) { continue; }
            m_points[count] = camera.Position;
            m_focal[count] = ((MathF.Max(x: 1f, y: ((view.DemandHeight > 0) ? view.DemandHeight : (view.Region.Height * height))) * 0.5f) / tan);
            count++;
        }
        Count = count;
    }
    /// <summary>Adds a demand point after those the latest frame named (or after a <see cref="Clear"/>): an eye with its
    /// pixels a metre one metre in front of it, for a plan made with no frame (an offline audit at a chosen eye).</summary>
    /// <param name="eye">The eye's position, in world space.</param>
    /// <param name="height">The view's height in display pixels; at least one.</param>
    /// <param name="tanHalfFieldOfView">The tangent of half the view's vertical field of view; positive.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="tanHalfFieldOfView"/> is not positive.</exception>
    public void Place(Vector3 eye, uint height, float tanHalfFieldOfView) {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value: tanHalfFieldOfView);

        if (Count == m_points.Length) {
            Array.Resize(array: ref m_points, newSize: (2 * m_points.Length));
            Array.Resize(array: ref m_focal, newSize: (2 * m_focal.Length));
        }
        m_points[Count] = eye;
        m_focal[Count] = ((Math.Max(val1: height, val2: 1u) * 0.5f) / tanHalfFieldOfView);
        Count++;
    }
    /// <summary>Forgets every demand point.</summary>
    public void Clear() => Count = 0;
    /// <summary>Returns the metres one display pixel covers at a box from the demand point that sees it finest: the
    /// distance from the eye to the box, no nearer than <see cref="NearDistance"/>, over the point's pixels a metre; what
    /// a tier's cell is measured against (<see cref="SdfMeshTier.Of"/>).</summary>
    /// <param name="center">The box's center, in world space.</param>
    /// <param name="half">The box's half extents along the world axes, none negative.</param>
    /// <returns>The pixel's size in metres; infinity with no demand point.</returns>
    public float PixelAt(Vector3 center, Vector3 half) {
        var finest = float.PositiveInfinity;

        for (var index = 0; (index < Count); index++) {
            finest = MathF.Min(x: finest, y: (MathF.Max(x: NearDistance, y: DistanceTo(center: center, half: half, point: m_points[index])) / m_focal[index]));
        }

        return finest;
    }
    /// <summary>Returns the metres one display pixel covers at a planned group's box (<see cref="PixelAt(Vector3, Vector3)"/>),
    /// in the group's own units: a dynamic group's box is carried into world space by its slot's transform among
    /// <paramref name="transforms"/>, and its pixel read in the slot's units (<see cref="SdfMeshGeometryBuilder.UnitsOf"/>),
    /// since its lattice is in that frame. What a plan asks each group's tier of (<see cref="SdfMeshTier.Of"/>).</summary>
    /// <param name="group">The group, its box set.</param>
    /// <param name="transforms">The dynamic transforms the plan is made at.</param>
    /// <returns>The pixel's size in the group's units; infinity with no demand point.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="group"/> or <paramref name="transforms"/> is
    /// <see langword="null"/>.</exception>
    public float PixelAt(SdfMeshBakeGroup group, IReadOnlyList<DynamicTransform> transforms) {
        ArgumentNullException.ThrowIfNull(argument: group);
        ArgumentNullException.ThrowIfNull(argument: transforms);

        var center = ((group.Low + group.High) * 0.5f);
        var half = Vector3.Abs(value: ((group.High - group.Low) * 0.5f));

        if (!group.Dynamic || (group.Slot >= transforms.Count)) { return PixelAt(center: center, half: half); }

        var transform = transforms[group.Slot];
        var units = SdfMeshGeometryBuilder.UnitsOf(transform: transform);
        var (position, orientation) = SdfMeshGeometryBuilder.PoseOf(transform: transform);
        var box = SdfMeshBox.Turned(center: (center * units), half: (half * units), offset: position, turn: orientation);

        return (PixelAt(center: box.Center, half: box.AxisHalf) / units);
    }
    /// <summary>Returns a box's priority at the demand point that ranks it highest. A box whose diagonal projects past a
    /// pixel there is demanded, and ranks by the pixels a metre covers at its distance, one plus the point's pixels a
    /// metre over the distance from its eye to the box, no nearer than <see cref="NearDistance"/>: the nearer, and the
    /// finer the view, the sooner, whatever the box's size. A box under a pixel ranks below every demanded one, by the
    /// share of a pixel it covers. Zero with no demand point.</summary>
    /// <param name="center">The box's center, in world space.</param>
    /// <param name="half">The box's half extents along the world axes, none negative.</param>
    /// <returns>The priority: past one for a demanded box, below one for another.</returns>
    public float Priority(Vector3 center, Vector3 half) {
        var diagonal = (2f * half.Length());
        var best = 0f;

        for (var index = 0; (index < Count); index++) {
            var density = (m_focal[index] / MathF.Max(x: NearDistance, y: DistanceTo(center: center, half: half, point: m_points[index])));
            var size = (diagonal * density);

            best = MathF.Max(x: best, y: ((size >= 1f) ? (1f + density) : size));
        }

        return best;
    }
    /// <summary>Returns the distance from the nearest demand point's eye to a box, zero inside it, or infinity with no
    /// demand point.</summary>
    /// <param name="center">The box's center, in world space.</param>
    /// <param name="half">The box's half extents along the world axes, none negative.</param>
    /// <returns>The distance in metres.</returns>
    public float Distance(Vector3 center, Vector3 half) {
        var nearest = float.PositiveInfinity;

        for (var index = 0; (index < Count); index++) {
            nearest = MathF.Min(x: nearest, y: DistanceTo(center: center, half: half, point: m_points[index]));
        }

        return nearest;
    }
    /// <summary>Returns the distance from one demand point's eye to a box, zero inside it.</summary>
    /// <param name="index">The point, below <see cref="Count"/>.</param>
    /// <param name="center">The box's center, in world space.</param>
    /// <param name="half">The box's half extents along the world axes, none negative.</param>
    /// <returns>The distance in metres.</returns>
    public float DistanceFrom(int index, Vector3 center, Vector3 half) => DistanceTo(center: center, half: half, point: m_points[index]);
    /// <summary>Returns a demand point's eye.</summary>
    /// <param name="index">The point, below <see cref="Count"/>.</param>
    /// <returns>The eye's position.</returns>
    public Vector3 Point(int index) => m_points[index];
    /// <summary>Returns the world-axis box about a draw's host box (<see cref="SdfMeshBox.TryOf"/>).</summary>
    /// <param name="draw">The draw.</param>
    /// <param name="center">The box's center; the origin for a draw no host box bounds.</param>
    /// <param name="half">The box's half extents along the world axes; zero for a draw no host box bounds.</param>
    /// <returns><see langword="false"/> for a draw no host box bounds (a skinned body's).</returns>
    public static bool BoxOf(in SdfMeshDraw draw, out Vector3 center, out Vector3 half) {
        var boxed = SdfMeshBox.TryOf(box: out var box, draw: in draw);

        center = box.Center;
        half = box.AxisHalf;
        return boxed;
    }

    // The distance from a point to a box, zero inside it.
    private static float DistanceTo(Vector3 center, Vector3 half, Vector3 point) =>
        Vector3.Max(value1: (Vector3.Abs(value: (point - center)) - half), value2: Vector3.Zero).Length();
}
