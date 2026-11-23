using Puck.Abstractions.Cameras;

namespace Puck.SdfVm;

/// <summary>
/// What one raster pass keeps of a frame's mesh draws: a view's mesh and glass passes the draws whose box
/// (<see cref="SdfMeshBox"/>) may lie in the view's frustum before the frame's far distance, a world's shadow entry the
/// draws whose box lies in its light's box (<see cref="OfLightBox"/>), and <see cref="None"/> every draw. A draw with no
/// host box is always kept. Two culls are equal when they keep by the same values, which a consumer that holds one across
/// frames compares (<see cref="SdfMotionScope"/>).
/// </summary>
public readonly record struct SdfMeshCull {
    /// <summary>The share of a view's near distance a draw is kept in front of: a face standing on the near plane, as a
    /// portal frame's face stands on the glass a window's camera starts at, rasterizes where its depth rounds onto the
    /// plane, so the cull keeps what lies within a thousandth of the distance before it.</summary>
    public const float NearKeeps = 0.999f;

    private readonly CameraSnapshot m_camera;
    private readonly SdfMeshShadowCamera m_cascade;
    private readonly float m_far;
    private readonly float m_margin;
    private readonly byte m_kind;

    private SdfMeshCull(byte kind, CameraSnapshot camera, float far, float margin, SdfMeshShadowCamera cascade) {
        m_kind = kind;
        m_camera = camera;
        m_far = far;
        m_margin = margin;
        m_cascade = cascade;
    }

    /// <summary>Gets the cull that keeps every draw.</summary>
    public static SdfMeshCull None => default;

    /// <summary>Returns what the cull keeps by, for a report: the view's frustum, a light's box, or every draw.</summary>
    /// <returns>The name.</returns>
    public override string ToString() => (m_kind switch {
        1 => "view",
        3 => "light-box",
        _ => "every-draw",
    });

    // GPU selection's pass controls; KEEP IN SYNC with sdf-mesh-selection.hlsli. The camera here is the unjittered
    // camera used by the host cull, including NearKeeps, rather than the raster projection's temporal sample.
    internal void WriteGpu(Span<System.Numerics.Vector4> rows) {
        rows[0] = new(m_kind, 0f, 0f, 0f);
        rows[1] = new(m_camera.Position, m_far);
        rows[2] = new(m_camera.Right, m_camera.TanHalfFieldOfView);
        rows[3] = new(m_camera.Up, m_camera.AspectRatio);
        rows[4] = new(m_camera.Forward, m_camera.Near);
        rows[5] = new(m_camera.FrustumOffset.X, m_camera.FrustumOffset.Y, m_margin, 0f);
        rows[6] = new(m_cascade.Position, m_cascade.HalfHeight);
        rows[7] = new(m_cascade.Right, m_cascade.Near);
        rows[8] = new(m_cascade.Up, m_cascade.Far);
        rows[9] = new(m_cascade.Forward, 0f);
    }

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
        margin: SdfMeshBox.MarginOf(height: height, width: width)
    );
    /// <summary>Returns the cull of a world's shadow entry (<see cref="SdfWorldShadows"/>), held across every view's turns
    /// and moves: the draws whose box lies in its light's box, whatever any view shows.</summary>
    /// <param name="cascade">The entry's light camera.</param>
    /// <returns>The cull.</returns>
    public static SdfMeshCull OfLightBox(in SdfMeshShadowCamera cascade) => new(
        camera: default,
        cascade: cascade,
        far: 0f,
        kind: 3,
        margin: 0f
    );

    /// <summary>Returns whether the pass draws a draw of a given box.</summary>
    /// <param name="box">The draw's box, or <see langword="null"/> for a draw no host box bounds.</param>
    /// <returns><see langword="false"/> only for a draw with a box the pass cannot show any of.</returns>
    public bool Keeps(in SdfMeshBox? box) => (
        (box is not { } bounded) ||
        (m_kind switch {
            1 => bounded.MaySee(camera: in m_camera, far: m_far, margin: m_margin),
            3 => bounded.MayCast(cascade: in m_cascade),
            _ => true,
        })
    );
}
