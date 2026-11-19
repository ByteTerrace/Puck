using System.Numerics;
using Puck.Abstractions.Cameras;
using Puck.Shaders;
using Puck.SignedDistance;

namespace Puck.SdfVm;

/// <summary>One shadow cascade's orthographic light camera: the plane it projects onto, its square half extent and the
/// depth interval along its forward axis (the direction the light travels) that its target holds.</summary>
/// <param name="Position">The center of the camera's plane, in world space.</param>
/// <param name="Right">The plane's unit right axis.</param>
/// <param name="Up">The plane's unit up axis.</param>
/// <param name="Forward">The unit direction the light travels.</param>
/// <param name="HalfHeight">The plane's half extent along each axis, in world units.</param>
/// <param name="Near">The nearest depth along <paramref name="Forward"/> the target holds, in world units.</param>
/// <param name="Far">The farthest depth along <paramref name="Forward"/> the target holds, in world units.</param>
public readonly record struct SdfMeshShadowCamera(Vector3 Position, Vector3 Right, Vector3 Up, Vector3 Forward, float HalfHeight, float Near, float Far);
/// <summary>One entry of a view's cascade set: the light camera it is drawn from, the shadow role its light holds
/// (<see cref="SdfLights.RoleSlot"/>) and that light's penumbra half-slope.</summary>
/// <param name="Camera">The entry's light camera.</param>
/// <param name="Role">The shadow role whose light the entry shadows, <see cref="SdfShadowSlots.Primary"/> or
/// <see cref="SdfShadowSlots.Secondary"/>.</param>
/// <param name="Slope">The light's penumbra half-slope, the tangent of its angular radius: zero for a point source.</param>
public readonly record struct SdfMeshShadowEntry(SdfMeshShadowCamera Camera, int Role, float Slope);
/// <summary>
/// The baked-mesh path's shadow cascades for one view, one indexed set of entries (<see cref="Entry"/>), each a light
/// camera for a shadow role. The set holds, in this order: the primary role's near and far entries, each enclosing a slice
/// of the view's frustum that starts at its eye, the near one the first <see cref="SdfShadowCoverage.Near"/> metres and
/// the far one up to <see cref="SdfShadowCoverage.Far"/> metres or the frame's far distance (the frame's
/// <see cref="SdfFrame.ShadowCoverage"/>); then the secondary role's one entry over the first <see cref="SecondSplit"/>
/// metres. A receiver takes the first entry of its light's role that holds it, and where none does, the probes' answer.
/// Each entry is a bounding sphere of its slice, so its extent does not change as the camera turns, and its plane's center
/// snaps to a whole texel of the view's cascade resolution (<see cref="SdfViewQuality.ShadowResolution"/>), so a still
/// light's shadow does not crawl as the camera moves. The fit is a pure function of the camera, the lights and the far
/// distance, so every pass of a frame that fits it agrees.
/// </summary>
public readonly struct SdfMeshShadowCascades {
    /// <summary>The constant depth bias a receiver subtracts, in metres.</summary>
    public const float ConstantBias = 0.02f;
    /// <summary>The receiver's offset along its triangle's normal, in cascade texels.</summary>
    public const float NormalOffsetTexels = 1.5f;
    /// <summary>The most entries a set holds; each has its own cascade targets and pass.</summary>
    public const int MaxEntries = SdfWorldPackage.MeshShadowEntries;
    /// <summary>The entry the primary role's near slice holds.</summary>
    public const int NearEntry = 0;
    /// <summary>The entry the primary role's far slice holds, whose two layers may be drawn from different refreshes
    /// (<see cref="FarExitCamera"/>).</summary>
    public const int FarEntry = 1;
    /// <summary>The entry the secondary role holds.</summary>
    public const int SecondEntry = 2;
    /// <summary>The camera index the far entry's exit layer reads through, after the entries
    /// (<see cref="this[int]"/>).</summary>
    public const int FarExitCamera = MaxEntries;

    /// <summary>The depth, in metres from the eye, the secondary role's one entry covers.</summary>
    public const float SecondSplit = 40f;

    private readonly SdfMeshShadowEntry m_near;
    private readonly SdfMeshShadowEntry m_far;
    private readonly SdfMeshShadowEntry m_second;
    private readonly SdfMeshShadowCamera m_farExit;

    private SdfMeshShadowCascades(SdfMeshShadowEntry near, SdfMeshShadowEntry far, SdfMeshShadowEntry second, SdfMeshShadowCamera farExit, int count, int texels) {
        m_near = near;
        m_far = far;
        m_second = second;
        m_farExit = farExit;
        Count = count;
        Texels = texels;
    }

    /// <summary>Gets the entries the frame renders, a prefix of the set: zero when no slotted light emits or the view
    /// skips shadows, two for the primary role alone, three with the secondary role.</summary>
    public int Count { get; }
    /// <summary>Gets the texels along each side of every entry's depth, zero when the set is empty.</summary>
    public int Texels { get; }

    /// <summary>Gets one entry's camera, or past the entries the camera the far entry's exit layer was drawn from.</summary>
    /// <param name="index">The entry (<see cref="NearEntry"/>, <see cref="FarEntry"/>, <see cref="SecondEntry"/>), or
    /// <see cref="FarExitCamera"/> (<see cref="WithFar"/>).</param>
    /// <returns>The camera.</returns>
    public SdfMeshShadowCamera this[int index] => ((index == FarExitCamera) ? m_farExit : Entry(index: index).Camera);

    /// <summary>Returns one entry of the set.</summary>
    /// <param name="index">The entry, from zero below <see cref="MaxEntries"/>; one at or past <see cref="Count"/> is
    /// empty.</param>
    /// <returns>The entry.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="index"/> is outside the set.</exception>
    public SdfMeshShadowEntry Entry(int index) => index switch {
        NearEntry => m_near,
        FarEntry => m_far,
        SecondEntry => m_second,
        _ => throw new ArgumentOutOfRangeException(paramName: nameof(index)),
    };
    /// <summary>Returns this set with the far entry's cameras replaced: a far entry held from an earlier frame
    /// (<see cref="SdfUpdateRates.ShadowFar"/>), whose layers were drawn from those cameras and are read through them. The
    /// exit layer's camera differs from the depth's only on the frame a refresh redraws the depth alone.</summary>
    /// <param name="far">The camera the far entry's depth was drawn from.</param>
    /// <param name="exit">The camera its exit layer was drawn from.</param>
    /// <returns>The set, every other member unchanged.</returns>
    public SdfMeshShadowCascades WithFar(in SdfMeshShadowCamera far, in SdfMeshShadowCamera exit) => new(
        count: Count,
        far: m_far with { Camera = far },
        farExit: exit,
        near: m_near,
        second: m_second,
        texels: Texels
    );
    /// <summary>Fits the cascade set of a view of a frame.</summary>
    /// <param name="frame">The frame.</param>
    /// <param name="view">The view's index in <see cref="SdfFrame.Views"/>.</param>
    /// <param name="texels">The texels along each side of the cascade depths the fit is drawn into and read from, or
    /// <see langword="null"/> for the view's <see cref="SdfViewQuality.ShadowResolution"/>: a pass that binds a cascade
    /// depth passes the depth's own size, which differs from the quality's while a graph of the new size builds.</param>
    /// <returns>The set; empty when no slotted directional light emits (no primary role) or the view skips
    /// shadows.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="frame"/> is <see langword="null"/>.</exception>
    public static SdfMeshShadowCascades Fit(SdfFrame frame, int view, int? texels = null) {
        ArgumentNullException.ThrowIfNull(argument: frame);

        var snapshot = frame.Views[Math.Clamp(value: view, min: 0, max: (frame.Views.Count - 1))];
        var lights = frame.Lights;
        var key = lights.RoleLight(role: SdfShadowSlots.Primary);

        if (snapshot.Quality.DisableSoftShadows || (key < 0)) {
            return default;
        }

        var camera = snapshot.Camera;
        var side = (texels ?? SdfShadowResolutions.Texels(resolution: snapshot.Quality.ShadowResolution));
        var coverage = frame.ShadowCoverage;
        var far = MathF.Min(coverage.Far, frame.FarDistance);
        var forward = -SdfLights.UnitDirection(direction: lights[key].Direction);
        var slope = lights[key].Param;
        var fill = lights.RoleLight(role: SdfShadowSlots.Secondary);
        var second = ((fill >= 0)
            ? new SdfMeshShadowEntry(
                Camera: Cascade(camera: camera, casters: coverage.Casters, forward: -SdfLights.UnitDirection(direction: lights[fill].Direction), depth: MathF.Min(SecondSplit, far), texels: side),
                Role: SdfShadowSlots.Secondary,
                Slope: lights[fill].Param
            )
            : default);
        var farCamera = Cascade(camera: camera, casters: coverage.Casters, forward: forward, depth: far, texels: side);

        return new SdfMeshShadowCascades(
            count: ((fill >= 0) ? 3 : 2),
            far: new SdfMeshShadowEntry(Camera: farCamera, Role: SdfShadowSlots.Primary, Slope: slope),
            farExit: farCamera,
            near: new SdfMeshShadowEntry(
                Camera: Cascade(camera: camera, casters: coverage.Casters, forward: forward, depth: MathF.Min(coverage.Near, far), texels: side),
                Role: SdfShadowSlots.Primary,
                Slope: slope
            ),
            second: second,
            texels: side
        );
    }

    // The cascade enclosing the frustum slice from the eye to `depth`: the slice's bounding sphere, whose center lies on
    // the view's forward axis, seen along the light, with its plane's center snapped to a whole one of its texels. The
    // radius is rounded up to a whole metre (SdfShadowCoverage.RadiusOf), which keeps the extent, and so the texel size,
    // still while the far distance or the field of view eases.
    private static SdfMeshShadowCamera Cascade(CameraSnapshot camera, float casters, Vector3 forward, float depth, int texels) {
        var lateral = (camera.TanHalfFieldOfView * camera.TanHalfFieldOfView) * (1f + (camera.AspectRatio * camera.AspectRatio));
        var centerDepth = MathF.Min(depth, ((depth * (1f + lateral)) * 0.5f));
        var radius = SdfShadowCoverage.RadiusOf(aspectRatio: camera.AspectRatio, depth: depth, tanHalfFieldOfView: camera.TanHalfFieldOfView);
        var center = (camera.Position + (camera.Forward * centerDepth));
        var reference = ((MathF.Abs(forward.Y) < 0.99f) ? Vector3.UnitY : Vector3.UnitX);
        // The camera basis keeps the view cameras' handedness (a camera along -Z has +X right and +Y up), so the mesh pass's
        // triangles keep their winding seen from the light.
        var right = Vector3.Normalize(Vector3.Cross(forward, reference));
        var up = Vector3.Cross(right, forward);
        var texel = ((2f * radius) / texels);
        var alongRight = (MathF.Round(Vector3.Dot(center, right) / texel) * texel);
        var alongUp = (MathF.Round(Vector3.Dot(center, up) / texel) * texel);
        var alongForward = Vector3.Dot(center, forward);
        var snapped = (((right * alongRight) + (up * alongUp)) + (forward * alongForward));
        var back = (radius + casters);

        return new SdfMeshShadowCamera(
            Far: (back + radius),
            Forward: forward,
            HalfHeight: radius,
            Near: 1f,
            Position: (snapped - (forward * back)),
            Right: right,
            Up: up
        );
    }
}
