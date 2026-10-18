using System.Numerics;
using Puck.Abstractions.Cameras;
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
/// <summary>
/// The baked-mesh path's shadow cascades for one view: two orthographic cameras along the key light (the light in the
/// frame's first shadow slot), each enclosing a slice of the view's frustum that starts at its eye, the near cascade the
/// first <see cref="NearSplit"/> metres and the far cascade up to <see cref="FarSplit"/> metres or the frame's far
/// distance, and one along the second shadow slot's light over the first <see cref="SecondSplit"/> metres. Each cascade is a bounding sphere of its slice, so its
/// extent does not change as the camera turns, and its plane's center snaps to a whole texel of the view's cascade
/// resolution (<see cref="SdfViewQuality.ShadowResolution"/>), so a still light's shadow does not crawl as the camera moves. The fit is a pure function of the camera, the light and the far distance, so every
/// pass of a frame that fits it agrees.
/// </summary>
public readonly struct SdfMeshShadowCascades {
    /// <summary>The depth, in metres from the eye, the near cascade covers.</summary>
    public const float NearSplit = 16f;
    /// <summary>The farthest depth, in metres from the eye, the far cascade covers.</summary>
    public const float FarSplit = 120f;
    /// <summary>The distance, in metres, past a cascade's sphere toward the light that its casters may lie.</summary>
    public const float CasterReach = 200f;
    /// <summary>The constant depth bias a receiver subtracts, in metres.</summary>
    public const float ConstantBias = 0.02f;
    /// <summary>The receiver's offset along its triangle's normal, in cascade texels.</summary>
    public const float NormalOffsetTexels = 1.5f;

    /// <summary>The depth, in metres from the eye, the second shadow slot's one cascade covers.</summary>
    public const float SecondSplit = 40f;

    private readonly SdfMeshShadowCamera m_near;
    private readonly SdfMeshShadowCamera m_far;
    private readonly SdfMeshShadowCamera m_second;

    private SdfMeshShadowCascades(SdfMeshShadowCamera near, SdfMeshShadowCamera far, SdfMeshShadowCamera second, int count, float penumbraSlope, float secondSlope, int texels) {
        m_near = near;
        m_far = far;
        m_second = second;
        Count = count;
        PenumbraSlope = penumbraSlope;
        SecondPenumbraSlope = secondSlope;
        Texels = texels;
    }

    /// <summary>Gets the key light's cascades the frame renders: zero when it casts no shadow.</summary>
    public int Count { get; }
    /// <summary>Gets the key light's penumbra slope: the penumbra's width per metre between a blocker and its
    /// receiver.</summary>
    public float PenumbraSlope { get; }
    /// <summary>Gets the second shadow slot's penumbra slope, zero when that slot holds no directional light, so its
    /// cascade (index 2) renders nothing.</summary>
    public float SecondPenumbraSlope { get; }
    /// <summary>Gets the texels along each side of every cascade's depth, zero when the key light casts no shadow.</summary>
    public int Texels { get; }

    /// <summary>Gets one cascade's camera.</summary>
    /// <param name="index">The cascade: zero the key light's near one, one its far one, two the second slot's.</param>
    /// <returns>The camera.</returns>
    public SdfMeshShadowCamera this[int index] => index switch {
        0 => m_near,
        1 => m_far,
        _ => m_second,
    };

    /// <summary>Fits the cascades of a view of a frame.</summary>
    /// <param name="frame">The frame.</param>
    /// <param name="view">The view's index in <see cref="SdfFrame.Views"/>.</param>
    /// <param name="texels">The texels along each side of the cascade depths the fit is drawn into and read from, or
    /// <see langword="null"/> for the view's <see cref="SdfViewQuality.ShadowResolution"/>: a pass that binds a cascade
    /// depth passes the depth's own size, which differs from the quality's while a graph of the new size builds.</param>
    /// <returns>The cascades; none when the frame's first shadow slot holds no directional light or the view skips soft
    /// shadows.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="frame"/> is <see langword="null"/>.</exception>
    public static SdfMeshShadowCascades Fit(SdfFrame frame, int view, int? texels = null) {
        ArgumentNullException.ThrowIfNull(argument: frame);

        var snapshot = frame.Views[Math.Clamp(value: view, min: 0, max: (frame.Views.Count - 1))];
        var lights = frame.Lights;
        var slot = ((lights.ShadowSlots.SlotCount > 0) ? lights.ShadowSlots[0] : -1);

        if (snapshot.Quality.DisableSoftShadows || (slot < 0) || (slot >= lights.Count) || (lights[slot].Kind != SdfLightKind.Directional)) {
            return default;
        }

        var light = lights[slot];
        var forward = -SdfLights.UnitDirection(direction: light.Direction);
        var camera = snapshot.Camera;
        var side = (texels ?? SdfShadowResolutions.Texels(resolution: snapshot.Quality.ShadowResolution));
        var far = MathF.Min(FarSplit, frame.FarDistance);
        var secondSlot = ((lights.ShadowSlots.SlotCount > 1) ? lights.ShadowSlots[1] : -1);
        var hasSecond = ((secondSlot >= 0) && (secondSlot < lights.Count) && (lights[secondSlot].Kind == SdfLightKind.Directional));
        var second = (hasSecond
            ? Cascade(camera: camera, forward: -SdfLights.UnitDirection(direction: lights[secondSlot].Direction), depth: MathF.Min(SecondSplit, far), texels: side)
            : default);

        return new SdfMeshShadowCascades(
            count: 2,
            far: Cascade(camera: camera, forward: forward, depth: far, texels: side),
            near: Cascade(camera: camera, forward: forward, depth: MathF.Min(NearSplit, far), texels: side),
            penumbraSlope: MathF.Max(light.Param, 1.0e-3f),
            second: second,
            secondSlope: (hasSecond ? MathF.Max(lights[secondSlot].Param, 1.0e-3f) : 0f),
            texels: side
        );
    }

    // The cascade enclosing the frustum slice from the eye to `depth`: the slice's bounding sphere, whose center lies on
    // the view's forward axis, seen along the light, with its plane's center snapped to a whole one of its texels.
    private static SdfMeshShadowCamera Cascade(CameraSnapshot camera, Vector3 forward, float depth, int texels) {
        var lateral = (camera.TanHalfFieldOfView * camera.TanHalfFieldOfView) * (1f + (camera.AspectRatio * camera.AspectRatio));
        var centerDepth = MathF.Min(depth, ((depth * (1f + lateral)) * 0.5f));
        var radius = MathF.Sqrt(((depth - centerDepth) * (depth - centerDepth)) + ((depth * depth) * lateral));

        // A radius rounded up to a whole metre keeps the extent, and so the texel size, still while the far distance or
        // the field of view eases.
        radius = MathF.Ceiling(radius);

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
        var back = (radius + CasterReach);

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
