using System.Numerics;

namespace Puck.Abstractions.Cameras;

/// <summary>One view's camera matrices, in the convention rasterized geometry and the SDF march share.</summary>
/// <remarks>
/// <para>Matrices use <see cref="System.Numerics"/>' row-vector convention: a point transforms as <c>p * M</c>. World
/// space is right-handed. View space puts the camera at the origin looking down −Z, with +X the camera's right and +Y
/// its up.</para>
/// <para>The projection is reversed-Z with an infinite far plane: depth is <c>Near / d</c> for a point at forward
/// distance <c>d</c>, so it is 1 on the near plane, falls toward 0 with distance, and never reaches 0. A nearer surface
/// always has the greater depth. Depth lies in [0, 1] on both backends.</para>
/// <para>Normalized device coordinates put +Y up. A view's UV origin is its top-left corner, and a pixel's sample is its
/// center (<see cref="NdcOf"/>), which is the ray the SDF march takes through that pixel. No jitter is applied
/// (<see cref="Jitter"/>).</para>
/// <para><see cref="RayParameter"/> reconstructs, from a depth, the ray parameter the SDF march records: the
/// Euclidean distance from the camera along the normalized ray through the sample.</para>
/// <para>The previous frame's matrices ride beside the current ones for motion. A view with no previous frame, or one
/// whose history is invalid, carries its own matrices as the previous ones, so it reports no motion.</para>
/// </remarks>
public readonly record struct ViewProjection {
    private ViewProjection(Vector3 position, float near, Vector2 frustumOffset, Matrix4x4 worldToView, Matrix4x4 viewToClip, Matrix4x4 clipToWorld) {
        Position = position;
        Near = near;
        FrustumOffset = frustumOffset;
        WorldToView = worldToView;
        ViewToClip = viewToClip;
        WorldToClip = (worldToView * viewToClip);
        ClipToWorld = clipToWorld;
        PreviousWorldToView = worldToView;
        PreviousWorldToClip = WorldToClip;
    }

    /// <summary>Gets the sub-pixel sample offset the projection applies, in normalized device coordinates: zero,
    /// because every sample is a pixel center.</summary>
    public static Vector2 Jitter => Vector2.Zero;
    /// <summary>Gets the inverse of <see cref="WorldToClip"/>, formed analytically rather than by a general
    /// inversion.</summary>
    public Matrix4x4 ClipToWorld { get; }
    /// <summary>Gets the off-axis frustum's tangent-space center offset; zero for a symmetric frustum.</summary>
    public Vector2 FrustumOffset { get; }
    /// <summary>Gets the near-plane distance, where depth is 1.</summary>
    public float Near { get; }
    /// <summary>Gets the camera's world-space position.</summary>
    public Vector3 Position { get; }
    /// <summary>Gets the previous frame's world-to-clip transform.</summary>
    public Matrix4x4 PreviousWorldToClip { get; private init; }
    /// <summary>Gets the previous frame's world-to-view transform.</summary>
    public Matrix4x4 PreviousWorldToView { get; private init; }
    /// <summary>Gets the view-to-clip transform: the reversed-Z infinite-far projection.</summary>
    public Matrix4x4 ViewToClip { get; }
    /// <summary>Gets the world-to-clip transform, <see cref="WorldToView"/> then <see cref="ViewToClip"/>.</summary>
    public Matrix4x4 WorldToClip { get; }
    /// <summary>Gets the world-to-view transform.</summary>
    public Matrix4x4 WorldToView { get; }

    /// <summary>Creates the matrices of <paramref name="camera"/> with its previous frame equal to itself.</summary>
    /// <param name="camera">The camera basis, field of view, and aspect ratio.</param>
    /// <param name="near">The near-plane distance; positive and finite.</param>
    /// <param name="frustumOffset">The off-axis frustum's tangent-space center offset, added to the ray through each
    /// sample along the camera's right and up axes; zero for a symmetric frustum.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="near"/> is not positive and finite, or
    /// <paramref name="frustumOffset"/> is not finite.</exception>
    public static ViewProjection Create(CameraSnapshot camera, float near, Vector2 frustumOffset = default) {
        if (
            !float.IsFinite(f: near) ||
            (near <= 0f)
        ) {
            throw new ArgumentOutOfRangeException(
                actualValue: near,
                message: "The near-plane distance must be positive and finite.",
                paramName: nameof(near)
            );
        }
        if (
            !float.IsFinite(f: frustumOffset.X) ||
            !float.IsFinite(f: frustumOffset.Y)
        ) {
            throw new ArgumentOutOfRangeException(
                actualValue: frustumOffset,
                message: "The frustum offset must be finite.",
                paramName: nameof(frustumOffset)
            );
        }

        var right = camera.Right;
        var up = camera.Up;
        var forward = camera.Forward;
        var position = camera.Position;
        var scaleX = (1f / (camera.AspectRatio * camera.TanHalfFieldOfView));
        var scaleY = (1f / camera.TanHalfFieldOfView);
        var worldToView = new Matrix4x4(
            m11: right.X,
            m12: up.X,
            m13: -forward.X,
            m14: 0f,
            m21: right.Y,
            m22: up.Y,
            m23: -forward.Y,
            m24: 0f,
            m31: right.Z,
            m32: up.Z,
            m33: -forward.Z,
            m34: 0f,
            m41: -Vector3.Dot(
                vector1: right,
                vector2: position
            ),
            m42: -Vector3.Dot(
                vector1: up,
                vector2: position
            ),
            m43: Vector3.Dot(
                vector1: forward,
                vector2: position
            ),
            m44: 1f
        );
        // clip = (sx (x - ox w), sy (y - oy w), near, w) with w = -z, the forward distance, so a point on the ray
        // w (ndc / s + offset, -1) lands back on ndc.
        var viewToClip = new Matrix4x4(
            m11: scaleX,
            m12: 0f,
            m13: 0f,
            m14: 0f,
            m21: 0f,
            m22: scaleY,
            m23: 0f,
            m24: 0f,
            m31: (scaleX * frustumOffset.X),
            m32: (scaleY * frustumOffset.Y),
            m33: 0f,
            m34: -1f,
            m41: 0f,
            m42: 0f,
            m43: near,
            m44: 0f
        );
        var clipToView = new Matrix4x4(
            m11: (1f / scaleX),
            m12: 0f,
            m13: 0f,
            m14: 0f,
            m21: 0f,
            m22: (1f / scaleY),
            m23: 0f,
            m24: 0f,
            m31: 0f,
            m32: 0f,
            m33: 0f,
            m34: (1f / near),
            m41: frustumOffset.X,
            m42: frustumOffset.Y,
            m43: -1f,
            m44: 0f
        );
        var viewToWorld = new Matrix4x4(
            m11: right.X,
            m12: right.Y,
            m13: right.Z,
            m14: 0f,
            m21: up.X,
            m22: up.Y,
            m23: up.Z,
            m24: 0f,
            m31: -forward.X,
            m32: -forward.Y,
            m33: -forward.Z,
            m34: 0f,
            m41: position.X,
            m42: position.Y,
            m43: position.Z,
            m44: 1f
        );

        return new ViewProjection(
            clipToWorld: (clipToView * viewToWorld),
            frustumOffset: frustumOffset,
            near: near,
            position: position,
            viewToClip: viewToClip,
            worldToView: worldToView
        );
    }
    /// <summary>Maps a view UV (origin top-left, +Y down) to normalized device coordinates (+Y up).</summary>
    /// <param name="uv">The view UV; a pixel's sample is <c>(pixel + 0.5) / extent</c>.</param>
    /// <returns>The normalized device coordinates.</returns>
    public static Vector2 NdcOf(Vector2 uv) =>
        new(
            x: ((uv.X * 2f) - 1f),
            y: (1f - (uv.Y * 2f))
        );
    /// <summary>Gets the depth of a point at forward distance <paramref name="forwardDistance"/>.</summary>
    /// <param name="forwardDistance">The distance along the camera's forward axis; at least <see cref="Near"/> for a
    /// visible point.</param>
    /// <returns><c>Near / forwardDistance</c>.</returns>
    public float DepthAt(float forwardDistance) =>
        (Near / forwardDistance);
    /// <summary>Gets the ray parameter the SDF march records for the sample at <paramref name="ndc"/> with
    /// <paramref name="depth"/>: the Euclidean distance from <see cref="Position"/> to that surface.</summary>
    /// <param name="ndc">The sample's normalized device coordinates.</param>
    /// <param name="depth">The sample's depth, in (0, 1].</param>
    /// <returns>The distance along the normalized ray through the sample.</returns>
    public float RayParameter(Vector2 ndc, float depth) {
        var direction = new Vector3(
            x: ((ndc.X / ViewToClip.M11) + FrustumOffset.X),
            y: ((ndc.Y / ViewToClip.M22) + FrustumOffset.Y),
            z: 1f
        );

        return ((Near / depth) * direction.Length());
    }
    /// <summary>Transforms a world-space point to clip space.</summary>
    /// <param name="world">The world-space point.</param>
    /// <returns>The homogeneous clip-space position; its <c>W</c> is the forward distance.</returns>
    public Vector4 ToClip(Vector3 world) =>
        Vector4.Transform(
            matrix: WorldToClip,
            position: world
        );
    /// <summary>Reconstructs the world-space point at <paramref name="ndc"/> and <paramref name="depth"/>.</summary>
    /// <param name="ndc">The sample's normalized device coordinates.</param>
    /// <param name="depth">The sample's depth, in (0, 1].</param>
    /// <returns>The world-space point.</returns>
    public Vector3 Unproject(Vector2 ndc, float depth) {
        var world = Vector4.Transform(
            matrix: ClipToWorld,
            vector: new Vector4(
                w: 1f,
                x: ndc.X,
                y: ndc.Y,
                z: depth
            )
        );

        return (new Vector3(
            x: world.X,
            y: world.Y,
            z: world.Z
        ) / world.W);
    }
    /// <summary>Returns these matrices with <paramref name="previous"/>'s current matrices as the previous frame's.</summary>
    /// <param name="previous">The same view's matrices one frame earlier.</param>
    /// <returns>The view with its previous-frame transforms set.</returns>
    public ViewProjection WithPrevious(ViewProjection previous) =>
        (this with {
            PreviousWorldToClip = previous.WorldToClip,
            PreviousWorldToView = previous.WorldToView,
        });
}
