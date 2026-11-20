using System.Numerics;
using Puck.Abstractions.Cameras;
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

    /// <summary>The fraction a view's frustum sides are widened by where the view demands only inside it
    /// (<see cref="SdfViewSnapshot.DemandWithinView"/>): half as wide again, so the viewer's next steps find the meshes
    /// beside the aperture's edge at the tier their view asks.</summary>
    public const float WithinViewMargin = 0.5f;

    private Vector3[] m_points = new Vector3[4];
    private float[] m_focal = new float[4];
    // Each point's camera, begun at its near plane widened for a point that demands inside its view (m_within), and
    // whether the point has one: a point placed with no frame (Place) sees every box.
    private CameraSnapshot[] m_cameras = new CameraSnapshot[4];
    private bool[] m_within = new bool[4];
    private bool[] m_framed = new bool[4];

    /// <summary>Gets how many demand points the latest frame named.</summary>
    public int Count { get; private set; }

    /// <summary>Reads the demand points from a frame's views: each view's eye, and its pixels a metre one metre in front of
    /// it, from the height its sizes are measured in (<see cref="SdfViewSnapshot.DemandHeight"/>) or else its share of
    /// the output's height, and its field of view. A view that demands only inside its frustum
    /// (<see cref="SdfViewSnapshot.DemandWithinView"/>) demands nothing of a box wholly outside it, widened by
    /// <see cref="WithinViewMargin"/> and begun at its near plane. The points keep the views' order, so the first is the
    /// primary seat's while that seat is presented here.</summary>
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
            m_cameras = new CameraSnapshot[views.Count];
            m_within = new bool[views.Count];
            m_framed = new bool[views.Count];
        }
        for (var index = 0; (index < views.Count); index++) {
            var view = views[index];
            var camera = view.Camera;
            var tan = camera.TanHalfFieldOfView;

            if (view.Elsewhere || !(view.Region.Height > 0f) || !(tan > 0f) || !float.IsFinite(f: camera.Position.X)) { continue; }
            m_points[count] = camera.Position;
            m_focal[count] = ((((view.DemandHeight > 0f) ? view.DemandHeight : MathF.Max(x: 1f, y: (view.Region.Height * height))) * 0.5f) / tan);
            m_within[count] = view.DemandWithinView;
            m_framed[count] = true;
            m_cameras[count] = (view.DemandWithinView ? (camera with { Near = (camera.Near * SdfMeshCull.NearKeeps) }) : camera);
            count++;
        }
        Count = count;
    }
    /// <summary>Adds a demand point after those the latest frame named: an eye with its
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
            Array.Resize(array: ref m_cameras, newSize: (2 * m_cameras.Length));
            Array.Resize(array: ref m_within, newSize: (2 * m_within.Length));
            Array.Resize(array: ref m_framed, newSize: (2 * m_framed.Length));
        }
        m_points[Count] = eye;
        m_within[Count] = false;
        m_framed[Count] = false;
        m_focal[Count] = ((Math.Max(val1: height, val2: 1u) * 0.5f) / tanHalfFieldOfView);
        Count++;
    }
    /// <summary>Returns the metres one display pixel covers at a box from the demand point that sees it finest: the
    /// distance from the eye to the box, no nearer than <see cref="NearDistance"/>, over the point's pixels a metre; what
    /// a tier's cell is measured against (<see cref="SdfMeshTier.Of"/>). A point that demands only inside its view
    /// counts for no box outside it.</summary>
    /// <param name="center">The box's center, in world space.</param>
    /// <param name="half">The box's half extents along the world axes, none negative.</param>
    /// <returns>The pixel's size in metres; infinity with no demand point.</returns>
    public float PixelAt(Vector3 center, Vector3 half) => PixelAt(center: center, half: half, point: out _);
    /// <summary>Returns the metres one display pixel covers at a box from the demand point that sees it finest
    /// (<see cref="PixelAt(Vector3, Vector3)"/>), and which point that is.</summary>
    /// <param name="center">The box's center, in world space.</param>
    /// <param name="half">The box's half extents along the world axes, none negative.</param>
    /// <param name="point">The demand point that sees the box finest, below <see cref="Count"/>; -1 with none.</param>
    /// <returns>The pixel's size in metres; infinity with no demand point.</returns>
    public float PixelAt(Vector3 center, Vector3 half, out int point) {
        var finest = float.PositiveInfinity;

        point = -1;
        for (var index = 0; (index < Count); index++) {
            var pixel = (MathF.Max(x: NearDistance, y: DistanceTo(center: center, half: half, point: m_points[index])) / m_focal[index]);

            // The distance is the cheap test and the view the costly one: only a point that would be the finest is
            // asked whether its view holds the box.
            if ((pixel < finest) && Demands(index: index, center: center, half: half)) {
                finest = pixel;
                point = index;
            }
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
        var (center, half, units) = WorldBoxOf(group: group, transforms: transforms);

        return (PixelAt(center: center, half: half) / units);
    }
    /// <summary>Returns a box's priority at the demand point that ranks it highest. A box whose diagonal projects past a
    /// pixel there is demanded, and ranks by the pixels a metre covers at its distance, one plus the point's pixels a
    /// metre over the distance from its eye to the box, no nearer than <see cref="NearDistance"/>: the nearer, and the
    /// finer the view, the sooner, whatever the box's size. A box under a pixel ranks below every demanded one, by the
    /// share of a pixel it covers. Zero with no demand point, or with none whose view may hold the box.</summary>
    /// <param name="center">The box's center, in world space.</param>
    /// <param name="half">The box's half extents along the world axes, none negative.</param>
    /// <returns>The priority: past one for a demanded box, below one for another.</returns>
    public float Priority(Vector3 center, Vector3 half) {
        var diagonal = (2f * half.Length());
        var best = 0f;

        for (var index = 0; (index < Count); index++) {
            var density = (m_focal[index] / MathF.Max(x: NearDistance, y: DistanceTo(center: center, half: half, point: m_points[index])));
            var size = (diagonal * density);
            var priority = ((size >= 1f) ? (1f + density) : size);

            // As PixelAt: the view is asked only of a point that would rank the box highest.
            if ((priority > best) && Demands(index: index, center: center, half: half)) { best = priority; }
        }

        return best;
    }
    /// <summary>Returns whether a box is in view: some demand point's view may see it, its frustum widened by
    /// <see cref="WithinViewMargin"/> so the viewer's next steps and turns find it, or a point placed with no frame
    /// (<see cref="Place"/>) stands, or no demand point does, so nothing ranks the box behind another (a boot or an
    /// arrival before any view). What the bake orders its work by and what it owes the frame first.</summary>
    /// <param name="center">The box's center, in world space.</param>
    /// <param name="half">The box's half extents along the world axes, none negative.</param>
    /// <returns><see langword="true"/> when the box is in view.</returns>
    public bool Sees(Vector3 center, Vector3 half) {
        if (Count == 0) { return true; }
        for (var index = 0; (index < Count); index++) {
            if (!m_framed[index] || SeesFrom(index: index, center: center, half: half)) { return true; }
        }
        return false;
    }
    /// <summary>Returns whether a planned group's box is in view (<see cref="Sees(Vector3, Vector3)"/>): a dynamic
    /// group's box carried into world space by its slot's transform among <paramref name="transforms"/>.</summary>
    /// <param name="group">The group, its box set.</param>
    /// <param name="transforms">The dynamic transforms the plan is made at.</param>
    /// <returns><see langword="true"/> when the group's box is in view.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="group"/> or <paramref name="transforms"/> is
    /// <see langword="null"/>.</exception>
    public bool Sees(SdfMeshBakeGroup group, IReadOnlyList<DynamicTransform> transforms) {
        var (center, half, _) = WorldBoxOf(group: group, transforms: transforms);

        return Sees(center: center, half: half);
    }
    // A planned group's box in world space, and the units its lattice is in: a dynamic group's carried out of its slot's
    // frame by the slot's transform among the plan's, in the slot's units (SdfMeshGeometryBuilder.UnitsOf); any other
    // group's as planned, in metres.
    private static (Vector3 Center, Vector3 Half, float Units) WorldBoxOf(SdfMeshBakeGroup group, IReadOnlyList<DynamicTransform> transforms) {
        ArgumentNullException.ThrowIfNull(argument: group);
        ArgumentNullException.ThrowIfNull(argument: transforms);

        var center = ((group.Low + group.High) * 0.5f);
        var half = Vector3.Abs(value: ((group.High - group.Low) * 0.5f));

        if (!group.Dynamic || (group.Slot >= transforms.Count)) { return (center, half, 1f); }

        var transform = transforms[group.Slot];
        var box = SdfMeshBox.Carried(center: center, half: half, transform: transform);

        return (box.Center, box.AxisHalf, SdfMeshGeometryBuilder.UnitsOf(transform: transform));
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
    /// <summary>Returns the pixels a metre covers one metre in front of a demand point.</summary>
    /// <param name="index">The point, below <see cref="Count"/>.</param>
    /// <returns>The pixels a metre.</returns>
    public float PixelsPerMetre(int index) => m_focal[index];
    /// <summary>Returns whether a demand point demands only inside its view (<see cref="SdfViewSnapshot.DemandWithinView"/>).</summary>
    /// <param name="index">The point, below <see cref="Count"/>.</param>
    /// <returns><see langword="true"/> for a point bounded to its view's frustum.</returns>
    public bool WithinView(int index) => m_within[index];
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

    // Whether a point demands a world-axis box at all: always for a point that demands by distance alone, and only a
    // box its widened frustum may hold for one that demands inside its view.
    private bool Demands(int index, Vector3 center, Vector3 half) => (!m_within[index] || SeesFrom(index: index, center: center, half: half));
    // Whether a framed point's widened frustum may hold a world-axis box.
    private bool SeesFrom(int index, Vector3 center, Vector3 half) => m_cameras[index].MaySee(
        center: center,
        halfX: new Vector3(x: half.X, y: 0f, z: 0f),
        halfY: new Vector3(x: 0f, y: half.Y, z: 0f),
        halfZ: new Vector3(x: 0f, y: 0f, z: half.Z),
        margin: WithinViewMargin
    );
    // The distance from a point to a box, zero inside it.
    private static float DistanceTo(Vector3 center, Vector3 half, Vector3 point) =>
        Vector3.Max(value1: (Vector3.Abs(value: (point - center)) - half), value2: Vector3.Zero).Length();
}
