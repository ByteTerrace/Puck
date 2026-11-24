using System.Numerics;
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
/// <summary>One entry of a world's cascade set: the light camera it is drawn from, the shadow role its light holds
/// (<see cref="SdfLights.RoleSlot"/>) and that light's penumbra half-slope.</summary>
/// <param name="Camera">The entry's light camera.</param>
/// <param name="Role">The shadow role whose light the entry shadows, <see cref="SdfShadowSlots.Primary"/> or
/// <see cref="SdfShadowSlots.Secondary"/>.</param>
/// <param name="Slope">The light's penumbra half-slope, the tangent of its angular radius: zero for a point source.</param>
public readonly record struct SdfMeshShadowEntry(SdfMeshShadowCamera Camera, int Role, float Slope);
/// <summary>
/// The baked-mesh path's shadow cascades for one world, one indexed set of entries (<see cref="Entry"/>), each a light
/// camera for a shadow role. The set holds, in this order: the primary role's near and far entries, reaching
/// <see cref="SdfShadowCoverage.Near"/> and <see cref="SdfShadowCoverage.Far"/> metres (or the frame's far distance) from the
/// eye in every direction; then the secondary role's one entry over <see cref="SecondReach"/> metres; then, while the
/// frame's distant map is on (<see cref="SdfFrame.DistantShadows"/>), the primary role's distant entry over its authored
/// reach at texels of its own. A receiver takes the first entry of its light's role that holds it, and where none does, the
/// probes' answer.
/// <para>Every entry is anchored to the world, never to the way a camera faces: its square is centred on the cell of a grid
/// of an eighth of its reach that the eye stands in (<see cref="CellOf"/>), its half extent the reach plus the most an eye
/// inside that cell lies from its centre, and its plane's centre snaps to a whole texel. So a turning camera moves no entry,
/// a walking one moves it only as it crosses a cell, and a still light's shadow never crawls. At the fields of view the
/// views use, the frustum slice's bounding sphere the entry stands in for is as large: a 16 m slice at a 70 degree vertical
/// field of view and a 16:9 aspect has a 23 m sphere, the eye-centred entry 18 m. The fit is a pure function of the eye, the
/// lights and the coverage, so every reader agrees; the world's shadows (<see cref="SdfWorldShadows"/>) replace each entry's
/// camera by the one its held layers were drawn from (<see cref="WithCamera"/>).</para>
/// </summary>
public readonly struct SdfMeshShadowCascades {
    /// <summary>The constant depth bias a receiver subtracts, in metres.</summary>
    public const float ConstantBias = 0.02f;
    /// <summary>The receiver's offset along its triangle's normal, in cascade texels.</summary>
    public const float NormalOffsetTexels = 1.5f;
    /// <summary>The most entries a set holds; each has its own depth layers.</summary>
    public const int MaxEntries = SdfWorldPackage.MeshShadowEntries;
    /// <summary>The entry the primary role's near reach holds.</summary>
    public const int NearEntry = 0;
    /// <summary>The entry the primary role's far reach holds.</summary>
    public const int FarEntry = 1;
    /// <summary>The entry the secondary role holds.</summary>
    public const int SecondEntry = 2;
    /// <summary>The entry the primary role's distant map holds.</summary>
    public const int DistantEntry = 3;
    /// <summary>The cells of an entry's grid across its reach: the eye may move one cell, an eighth of the reach, before
    /// the entry is centred again.</summary>
    public const int CellsPerReach = 8;

    /// <summary>The distance, in metres from the eye, the secondary role's one entry reaches.</summary>
    public const float SecondReach = 40f;

    private readonly SdfMeshShadowEntry m_near;
    private readonly SdfMeshShadowEntry m_far;
    private readonly SdfMeshShadowEntry m_second;
    private readonly SdfMeshShadowEntry m_distant;

    private SdfMeshShadowCascades(SdfMeshShadowEntry near, SdfMeshShadowEntry far, SdfMeshShadowEntry second, SdfMeshShadowEntry distant, int count, int texels, int distantTexels) {
        m_near = near;
        m_far = far;
        m_second = second;
        m_distant = distant;
        Count = count;
        Texels = texels;
        DistantTexels = distantTexels;
    }

    /// <summary>Gets the entries the pass block carries, a prefix of the set: zero when no slotted light emits or shadows
    /// are off, two for the primary role alone, three with the secondary role, four with the distant map. An entry inside
    /// the prefix may still be empty (<see cref="Holds"/>): the secondary entry of a set with the distant map and no
    /// secondary role, or an entry whose layers no submitted upload has drawn yet.</summary>
    public int Count { get; }
    /// <summary>Gets the texels along each side of the near, far and secondary entries' depths, zero when the set is
    /// empty.</summary>
    public int Texels { get; }
    /// <summary>Gets the texels along each side of the distant entry's depth, zero when the set holds no distant map.</summary>
    public int DistantTexels { get; }

    /// <summary>Gets one entry's camera.</summary>
    /// <param name="index">The entry (<see cref="NearEntry"/>, <see cref="FarEntry"/>, <see cref="SecondEntry"/>,
    /// <see cref="DistantEntry"/>).</param>
    /// <returns>The camera.</returns>
    public SdfMeshShadowCamera this[int index] => Entry(index: index).Camera;

    /// <summary>Returns one entry of the set.</summary>
    /// <param name="index">The entry, from zero below <see cref="MaxEntries"/>; one the set does not hold is empty.</param>
    /// <returns>The entry.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="index"/> is outside the set.</exception>
    public SdfMeshShadowEntry Entry(int index) => index switch {
        NearEntry => m_near,
        FarEntry => m_far,
        SecondEntry => m_second,
        DistantEntry => m_distant,
        _ => throw new ArgumentOutOfRangeException(paramName: nameof(index)),
    };
    /// <summary>Returns whether the set holds an entry: one inside <see cref="Count"/> with a camera to be drawn from and
    /// read through.</summary>
    /// <param name="index">The entry, from zero below <see cref="MaxEntries"/>.</param>
    /// <returns><see langword="true"/> for an entry the world's shadows draw and the surface pass reads.</returns>
    public bool Holds(int index) => ((index >= 0) && (index < Count) && (Entry(index: index).Camera.HalfHeight > 0f));
    /// <summary>Returns the texels along each side of an entry's depth.</summary>
    /// <param name="index">The entry, from zero below <see cref="MaxEntries"/>.</param>
    /// <returns>The texels a side: <see cref="DistantTexels"/> for the distant entry, <see cref="Texels"/> for any
    /// other.</returns>
    public int TexelsOf(int index) => ((index == DistantEntry) ? DistantTexels : Texels);
    /// <summary>Returns this set with one entry's camera replaced by the one its held layers were drawn from, or with no
    /// camera (the entry empty, <see cref="Holds"/> false) while no layer is drawn to read.</summary>
    /// <param name="index">The entry, from zero below <see cref="MaxEntries"/>.</param>
    /// <param name="camera">The camera the entry's layers were drawn from, or <see langword="default"/> for none.</param>
    /// <returns>The set, every other member unchanged.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="index"/> is outside the set.</exception>
    public SdfMeshShadowCascades WithCamera(int index, in SdfMeshShadowCamera camera) => new(
        count: Count,
        distant: ((index == DistantEntry) ? (m_distant with { Camera = camera }) : m_distant),
        distantTexels: DistantTexels,
        far: ((index == FarEntry) ? (m_far with { Camera = camera }) : m_far),
        near: ((index == NearEntry) ? (m_near with { Camera = camera }) : ((index is >= 0 and < MaxEntries) ? m_near : throw new ArgumentOutOfRangeException(paramName: nameof(index)))),
        second: ((index == SecondEntry) ? (m_second with { Camera = camera }) : m_second),
        texels: Texels
    );
    /// <summary>Returns the reach, in metres from the eye in every direction, an entry of a frame's set covers.</summary>
    /// <param name="frame">The frame.</param>
    /// <param name="index">The entry, from zero below <see cref="MaxEntries"/>.</param>
    /// <returns>The reach: the coverage's near or far distance (the far one bounded by the frame's far distance), the
    /// secondary role's reach, or the distant map's.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="frame"/> is <see langword="null"/>.</exception>
    public static float ReachOf(SdfFrame frame, int index) {
        ArgumentNullException.ThrowIfNull(argument: frame);

        var coverage = frame.ShadowCoverage;
        var far = MathF.Min(coverage.Far, frame.FarDistance);

        return index switch {
            NearEntry => MathF.Min(coverage.Near, far),
            FarEntry => far,
            SecondEntry => MathF.Min(SecondReach, far),
            _ => frame.DistantShadows.Reach,
        };
    }
    /// <summary>Returns the cell of an entry's grid an eye stands in, which the entry is centred on.</summary>
    /// <param name="reach">The entry's reach, in metres.</param>
    /// <param name="eye">The eye, in world space.</param>
    /// <returns>The cell's centre, in world space.</returns>
    public static Vector3 CellOf(float reach, Vector3 eye) {
        var cell = (reach / CellsPerReach);

        return ((cell > 0f)
            ? new Vector3(x: (MathF.Round(x: (eye.X / cell)) * cell), y: (MathF.Round(x: (eye.Y / cell)) * cell), z: (MathF.Round(x: (eye.Z / cell)) * cell))
            : eye);
    }
    /// <summary>Returns the half extent, in metres, of an entry's square: its reach and the most an eye inside a cell lies
    /// from the cell's centre, rounded up to a whole metre, so a receiver at the reach in any direction from anywhere in
    /// the cell is inside it.</summary>
    /// <param name="reach">The entry's reach, in metres.</param>
    /// <returns>The half extent.</returns>
    public static float HalfExtentOf(float reach) => MathF.Ceiling(x: (reach + ((reach / CellsPerReach) * 0.8660254f)));
    /// <summary>Fits the cascade set of a frame, eye-centred on one of its views.</summary>
    /// <param name="frame">The frame.</param>
    /// <param name="view">The index in <see cref="SdfFrame.Views"/> of the view whose eye the set is centred on and whose
    /// quality sets its texels.</param>
    /// <param name="texels">The texels along each side of the near, far and secondary depths, or <see langword="null"/>
    /// for the view's <see cref="SdfViewQuality.ShadowResolution"/>.</param>
    /// <param name="distantTexels">The texels along each side of the distant map's depth, or <see langword="null"/> for the
    /// frame's distant map at the view's shadow resolution (<see cref="SdfDistantShadows.TexelsAt"/>).</param>
    /// <returns>The set; empty when no slotted directional light emits (no primary role) or the view skips
    /// shadows.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="frame"/> is <see langword="null"/>.</exception>
    public static SdfMeshShadowCascades Fit(SdfFrame frame, int view, int? texels = null, int? distantTexels = null) {
        ArgumentNullException.ThrowIfNull(argument: frame);
        SdfCadenceWork.ProcessWork.Count(kind: SdfCadenceWork.Fits);

        if (frame.Views.Count == 0) { return default; }

        var snapshot = frame.Views[Math.Clamp(value: view, min: 0, max: (frame.Views.Count - 1))];
        var lights = frame.Lights;
        var key = lights.RoleLight(role: SdfShadowSlots.Primary);

        if (snapshot.Quality.DisableSoftShadows || (key < 0)) {
            return default;
        }

        var eye = snapshot.Camera.Position;
        var quality = TexelsFor(frame: frame, view: view);
        var side = (texels ?? quality.Texels);
        var distantSide = (distantTexels ?? quality.DistantTexels);
        var casters = frame.ShadowCoverage.Casters;
        var forward = -SdfLights.UnitDirection(direction: lights[key].Direction);
        var slope = lights[key].Param;
        var fill = lights.RoleLight(role: SdfShadowSlots.Secondary);
        var second = ((fill >= 0)
            ? new SdfMeshShadowEntry(
                Camera: Anchored(casters: casters, eye: eye, forward: -SdfLights.UnitDirection(direction: lights[fill].Direction), reach: ReachOf(frame: frame, index: SecondEntry), texels: side),
                Role: SdfShadowSlots.Secondary,
                Slope: lights[fill].Param
            )
            : default);
        var distant = ((frame.DistantShadows.IsOn && (distantSide > 0))
            ? new SdfMeshShadowEntry(
                Camera: Anchored(casters: casters, eye: eye, forward: forward, reach: ReachOf(frame: frame, index: DistantEntry), texels: distantSide),
                Role: SdfShadowSlots.Primary,
                Slope: slope
            )
            : default);

        return new SdfMeshShadowCascades(
            count: ((distant.Camera.HalfHeight > 0f) ? 4 : ((fill >= 0) ? 3 : 2)),
            distant: distant,
            distantTexels: ((distant.Camera.HalfHeight > 0f) ? distantSide : 0),
            far: new SdfMeshShadowEntry(Camera: Anchored(casters: casters, eye: eye, forward: forward, reach: ReachOf(frame: frame, index: FarEntry), texels: side), Role: SdfShadowSlots.Primary, Slope: slope),
            near: new SdfMeshShadowEntry(Camera: Anchored(casters: casters, eye: eye, forward: forward, reach: ReachOf(frame: frame, index: NearEntry), texels: side), Role: SdfShadowSlots.Primary, Slope: slope),
            second: second,
            texels: side
        );
    }

    /// <summary>Returns the texels a view's quality fits its cascade set at when a caller names none
    /// (<see cref="Fit"/>): the view's shadow resolution, and the frame's distant map at that resolution.</summary>
    /// <param name="frame">The frame.</param>
    /// <param name="view">The view's index in <see cref="SdfFrame.Views"/>, clamped to the frame's views.</param>
    /// <returns>The texels along each side of the near, far and secondary depths, and of the distant map's depth (zero
    /// while the frame's distant map is off).</returns>
    /// <exception cref="ArgumentNullException"><paramref name="frame"/> is <see langword="null"/>.</exception>
    public static (int Texels, int DistantTexels) TexelsFor(SdfFrame frame, int view) {
        ArgumentNullException.ThrowIfNull(argument: frame);

        var resolution = frame.Views[Math.Clamp(value: view, min: 0, max: (frame.Views.Count - 1))].Quality.ShadowResolution;

        return (SdfShadowResolutions.Texels(full: frame.ShadowTexels, resolution: resolution), frame.DistantShadows.TexelsAt(resolution: resolution));
    }

    // An entry of `reach` metres: the square about the cell of its grid the eye stands in, seen along the light.
    private static SdfMeshShadowCamera Anchored(Vector3 eye, float reach, float casters, Vector3 forward, int texels) =>
        LightCamera(casters: casters, center: CellOf(eye: eye, reach: reach), forward: forward, radius: HalfExtentOf(reach: reach), texels: texels);
    // The orthographic light camera enclosing a sphere seen along the light, its plane's center snapped to a whole one of
    // its texels, with casters up to `casters` metres toward the light past the sphere.
    private static SdfMeshShadowCamera LightCamera(Vector3 center, float radius, float casters, Vector3 forward, int texels) {
        var reference = ((MathF.Abs(forward.Y) < 0.99f) ? Vector3.UnitY : Vector3.UnitX);
        // The camera basis keeps the view cameras' handedness (a camera along -Z has +X right and +Y up), so the mesh pass's
        // triangles keep their winding seen from the light.
        var right = Vector3.Normalize(Vector3.Cross(forward, reference));
        var up = Vector3.Cross(right, forward);
        var texel = ((2f * radius) / Math.Max(val1: texels, val2: 1));
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
