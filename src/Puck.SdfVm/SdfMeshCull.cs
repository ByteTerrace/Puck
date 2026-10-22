using Puck.Abstractions.Cameras;

namespace Puck.SdfVm;

/// <summary>
/// What one raster pass of a view keeps of a frame's mesh draws: a view's mesh and glass passes the draws whose box
/// (<see cref="SdfMeshBox"/>) may lie in the view's frustum before the frame's far distance, a shadow cascade the draws
/// whose box lies in its light's box and, swept along the light, may shadow a receiver the view shows, and
/// <see cref="None"/> every draw. A draw with no host box is always kept.
/// </summary>
public readonly struct SdfMeshCull {
    /// <summary>The texels beside a receiver a cascade's shadow filter reads at most: the blocker search's widest disk
    /// (<c>SdfMeshShadowMaxTexels</c>, <c>passes/sdf-surface-stage.hlsli</c>) and the receiver's normal offset, rounded
    /// up.</summary>
    public const float ShadowFilterTexels = 18f;
    /// <summary>The share of a view's near distance a draw is kept in front of: a face standing on the near plane, as a
    /// portal frame's face stands on the glass a window's camera starts at, rasterizes where its depth rounds onto the
    /// plane, so the cull keeps what lies within a thousandth of the distance before it.</summary>
    public const float NearKeeps = 0.999f;

    private readonly CameraSnapshot m_camera;
    private readonly SdfMeshShadowCamera m_cascade;
    private readonly float m_far;
    private readonly float m_margin;
    private readonly float m_reach;
    private readonly byte m_kind;

    private SdfMeshCull(byte kind, CameraSnapshot camera, float far, float margin, SdfMeshShadowCamera cascade, float reach) {
        m_kind = kind;
        m_camera = camera;
        m_far = far;
        m_margin = margin;
        m_cascade = cascade;
        m_reach = reach;
    }

    /// <summary>Gets the cull that keeps every draw.</summary>
    public static SdfMeshCull None => default;

    /// <summary>Returns the cull of a view's mesh or glass pass.</summary>
    /// <param name="camera">The view's camera.</param>
    /// <param name="far">The frame's far distance, in world units.</param>
    /// <param name="width">The pixels across the view's render extent.</param>
    /// <param name="height">The pixels down the view's render extent.</param>
    /// <returns>The cull.</returns>
    public static SdfMeshCull Of(in CameraSnapshot camera, float far, uint width, uint height) => new(
        camera: (camera with { Near = (camera.Near * NearKeeps) }),
        cascade: default,
        far: far,
        kind: 1,
        margin: SdfMeshBox.MarginOf(height: height, width: width),
        reach: 0f
    );
    /// <summary>Returns the cull of one of a view's shadow cascades.</summary>
    /// <param name="cascade">The cascade's light camera.</param>
    /// <param name="camera">The view's camera, whose receivers the cascade shadows.</param>
    /// <param name="far">The frame's far distance, in world units.</param>
    /// <param name="texels">The texels across a side of the cascade's depth.</param>
    /// <param name="width">The pixels across the view's render extent.</param>
    /// <param name="height">The pixels down the view's render extent.</param>
    /// <returns>The cull.</returns>
    public static SdfMeshCull Of(in SdfMeshShadowCamera cascade, in CameraSnapshot camera, float far, int texels, uint width, uint height) => new(
        camera: camera,
        cascade: cascade,
        far: far,
        kind: 2,
        margin: SdfMeshBox.MarginOf(height: height, width: width),
        reach: (ShadowFilterTexels * ((2f * cascade.HalfHeight) / Math.Max(val1: texels, val2: 1)))
    );

    /// <summary>Returns whether the pass draws a draw of a given box.</summary>
    /// <param name="box">The draw's box, or <see langword="null"/> for a draw no host box bounds.</param>
    /// <returns><see langword="false"/> only for a draw with a box the pass cannot show any of.</returns>
    public bool Keeps(in SdfMeshBox? box) => (
        (box is not { } bounded) ||
        (m_kind switch {
            1 => bounded.MaySee(camera: in m_camera, far: m_far, margin: m_margin),
            2 => (bounded.MayCast(cascade: in m_cascade) && bounded.MayShadow(camera: in m_camera, cascade: in m_cascade, far: m_far, margin: m_margin, reach: m_reach)),
            _ => true,
        })
    );
}
