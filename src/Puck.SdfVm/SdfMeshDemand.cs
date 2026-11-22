using System.Numerics;
using Puck.Abstractions.Cameras;
using Puck.Abstractions.Memory;
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
    // Each point's view, by the object whose view it shows and its cut (SdfViewSnapshot.Source, CutRevision), which an
    // observation matches the points it held by; a view with no source is matched by its place among those with none.
    private object?[] m_sources = new object?[4];
    private long[] m_cuts = new long[4];
    // The points the observation before held, which the next one matches its views against.
    private Viewpoint[] m_held = new Viewpoint[4];
    private object?[] m_heldSources = new object?[4];
    private long[] m_heldCuts = new long[4];
    private int m_heldCount;
    // The changes no odometer bounds since the bake last took them (TakeChanges), and whether they ran past what the list
    // holds, which leaves every answer open.
    private readonly List<Change> m_changes = [];
    private bool m_overflowed = true;

    /// <summary>The farthest, in metres, a point's eye moves between two observations before the move is read as a jump
    /// (<see cref="Change"/>) rather than travel: well past a step a walking or flying view takes in a frame, and well
    /// short of a teleport.</summary>
    public const float JumpMetres = 2f;
    /// <summary>The most changes the list keeps between two takes (<see cref="TakeChanges"/>) before it says only that
    /// it overflowed.</summary>
    public const int MostChanges = 256;

    /// <summary>One demand point as an observation saw it: its eye, its pixels a metre one metre in front of it, whether it
    /// demands only inside its view, whether it has a camera at all, and the camera, begun at its widened near plane for
    /// a point bounded to its view.</summary>
    /// <param name="Eye">The eye, in world space.</param>
    /// <param name="Focal">The pixels a metre one metre in front of the eye.</param>
    /// <param name="Within">Whether the point demands only inside its view's widened frustum.</param>
    /// <param name="Framed">Whether the point has a camera; a point placed with no frame sees every box.</param>
    /// <param name="Camera">The point's camera, when framed.</param>
    public readonly record struct Viewpoint(Vector3 Eye, float Focal, bool Within, bool Framed, CameraSnapshot Camera) {
        /// <summary>Returns the metres one display pixel covers at a box from this point's distance alone, whatever its view
        /// holds (<see cref="PixelAt(Vector3, Vector3)"/> without the view's test): no turn of a point bounded to its view
        /// can bring the box finer than this.</summary>
        /// <param name="center">The box's center, in world space.</param>
        /// <param name="half">The box's half extents along the world axes, none negative.</param>
        /// <returns>The pixel's size in metres.</returns>
        public float ReachAt(Vector3 center, Vector3 half) => (MathF.Max(x: NearDistance, y: DistanceTo(center: center, half: half, point: Eye)) / Focal);
    }
    /// <summary>A change of the demand points that no odometer bounds: a point that came (<paramref name="Before"/> none),
    /// went (<paramref name="After"/> none), or changed whether it is bounded to its view or has a frame. A pixel any box
    /// reads moves only through the points the change names, so a box neither side's pixel reaches below its own keeps
    /// its answer.</summary>
    /// <param name="Before">The point as it stood before, when it stood.</param>
    /// <param name="After">The point as it stands after, when it stands.</param>
    /// <param name="HadBefore">Whether <paramref name="Before"/> names a point.</param>
    /// <param name="HasAfter">Whether <paramref name="After"/> names a point.</param>
    public readonly record struct Change(Viewpoint Before, Viewpoint After, bool HadBefore, bool HasAfter);

    /// <summary>Gets how many demand points the latest frame named.</summary>
    public int Count { get; private set; }

    /// <summary>Gets the demand points' travel, in metres a display pixel covers: each observation adds the most any point
    /// it matched moved over that point's pixels a metre. A box's <see cref="PixelAt(Vector3, Vector3)"/> answer moves
    /// by no more than the travel between two observations, divided by the units the box is measured in, together with
    /// <see cref="Scale"/> and <see cref="Changes"/>: a box's distance from a point moves no more than the point does,
    /// and the near clamp keeps that bound.</summary>
    public double Travel { get; private set; }
    /// <summary>Gets the demand points' change of scale: each observation adds the largest magnitude of the natural
    /// logarithm of a matched point's pixels a metre over what they were. A point's pixel at a fixed distance is
    /// multiplied by at most <c>e</c> to the scale between two observations, either way.</summary>
    public double Scale { get; private set; }
    /// <summary>Gets the travel of the points bounded to their views, in metres: each observation adds the most any such
    /// matched point's eye and near plane moved. A box's distance to one of those frustums' planes moves by no more than
    /// this, with <see cref="Turn"/>.</summary>
    public double Shift { get; private set; }
    /// <summary>Gets the turn of the points bounded to their views, in radians: each observation adds the largest angle any
    /// such matched point's frustum planes turned through (<see cref="CameraSnapshot.FrustumTurn"/>), whether its camera
    /// turned or its field of view or aspect moved. A box within a distance <c>r</c> of the eye has its distance to a plane
    /// moved by no more than <c>r</c> times the turn.</summary>
    public double Turn { get; private set; }
    /// <summary>Gets the changes no odometer bounds since the bake last took them (<see cref="TakeChanges"/>).</summary>
    public IReadOnlyList<Change> Changes => m_changes;

    /// <summary>Hands over the changes since the last hand-over and starts the next list, for the one bake that measures by
    /// this demand.</summary>
    /// <param name="overflowed">Whether more changes came than the list keeps (<see cref="MostChanges"/>), or none was
    /// observed yet, so every answer may have moved.</param>
    /// <returns>The changes, valid until the next observation.</returns>
    public IReadOnlyList<Change> TakeChanges(out bool overflowed) {
        overflowed = m_overflowed;
        m_overflowed = false;
        m_taken.Clear();
        m_taken.AddRange(collection: m_changes);
        m_changes.Clear();
        return m_taken;
    }

    private readonly List<Change> m_taken = [];

    /// <summary>Reads the demand points from a frame's views: each view's eye, and its pixels a metre one metre in front of
    /// it, from the height its sizes are measured in (<see cref="SdfViewSnapshot.DemandHeight"/>) or else its share of
    /// the output's height, and its field of view. A view that demands only inside its frustum
    /// (<see cref="SdfViewSnapshot.DemandWithinView"/>) demands nothing of a box wholly outside it, widened by
    /// <see cref="WithinViewMargin"/> and begun at its near plane. The points keep the views' order, so the first is the
    /// primary seat's while that seat is presented here. Each view is matched to the point it held before by its source
    /// and cut, or by its place among the views with none: a matched point's moves add to the odometers
    /// (<see cref="Travel"/>, <see cref="Scale"/>, <see cref="Shift"/>, <see cref="Turn"/>), and any other change is
    /// listed (<see cref="Changes"/>).</summary>
    /// <param name="frame">The frame.</param>
    /// <param name="height">The output's height in display pixels.</param>
    /// <exception cref="ArgumentNullException"><paramref name="frame"/> is <see langword="null"/>.</exception>
    public void Observe(SdfFrame frame, uint height) {
        ArgumentNullException.ThrowIfNull(argument: frame);

        var views = frame.Views;
        var count = 0;
        var travel = 0f;
        var scale = 0f;
        var shift = 0f;
        var turn = 0f;
        var unsourced = 0;

        Hold();
        EnsureCapacity(views.Count);
        Span<bool> matched = stackalloc bool[m_heldCount];

        matched.Clear();
        for (var index = 0; (index < views.Count); index++) {
            var view = views[index];
            var camera = view.Camera;
            var tan = camera.TanHalfFieldOfView;

            if (view.Elsewhere || !(view.Region.Height > 0f) || !(tan > 0f) || !float.IsFinite(f: camera.Position.X)) { continue; }

            var focal = ((((view.DemandHeight > 0f) ? view.DemandHeight : MathF.Max(x: 1f, y: (view.Region.Height * height))) * 0.5f) / tan);
            var within = view.DemandWithinView;
            var held = (within ? (camera with { Near = (camera.Near * SdfMeshCull.NearKeeps) }) : camera);
            var point = new Viewpoint(Camera: held, Eye: camera.Position, Focal: focal, Framed: true, Within: within);
            var before = Match(source: view.Source, cut: view.CutRevision, unsourced: ((view.Source is null) ? unsourced++ : -1), matched: matched);

            // A point that jumped (a teleport, a cut) is read as one that went and one that came: the boxes either side
            // reaches are measured again, rather than every box a jump's travel would make due.
            if ((before < 0) || (m_held[before].Within != within) || !m_held[before].Framed || (Vector3.Distance(value1: m_held[before].Eye, value2: point.Eye) > JumpMetres)) {
                if (before >= 0) { Changed(before: m_held[before], hadBefore: true, after: point, hasAfter: true); } else { Changed(before: default, hadBefore: false, after: point, hasAfter: true); }
            } else {
                var was = m_held[before];

                // A distance moves no more than the eye does; a pixel at a fixed distance by the focal's ratio.
                travel = MathF.Max(x: travel, y: (Vector3.Distance(value1: was.Eye, value2: point.Eye) / focal));
                scale = MathF.Max(x: scale, y: MathF.Abs(x: MathF.Log(x: (focal / was.Focal))));
                if (within) {
                    shift = MathF.Max(x: shift, y: (Vector3.Distance(value1: was.Eye, value2: point.Eye) + MathF.Abs(x: (held.Near - was.Camera.Near))));
                    turn = MathF.Max(x: turn, y: held.FrustumTurn(other: was.Camera, margin: WithinViewMargin));
                }
            }
            m_points[count] = point.Eye;
            m_focal[count] = focal;
            m_within[count] = within;
            m_framed[count] = true;
            m_cameras[count] = held;
            m_sources[count] = view.Source;
            m_cuts[count] = view.CutRevision;
            count++;
        }
        for (var before = 0; (before < m_heldCount); before++) {
            if (!matched[before]) { Changed(before: m_held[before], hadBefore: true, after: default, hasAfter: false); }
        }
        Travel += travel;
        Scale += scale;
        Shift += shift;
        Turn += turn;
        Count = count;
    }
    // Keeps the points the latest observation held, for the next one to match its views against.
    private void Hold() {
        if (m_held.Length < Count) {
            Array.Resize(array: ref m_held, newSize: m_points.Length);
            Array.Resize(array: ref m_heldSources, newSize: m_points.Length);
            Array.Resize(array: ref m_heldCuts, newSize: m_points.Length);
        }
        for (var index = 0; (index < Count); index++) {
            m_held[index] = PointAt(index: index);
            m_heldSources[index] = m_sources[index];
            m_heldCuts[index] = m_cuts[index];
        }
        m_heldCount = Count;
    }
    // The held point a view continues: the one with its source and cut, or for a view with none the held point at the
    // same place among those with none; -1 for none.
    private int Match(object? source, long cut, int unsourced, Span<bool> matched) {
        var place = 0;

        for (var before = 0; (before < m_heldCount); before++) {
            var heldSource = m_heldSources[before];
            var match = ((source is null) ? ((heldSource is null) && (place++ == unsourced)) : (ReferenceEquals(objA: heldSource, objB: source) && (m_heldCuts[before] == cut)));

            if (!match || matched[before]) { continue; }
            matched[before] = true;
            return before;
        }

        return -1;
    }
    // Lists a change no odometer bounds, or notes that the list overflowed.
    private void Changed(Viewpoint before, bool hadBefore, Viewpoint after, bool hasAfter) {
        if (m_changes.Count >= MostChanges) {
            m_overflowed = true;
            return;
        }
        m_changes.Add(item: new Change(After: after, Before: before, HadBefore: hadBefore, HasAfter: hasAfter));
    }
    /// <summary>Returns a demand point as the latest observation holds it.</summary>
    /// <param name="index">The point, below <see cref="Count"/>.</param>
    /// <returns>The point.</returns>
    public Viewpoint PointAt(int index) => new(Camera: m_cameras[index], Eye: m_points[index], Focal: m_focal[index], Framed: m_framed[index], Within: m_within[index]);
    private void EnsureCapacity(int required) {
        if (required <= m_points.Length) { return; }
        var capacity = ArrayCapacity.Grown(m_points.Length, required);
        Array.Resize(ref m_points, capacity);
        Array.Resize(ref m_focal, capacity);
        Array.Resize(ref m_cameras, capacity);
        Array.Resize(ref m_within, capacity);
        Array.Resize(ref m_framed, capacity);
        Array.Resize(ref m_sources, capacity);
        Array.Resize(ref m_cuts, capacity);
    }
    /// <summary>Adds a demand point after those the latest frame named: an eye with its
    /// pixels a metre one metre in front of it, for a plan made with no frame (an offline audit at a chosen eye).</summary>
    /// <param name="eye">The eye's position, in world space.</param>
    /// <param name="height">The view's height in display pixels; at least one.</param>
    /// <param name="tanHalfFieldOfView">The tangent of half the view's vertical field of view; positive.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="tanHalfFieldOfView"/> is not positive.</exception>
    public void Place(Vector3 eye, uint height, float tanHalfFieldOfView) {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value: tanHalfFieldOfView);

        EnsureCapacity(Count + 1);
        m_points[Count] = eye;
        m_within[Count] = false;
        m_framed[Count] = false;
        m_focal[Count] = ((Math.Max(val1: height, val2: 1u) * 0.5f) / tanHalfFieldOfView);
        m_sources[Count] = null;
        m_cuts[Count] = 0L;
        Changed(before: default, hadBefore: false, after: PointAt(index: Count), hasAfter: true);
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
    /// <summary>Returns the world-axis box about a draw's host box where a frame places it
    /// (<see cref="SdfMeshBox.TryOf"/>).</summary>
    /// <param name="draw">The draw.</param>
    /// <param name="transforms">The frame's dynamic transforms.</param>
    /// <param name="center">The box's center; the origin for a draw no host box bounds.</param>
    /// <param name="half">The box's half extents along the world axes; zero for a draw no host box bounds.</param>
    /// <returns><see langword="false"/> for a draw no host box bounds (a skinned body's).</returns>
    public static bool BoxOf(in SdfMeshDraw draw, IReadOnlyList<DynamicTransform> transforms, out Vector3 center, out Vector3 half) {
        var boxed = SdfMeshBox.TryOf(box: out var box, draw: in draw, transforms: transforms);

        center = box.Center;
        half = box.AxisHalf;
        return boxed;
    }

    /// <summary>How a box stands toward the demand points that could move its tier past a pixel, its band's top: the
    /// points whose distance alone puts the box's pixel at or under the top count (the rest read it no finer however they
    /// turn, and cannot become its finest while it moves less than <paramref name="Clearance"/>).</summary>
    /// <param name="Focal">The fewest pixels a metre among the counted points: a box that moves a distance moves its
    /// pixel by at most that distance over this; infinity with none counted.</param>
    /// <param name="Clearance">The least distance, in metres, the box may move before a point not counted could put its
    /// pixel under the top; infinity with none uncounted.</param>
    /// <param name="FrustumSlack">The least distance, in metres, the box may move before a counted point bounded to its
    /// view answers otherwise whether its widened frustum may hold it (<see cref="CameraSnapshot.FrustumSlack"/>);
    /// infinity with none.</param>
    /// <param name="FrustumReach">The most the box reaches from a counted point bounded to its view, in metres: the
    /// distance to its center plus its half extents' sum, which that frustum's <see cref="Turn"/> multiplies into the most
    /// the box's distance to a plane moves; zero with none.</param>
    /// <param name="Beyond">The least pixel an uncounted point's distance puts the box at; infinity with none.</param>
    public readonly record struct Bounds(float Focal, float Clearance, float FrustumSlack, float FrustumReach, float Beyond) {
        /// <summary>Gets the bounds of no box: nothing counted and nothing uncounted.</summary>
        public static Bounds None { get; } = new(Beyond: float.PositiveInfinity, Clearance: float.PositiveInfinity, Focal: float.PositiveInfinity, FrustumReach: 0f, FrustumSlack: float.PositiveInfinity);

        /// <summary>Returns the bounds of two boxes read as one: the tighter of each.</summary>
        /// <param name="other">The other box's bounds.</param>
        /// <returns>The joined bounds.</returns>
        public Bounds Join(Bounds other) => new(
            Beyond: MathF.Min(x: Beyond, y: other.Beyond),
            Clearance: MathF.Min(x: Clearance, y: other.Clearance),
            Focal: MathF.Min(x: Focal, y: other.Focal),
            FrustumReach: MathF.Max(x: FrustumReach, y: other.FrustumReach),
            FrustumSlack: MathF.Min(x: FrustumSlack, y: other.FrustumSlack)
        );
    }
    /// <summary>Returns how a world-axis box stands toward the demand points that could move its tier past a pixel
    /// (<see cref="Bounds"/>).</summary>
    /// <param name="center">The box's center, in world space.</param>
    /// <param name="half">The box's half extents along the world axes, none negative.</param>
    /// <param name="units">The units the box's pixels are read in; positive.</param>
    /// <param name="top">The pixel, in those units, a point must reach to count.</param>
    /// <returns>The bounds.</returns>
    public Bounds BoundsOf(Vector3 center, Vector3 half, float units, float top) {
        var bounds = Bounds.None;
        var (focal, clearance, slack, reach, beyond) = (bounds.Focal, bounds.Clearance, bounds.FrustumSlack, bounds.FrustumReach, bounds.Beyond);

        for (var index = 0; (index < Count); index++) {
            var pixel = (MathF.Max(x: NearDistance, y: DistanceTo(center: center, half: half, point: m_points[index])) / (m_focal[index] * units));

            if (pixel > top) {
                beyond = MathF.Min(x: beyond, y: pixel);
                clearance = MathF.Min(x: clearance, y: ((pixel - top) * m_focal[index] * units));
                continue;
            }
            focal = MathF.Min(x: focal, y: m_focal[index]);
            if (!m_within[index]) { continue; }
            slack = MathF.Min(x: slack, y: m_cameras[index].FrustumSlack(
                center: center,
                halfX: new Vector3(x: half.X, y: 0f, z: 0f),
                halfY: new Vector3(x: 0f, y: half.Y, z: 0f),
                halfZ: new Vector3(x: 0f, y: 0f, z: half.Z),
                margin: WithinViewMargin
            ));
            reach = MathF.Max(x: reach, y: (Vector3.Distance(value1: center, value2: m_points[index]) + ((half.X + half.Y) + half.Z)));
        }

        return new Bounds(Beyond: beyond, Clearance: clearance, Focal: focal, FrustumReach: reach, FrustumSlack: slack);
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
