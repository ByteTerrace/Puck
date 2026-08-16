using System.Numerics;

namespace Puck.Abstractions.Cameras;

/// <summary>An immutable, finite camera basis and projection snapshot derived by <see cref="LookAt"/>.</summary>
public readonly record struct CameraSnapshot {
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
        if (
            !float.IsFinite(f: value.X) ||
            !float.IsFinite(f: value.Y) ||
            !float.IsFinite(f: value.Z)
        ) {
            throw new ArgumentException(
                message: "All vector components must be finite.",
                paramName: paramName
            );
        }
    }

    /// <summary>Creates a finite camera snapshot looking from <paramref name="position"/> toward <paramref name="target"/>.</summary>
    /// <exception cref="ArgumentException">A position or target component is not finite.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The field of view is outside 0..<see cref="MathF.PI"/>, or a viewport dimension is zero.</exception>
    public static CameraSnapshot LookAt(Vector3 position, Vector3 target, float fieldOfViewRadians, uint viewportWidth, uint viewportHeight) {
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
            (fieldOfViewRadians <= 0f) ||
            (fieldOfViewRadians >= MathF.PI)
        ) {
            throw new ArgumentOutOfRangeException(
                nameof(fieldOfViewRadians),
                fieldOfViewRadians,
                "The field of view must be finite and strictly between zero and pi radians."
            );
        }
        ArgumentOutOfRangeException.ThrowIfZero(value: viewportWidth);
        ArgumentOutOfRangeException.ThrowIfZero(value: viewportHeight);

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
            AspectRatio: (viewportWidth / ((float)viewportHeight))
        );
    }
}
