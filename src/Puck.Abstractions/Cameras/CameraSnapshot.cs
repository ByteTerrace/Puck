using System.Numerics;
using Puck.Maths;

namespace Puck.Abstractions.Cameras;

/// <summary>An immutable, finite camera basis and projection snapshot derived by <see cref="LookAt"/>.</summary>
public readonly record struct CameraSnapshot {
    private readonly Vector2 m_frustumOffset;
    private readonly float m_near;

    /// <summary>Initializes a finite camera snapshot from an already-derived basis and projection.</summary>
    public CameraSnapshot(Vector3 Position, Vector3 Right, Vector3 Up, Vector3 Forward, float TanHalfFieldOfView, float AspectRatio) {
        ValidateFinite(
            Position,
            nameof(Position)
        );
        ValidateFinite(
            Right,
            nameof(Right)
        );
        ValidateFinite(
            Up,
            nameof(Up)
        );
        ValidateFinite(
            Forward,
            nameof(Forward)
        );
        ValidateBasis(
            Right,
            nameof(Right)
        );
        ValidateBasis(
            Up,
            nameof(Up)
        );
        ValidateBasis(
            Forward,
            nameof(Forward)
        );

        if (
            (MathF.Abs(x: Vector3.Dot(
            vector1: Right,
            vector2: Up
        )) > 1e-3f) ||
            (MathF.Abs(x: Vector3.Dot(
            vector1: Right,
            vector2: Forward
        )) > 1e-3f) ||
            (MathF.Abs(x: Vector3.Dot(
            vector1: Up,
            vector2: Forward
        )) > 1e-3f)
        ) {
            throw new ArgumentException(message: "The camera basis vectors must be mutually perpendicular.");
        }

        if (
            !float.IsFinite(f: TanHalfFieldOfView) ||
            (TanHalfFieldOfView <= 0f)
        ) {
            throw new ArgumentOutOfRangeException(paramName: nameof(TanHalfFieldOfView));
        }
        if (
            !float.IsFinite(f: AspectRatio) ||
            (AspectRatio <= 0f)
        ) {
            throw new ArgumentOutOfRangeException(paramName: nameof(AspectRatio));
        }

        this.Position = Position;
        this.Right = Right;
        this.Up = Up;
        this.Forward = Forward;
        this.TanHalfFieldOfView = TanHalfFieldOfView;
        this.AspectRatio = AspectRatio;
    }

    /// <summary>Gets the viewport width-to-height ratio.</summary>
    public float AspectRatio { get; }
    /// <summary>Gets the normalized camera-forward basis vector.</summary>
    public Vector3 Forward { get; }
    /// <summary>Gets the off-axis frustum's tangent-space center offset: the ray through every image point gains
    /// <c>X·Right + Y·Up</c> as a trailing term, so the frustum shears without turning. Zero, the default, is a symmetric
    /// camera. A border window's camera (<c>Puck.SdfVm.Views.SdfAsymmetricFrustum</c>) carries a non-zero offset, which
    /// the SDF view pass's <c>cameraRayDirection</c>, the rasterized projection (<see cref="ViewProjection"/>) and a hit
    /// through the camera's image (<c>Puck.Commands.SourceRay.Through</c>) all read from here.</summary>
    /// <exception cref="ArgumentException">A component set at initialization is not finite.</exception>
    public Vector2 FrustumOffset {
        get => m_frustumOffset;
        init {
            if (!VectorFunctions.IsFinite(vector: value)) {
                throw new ArgumentException(
                    message: "The frustum offset must be finite.",
                    paramName: nameof(FrustumOffset)
                );
            }

            m_frustumOffset = value;
        }
    }
    /// <summary>Gets the forward distance of the camera's near plane: the image begins there, so nothing nearer along
    /// <see cref="Forward"/> is seen or hit. Zero, the default, begins the image at <see cref="Position"/>. A border
    /// window's camera (<c>Puck.SdfVm.Views.SdfAsymmetricFrustum</c>) puts it on the glass, so the window shows only
    /// what lies beyond its aperture; the SDF view pass starts its march there and a hit through the camera's image
    /// (<c>Puck.Commands.SourceRay.Through</c>) starts its ray there.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The distance set at initialization is negative or not
    /// finite.</exception>
    public float Near {
        get => m_near;
        init {
            if (
                !float.IsFinite(f: value) ||
                (value < 0f)
            ) {
                throw new ArgumentOutOfRangeException(
                    actualValue: value,
                    message: "The near-plane distance must be finite and not negative.",
                    paramName: nameof(Near)
                );
            }

            m_near = value;
        }
    }
    /// <summary>Gets the world-space camera position.</summary>
    public Vector3 Position { get; }
    /// <summary>Gets the normalized camera-right basis vector.</summary>
    public Vector3 Right { get; }
    /// <summary>Gets the tangent of half the vertical field of view.</summary>
    public float TanHalfFieldOfView { get; }
    /// <summary>Gets the normalized camera-up basis vector.</summary>
    public Vector3 Up { get; }

    private static Vector3 SafeNormalize(Vector3 value, Vector3 fallback) {
        var length = value.Length();

        return ((length > 1e-5f)
            ? (value / length)
            : fallback
        );
    }
    private static void ValidateBasis(Vector3 value, string paramName) {
        if (MathF.Abs(x: (value.LengthSquared() - 1f)) > 1e-3f) {
            throw new ArgumentException(
                message: "Camera basis vectors must be normalized.",
                paramName: paramName
            );
        }
    }
    private static void ValidateFinite(Vector3 value, string paramName) {
        if (!VectorFunctions.IsFinite(vector: value)) {
            throw new ArgumentException(
                message: "All vector components must be finite.",
                paramName: paramName
            );
        }
    }

    /// <summary>Returns the ray the camera casts through an image point, summed as the SDF view pass's
    /// <c>cameraRayDirection</c> (<c>frame/sdf-viewport.hlsli</c>) sums it: the forward axis, the point scaled by the
    /// half-extents on the right and up axes, then the <see cref="FrustumOffset"/> as a trailing term.</summary>
    /// <param name="ndc">The image point in normalized device coordinates, <c>[-1, 1]²</c> across the image, up
    /// positive.</param>
    /// <returns>The unnormalized direction, whose component along <see cref="Forward"/> is one.</returns>
    public Vector3 RayThrough(Vector2 ndc) {
        var direction = (
            (Forward +
            (((ndc.X * AspectRatio) * TanHalfFieldOfView) * Right)) +
            ((ndc.Y * TanHalfFieldOfView) * Up)
        );

        return (direction + ((FrustumOffset.X * Right) + (FrustumOffset.Y * Up)));
    }
    /// <summary>Projects a world point onto the camera's image: the inverse of <see cref="RayThrough"/>.</summary>
    /// <param name="world">The world-space point.</param>
    /// <param name="ndc">The point's normalized device coordinates, up positive; <c>[-1, 1]²</c> inside the image and
    /// beyond it outside; zero when the projection fails.</param>
    /// <param name="depth">The point's distance along <see cref="Forward"/> from <see cref="Position"/>, in world
    /// units; computed whether or not the projection succeeds.</param>
    /// <returns><see langword="true"/> when the point lies ahead of the camera (a positive <paramref name="depth"/>);
    /// <see langword="false"/> for a point at or behind the eye, which has no image point.</returns>
    public bool TryProject(Vector3 world, out Vector2 ndc, out float depth) {
        var delta = (world - Position);

        depth = Vector3.Dot(
            vector1: delta,
            vector2: Forward
        );

        if (!(depth > 0f)) {
            ndc = Vector2.Zero;

            return false;
        }

        ndc = new Vector2(
            x: ((Vector3.Dot(
                vector1: delta,
                vector2: Right
            ) - (depth * FrustumOffset.X)) / ((depth * TanHalfFieldOfView) * AspectRatio)),
            y: ((Vector3.Dot(
                vector1: delta,
                vector2: Up
            ) - (depth * FrustumOffset.Y)) / (depth * TanHalfFieldOfView))
        );

        return true;
    }

    /// <summary>Gets the narrowest field of view a camera is built with, in radians. Half of it is a normal float, so
    /// its tangent, the snapshot's <see cref="TanHalfFieldOfView"/>, is a positive normal float that no device flushes
    /// to zero; a narrower angle lets <see cref="MathF.Tan"/> of its half underflow to zero, which no snapshot admits.</summary>
    public const float MinFieldOfViewRadians = 1e-6f;

    /// <summary>Creates a finite camera snapshot looking from <paramref name="position"/> toward <paramref name="target"/>.</summary>
    /// <exception cref="ArgumentException">A position or target component is not finite.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The field of view is below <see cref="MinFieldOfViewRadians"/> or not
    /// below <see cref="MathF.PI"/>, or a viewport dimension is zero.</exception>
    public static CameraSnapshot LookAt(Vector3 position, Vector3 target, float fieldOfViewRadians, float viewportWidth, float viewportHeight) {
        ValidateFinite(
            value: position,
            paramName: nameof(position)
        );
        ValidateFinite(
            value: target,
            paramName: nameof(target)
        );

        if (
            !float.IsFinite(f: fieldOfViewRadians) ||
            (fieldOfViewRadians < MinFieldOfViewRadians) ||
            (fieldOfViewRadians >= MathF.PI)
        ) {
            throw new ArgumentOutOfRangeException(
                nameof(fieldOfViewRadians),
                fieldOfViewRadians,
                $"The field of view must be finite, at least {MinFieldOfViewRadians} and below pi radians."
            );
        }
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value: viewportWidth);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value: viewportHeight);

        var forward = SafeNormalize(
            fallback: -Vector3.UnitZ,
            value: (target - position)
        );
        var right = SafeNormalize(
            fallback: Vector3.UnitX,
            value: Vector3.Cross(
                vector1: forward,
                vector2: Vector3.UnitY
            )
        );
        var up = Vector3.Cross(
            vector1: right,
            vector2: forward
        );

        return new CameraSnapshot(
            Position: position,
            Right: right,
            Up: up,
            Forward: forward,
            TanHalfFieldOfView: MathF.Tan(x: (fieldOfViewRadians * 0.5f)),
            AspectRatio: (viewportWidth / viewportHeight)
        );
    }
}
