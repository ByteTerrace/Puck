using System.Numerics;

namespace Puck.Abstractions.Cameras;

/// <summary>An immutable, finite camera basis and projection snapshot derived by <see cref="LookAt"/>.</summary>
public readonly record struct CameraSnapshot {
    /// <summary>Initializes a finite camera snapshot from an already-derived basis and projection.</summary>
    public CameraSnapshot(Vector3 Position, Vector3 Right, Vector3 Up, Vector3 Forward, float TanHalfFieldOfView, float AspectRatio) {
        ValidateFinite(Position, nameof(Position));
        ValidateFinite(Right, nameof(Right));
        ValidateFinite(Up, nameof(Up));
        ValidateFinite(Forward, nameof(Forward));
        ValidateBasis(Right, nameof(Right));
        ValidateBasis(Up, nameof(Up));
        ValidateBasis(Forward, nameof(Forward));

        if ((MathF.Abs(Vector3.Dot(Right, Up)) > 1e-3f) ||
            (MathF.Abs(Vector3.Dot(Right, Forward)) > 1e-3f) ||
            (MathF.Abs(Vector3.Dot(Up, Forward)) > 1e-3f)) {
            throw new ArgumentException(message: "The camera basis vectors must be mutually perpendicular.");
        }

        if (!float.IsFinite(TanHalfFieldOfView) || (TanHalfFieldOfView <= 0f)) {
            throw new ArgumentOutOfRangeException(nameof(TanHalfFieldOfView));
        }
        if (!float.IsFinite(AspectRatio) || (AspectRatio <= 0f)) {
            throw new ArgumentOutOfRangeException(nameof(AspectRatio));
        }

        this.Position = Position;
        this.Right = Right;
        this.Up = Up;
        this.Forward = Forward;
        this.TanHalfFieldOfView = TanHalfFieldOfView;
        this.AspectRatio = AspectRatio;
    }

    /// <summary>Gets the world-space camera position.</summary>
    public Vector3 Position { get; }
    /// <summary>Gets the normalized camera-right basis vector.</summary>
    public Vector3 Right { get; }
    /// <summary>Gets the normalized camera-up basis vector.</summary>
    public Vector3 Up { get; }
    /// <summary>Gets the normalized camera-forward basis vector.</summary>
    public Vector3 Forward { get; }
    /// <summary>Gets the tangent of half the vertical field of view.</summary>
    public float TanHalfFieldOfView { get; }
    /// <summary>Gets the viewport width-to-height ratio.</summary>
    public float AspectRatio { get; }

    /// <summary>Creates a finite camera snapshot looking from <paramref name="position"/> toward <paramref name="target"/>.</summary>
    /// <exception cref="ArgumentException">A position or target component is not finite.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The field of view is outside 0..<see cref="MathF.PI"/>, or a viewport dimension is zero.</exception>
    public static CameraSnapshot LookAt(Vector3 position, Vector3 target, float fieldOfViewRadians, uint viewportWidth, uint viewportHeight) {
        ValidateFinite(value: position, paramName: nameof(position));
        ValidateFinite(value: target, paramName: nameof(target));

        if (!float.IsFinite(fieldOfViewRadians) || (fieldOfViewRadians <= 0f) || (fieldOfViewRadians >= MathF.PI)) {
            throw new ArgumentOutOfRangeException(nameof(fieldOfViewRadians), fieldOfViewRadians, "The field of view must be finite and strictly between zero and pi radians.");
        }
        ArgumentOutOfRangeException.ThrowIfZero(value: viewportWidth);
        ArgumentOutOfRangeException.ThrowIfZero(value: viewportHeight);

        var forward = SafeNormalize(fallback: -Vector3.UnitZ, value: target - position);
        var right = SafeNormalize(fallback: Vector3.UnitX, value: Vector3.Cross(forward, Vector3.UnitY));
        var up = Vector3.Cross(right, forward);

        return new CameraSnapshot(
            Position: position,
            Right: right,
            Up: up,
            Forward: forward,
            TanHalfFieldOfView: MathF.Tan(fieldOfViewRadians * 0.5f),
            AspectRatio: viewportWidth / (float)viewportHeight
        );
    }

    private static Vector3 SafeNormalize(Vector3 value, Vector3 fallback) {
        var length = value.Length();
        return ((length > 1e-5f) ? (value / length) : fallback);
    }

    private static void ValidateFinite(Vector3 value, string paramName) {
        if (!float.IsFinite(value.X) || !float.IsFinite(value.Y) || !float.IsFinite(value.Z)) {
            throw new ArgumentException(message: "All vector components must be finite.", paramName: paramName);
        }
    }

    private static void ValidateBasis(Vector3 value, string paramName) {
        if (MathF.Abs(value.LengthSquared() - 1f) > 1e-3f) {
            throw new ArgumentException(message: "Camera basis vectors must be normalized.", paramName: paramName);
        }
    }
}
