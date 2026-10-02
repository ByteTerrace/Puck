using System.Numerics;
using Puck.Abstractions.Counting;

namespace Puck.SdfVm;

/// <summary>
/// How a baked placement's two representations take turns in a view: its mesh while the placement is large on screen,
/// its impostor (<see cref="SdfMeshImpostor"/>) once it is small. Both are draws of the placement
/// (<see cref="SdfMeshDraw.Lod"/>) bounded by the same sphere, and a view records exactly one of them: the one whose
/// <see cref="Far"/> matches what <see cref="SelectFar"/> says of the sphere's projected diameter in that view's render
/// pixels (<see cref="ProjectedPixels"/>).
/// <para>The switch is <see cref="SwitchPixels"/>, the impostor's view edge in texels
/// (<see cref="ForImpostor"/>): below it one impostor texel covers at most one pixel, so the impostor loses nothing the
/// view could show of the sphere's whole silhouette. A placement already drawn as its impostor stays one until its
/// diameter passes <see cref="SwitchPixels"/> by <see cref="Hysteresis"/>, so a camera hovering at the switch does not
/// alternate representations frame to frame. A camera within one radius of the sphere along its forward axis is
/// never far, and a placement that is not a bake carries no LOD and is always drawn.</para>
/// </summary>
/// <param name="Center">The bounding sphere's center, in the draw's object space.</param>
/// <param name="Radius">The bounding sphere's radius, in the draw's object space.</param>
/// <param name="SwitchPixels">The projected diameter, in render pixels, below which the placement draws as its
/// impostor.</param>
/// <param name="Far">Whether this draw is the impostor (<see langword="true"/>) or the mesh.</param>
public readonly record struct SdfMeshLod(Vector3 Center, float Radius, float SwitchPixels, bool Far) {
    /// <summary>The fraction of <see cref="SwitchPixels"/> a diameter must exceed it by for an impostor to hand back to its
    /// mesh.</summary>
    public const float Hysteresis = 0.25f;

    /// <summary>Returns the LOD of one representation of a baked placement.</summary>
    /// <param name="impostor">The placement's impostor, whose sphere bounds both representations and whose view edge is the
    /// switch.</param>
    /// <param name="far"><see langword="true"/> for the impostor's draw, <see langword="false"/> for the mesh's.</param>
    /// <returns>The LOD.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="impostor"/> is <see langword="null"/>.</exception>
    public static SdfMeshLod ForImpostor(SdfMeshImpostor impostor, bool far) {
        ArgumentNullException.ThrowIfNull(argument: impostor);

        return new SdfMeshLod(
            Center: impostor.Center,
            Far: far,
            Radius: impostor.Radius,
            SwitchPixels: impostor.ViewTexels
        );
    }
    /// <summary>Returns how many render pixels span one world unit at one unit of forward depth: the extent's height over
    /// twice the tangent of the half field of view.</summary>
    /// <param name="renderHeight">The view's render height, in pixels.</param>
    /// <param name="tanHalfFieldOfView">The tangent of half the vertical field of view
    /// (<c>CameraSnapshot.TanHalfFieldOfView</c>).</param>
    /// <returns>The pixels per unit at unit depth.</returns>
    public static float PixelsPerUnitDepth(float renderHeight, float tanHalfFieldOfView) =>
        (renderHeight / (2f * tanHalfFieldOfView));
    /// <summary>Returns the diameter of the bounding sphere's projection, in render pixels: twice its world radius over the
    /// forward depth of its center. Infinite where the camera is within one radius of the sphere along its forward axis, or
    /// the sphere lies behind it.</summary>
    /// <param name="objectToWorld">The draw's row-vector object-to-world matrix; its first row's length is the uniform
    /// scale.</param>
    /// <param name="cameraPosition">The camera's position.</param>
    /// <param name="cameraForward">The camera's unit forward axis.</param>
    /// <param name="pixelsPerUnitDepth">The view's <see cref="PixelsPerUnitDepth"/>.</param>
    /// <returns>The diameter, in pixels.</returns>
    public float ProjectedPixels(Matrix4x4 objectToWorld, Vector3 cameraPosition, Vector3 cameraForward, float pixelsPerUnitDepth) {
        var center = Vector3.Transform(
            matrix: objectToWorld,
            position: Center
        );
        var radius = (Radius * new Vector3(x: objectToWorld.M11, y: objectToWorld.M12, z: objectToWorld.M13).Length());
        var depth = Vector3.Dot(
            vector1: (center - cameraPosition),
            vector2: cameraForward
        );

        return ((depth > radius)
            ? (((2f * radius) * pixelsPerUnitDepth) / depth)
            : float.PositiveInfinity);
    }
    /// <summary>Returns whether a placement draws as its impostor at a projected diameter: below
    /// <see cref="SwitchPixels"/>, or, for a placement that already did, below it by <see cref="Hysteresis"/> more.</summary>
    /// <param name="wasFar">Whether the placement drew as its impostor in the view's last frame.</param>
    /// <param name="pixels">The projected diameter (<see cref="ProjectedPixels"/>).</param>
    /// <returns><see langword="true"/> when the impostor draws.</returns>
    public bool SelectFar(bool wasFar, float pixels) =>
        (pixels < (wasFar ? (SwitchPixels * (1f + Hysteresis)) : SwitchPixels));
}
/// <summary>
/// Chooses, for one view, which draws of a frame it records: every draw without a <see cref="SdfMeshLod"/>, and of each
/// baked placement's pair the one its projected diameter selects. It keeps each draw's last choice so the hysteresis of
/// <see cref="SdfMeshLod.SelectFar"/> holds across the view's frames, including revisions and reordering of the draw list.
/// A draw keeps its choice while its identity and LOD agree; absent identities are forgotten.
/// </summary>
/// <param name="work">The counters the choices count into, or <see langword="null"/> for the process's
/// (<see cref="ProcessWork"/>).</param>
public sealed class SdfMeshLodSelector(WorkCounterSet? work = null) {
    /// <summary>The name of the counter source.</summary>
    public const string SourceName = "sdf.mesh.lod";

    /// <summary>Gets the kind counting the mesh draws of baked placements a view recorded, one for each draw each frame.</summary>
    public static WorkKind Near { get; } = new(name: "sdf.mesh.lod.near", unit: "count", workClass: WorkClass.Pacing);
    /// <summary>Gets the kind counting the impostor draws of baked placements a view recorded, one for each draw each
    /// frame.</summary>
    public static WorkKind Far { get; } = new(name: "sdf.mesh.lod.far", unit: "count", workClass: WorkClass.Pacing);
    /// <summary>Gets the process's counters, which a host registers as a work source.</summary>
    public static WorkCounterSet ProcessWork { get; } = new(
        kinds: [Near, Far],
        name: SourceName
    );

    private readonly WorkCounterSet m_work = (work ?? ProcessWork);
    private Dictionary<object, (SdfMeshLod Lod, bool Far)> m_choices = [];
    private Dictionary<object, (SdfMeshLod Lod, bool Far)> m_next = [];

    /// <summary>Chooses the draws one view records this frame.</summary>
    /// <param name="draws">The frame's draws.</param>
    /// <param name="impostorsAvailable">Whether the frame packed its impostor atlases successfully. Without them every
    /// pair records its mesh, since an unpacked card has no surface.</param>
    /// <param name="cameraPosition">The view's camera position.</param>
    /// <param name="cameraForward">The view's camera forward axis.</param>
    /// <param name="pixelsPerUnitDepth">The view's <see cref="SdfMeshLod.PixelsPerUnitDepth"/>.</param>
    /// <param name="recorded">Receives, for each draw, whether the view records it; at least as long as
    /// <paramref name="draws"/>.</param>
    /// <exception cref="ArgumentNullException"><paramref name="draws"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="recorded"/> is shorter than <paramref name="draws"/>.</exception>
    public void Select(IReadOnlyList<SdfMeshDraw> draws, bool impostorsAvailable, Vector3 cameraPosition, Vector3 cameraForward, float pixelsPerUnitDepth, Span<bool> recorded) {
        ArgumentNullException.ThrowIfNull(argument: draws);

        if (recorded.Length < draws.Count) {
            throw new ArgumentException(
                message: $"The view records {draws.Count} draws; the destination holds {recorded.Length}.",
                paramName: nameof(recorded)
            );
        }

        m_next.Clear();
        var near = 0L;
        var far = 0L;

        for (var index = 0; (index < draws.Count); index++) {
            var draw = draws[index];

            if (draw.Lod is not { } lod) {
                recorded[index] = true;
                continue;
            }

            var wasFar = (m_choices.TryGetValue(key: draw.Identity, value: out var previous) &&
                (previous.Lod == lod) && previous.Far);
            var selected = (impostorsAvailable && lod.SelectFar(
                pixels: lod.ProjectedPixels(
                    cameraForward: cameraForward,
                    cameraPosition: cameraPosition,
                    objectToWorld: draw.ObjectToWorld,
                    pixelsPerUnitDepth: pixelsPerUnitDepth
                ),
                wasFar: wasFar
            ));

            m_next[draw.Identity] = (lod, selected);
            recorded[index] = (selected == lod.Far);

            if (recorded[index]) {
                if (lod.Far) {
                    far++;
                } else {
                    near++;
                }
            }
        }

        (m_choices, m_next) = (m_next, m_choices);
        m_work.Add(amount: near, kind: Near);
        m_work.Add(amount: far, kind: Far);
    }
}
