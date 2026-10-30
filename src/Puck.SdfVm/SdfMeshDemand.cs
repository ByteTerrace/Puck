using System.Numerics;

namespace Puck.SdfVm;

/// <summary>
/// The demand points one residency's presentation orders its mesh residency by: the eye of every view its latest frame
/// renders (each seat's camera, the operator's, every layout slot's), each with the display pixels a metre covers one
/// metre in front of it. A thing's priority is the size it projects at the demand point that sees it largest
/// (<see cref="Priority"/>), so the nearest demand point wins and a small thing close by outranks a large one far off; the
/// bake takes lattices (<see cref="SdfMeshPathBake"/>), its store loads and the mesh region its owed meshes
/// (<see cref="SdfWorldTables"/>) in that order.
/// <para>Presentation alone reads it: nothing here reaches simulation state, and a mesh is the same whichever order it was
/// baked or loaded in. A view another residency renders (<see cref="SdfViewSnapshot.Elsewhere"/>) or a hidden one demands
/// nothing here; a frame with no demand point gives everything priority zero, which leaves the plan's order.</para>
/// <para>Read and written on the frame's thread alone.</para>
/// </summary>
public sealed class SdfMeshDemand {
    /// <summary>The nearest a demand point is counted to a thing, in metres: one it stands inside, or nearer than this,
    /// projects as though this far off, so every such thing ranks above anything farther and the size of what holds the eye
    /// still orders them.</summary>
    public const float NearDistance = 0.25f;

    private Vector3[] m_points = new Vector3[4];
    private float[] m_focal = new float[4];

    /// <summary>Gets how many demand points the latest frame named.</summary>
    public int Count { get; private set; }

    /// <summary>Reads the demand points from a frame's views: each view's eye, and its display pixels a metre one metre in
    /// front of it, from the view's share of the output's height and its field of view.</summary>
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
            m_focal[count] = ((MathF.Max(x: 1f, y: (view.Region.Height * height)) * 0.5f) / tan);
            count++;
        }
        Count = count;
    }
    /// <summary>Returns the display pixels a sphere projects across at the demand point that sees it largest: its diameter
    /// over its distance from that point's eye to its surface, no nearer than <see cref="NearDistance"/>; zero with no demand
    /// point.</summary>
    /// <param name="center">The sphere's center, in world space.</param>
    /// <param name="radius">The sphere's radius.</param>
    /// <returns>The projected size in display pixels.</returns>
    public float Priority(Vector3 center, float radius) {
        var best = 0f;

        for (var index = 0; (index < Count); index++) {
            var distance = MathF.Max(x: NearDistance, y: (Vector3.Distance(value1: center, value2: m_points[index]) - radius));

            best = MathF.Max(x: best, y: (((2f * radius) * m_focal[index]) / distance));
        }

        return best;
    }
    /// <summary>Returns the distance from the nearest demand point's eye to a sphere's surface, zero inside it, or infinity
    /// with no demand point.</summary>
    /// <param name="center">The sphere's center, in world space.</param>
    /// <param name="radius">The sphere's radius.</param>
    /// <returns>The distance in metres.</returns>
    public float Distance(Vector3 center, float radius) {
        var nearest = float.PositiveInfinity;

        for (var index = 0; (index < Count); index++) {
            nearest = MathF.Min(x: nearest, y: MathF.Max(x: 0f, y: (Vector3.Distance(value1: center, value2: m_points[index]) - radius)));
        }

        return nearest;
    }
    /// <summary>Returns a demand point's eye.</summary>
    /// <param name="index">The point, below <see cref="Count"/>.</param>
    /// <returns>The eye's position.</returns>
    public Vector3 Point(int index) => m_points[index];
    /// <summary>Returns a draw's bounding sphere in world space: its host box's (<see cref="SdfMeshBox.TryOf"/>), or
    /// <see langword="false"/> for a draw no host box bounds.</summary>
    /// <param name="draw">The draw.</param>
    /// <param name="center">The sphere's center.</param>
    /// <param name="radius">The sphere's radius.</param>
    /// <returns>Whether the draw has a box.</returns>
    public static bool SphereOf(in SdfMeshDraw draw, out Vector3 center, out float radius) {
        if (!SdfMeshBox.TryOf(box: out var box, draw: in draw)) {
            center = default;
            radius = 0f;
            return false;
        }

        center = box.Center;
        radius = MathF.Sqrt(x: ((box.HalfX.LengthSquared() + box.HalfY.LengthSquared()) + box.HalfZ.LengthSquared()));
        return true;
    }
}
