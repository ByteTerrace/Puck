using System.Numerics;

namespace Puck.Commands;

/// <summary>
/// Represents the value a command carries for a single frame, tagged with the
/// <see cref="CommandValueKind"/> that determines how it is interpreted.
/// </summary>
/// <remarks>
/// All value shapes are packed into a single <see cref="Vector4"/>:
/// <list type="bullet">
/// <item><description><see cref="CommandValueKind.Digital"/> and <see cref="CommandValueKind.Axis1D"/> use the <c>X</c> component.</description></item>
/// <item><description><see cref="CommandValueKind.Axis2D"/> uses <c>X</c> and <c>Y</c>.</description></item>
/// <item><description><see cref="CommandValueKind.Axis3D"/> uses <c>X</c>, <c>Y</c>, and <c>Z</c>.</description></item>
/// <item><description><see cref="CommandValueKind.Orientation"/> uses all four components as the quaternion <c>(X, Y, Z, W)</c>.</description></item>
/// </list>
/// A <see cref="Vector4"/> backing matches the natural SIMD width, so the value remains small and
/// cheap to copy regardless of its kind, and never allocates.
/// </remarks>
/// <param name="Kind">The shape that determines how <paramref name="Raw"/> is interpreted.</param>
/// <param name="Raw">The packed component data for the value.</param>
public readonly record struct CommandValue(CommandValueKind Kind, Vector4 Raw) {
    /// <summary>Creates an inactive (zero-valued) command value of the specified kind.</summary>
    /// <param name="kind">The kind to assign to the resulting value.</param>
    /// <returns>A value whose components are all zero.</returns>
    public static CommandValue Inactive(CommandValueKind kind) => new(
        Kind: kind,
        Raw: Vector4.Zero
    );

    /// <summary>Creates a <see cref="CommandValueKind.Digital"/> value.</summary>
    /// <param name="active"><see langword="true"/> for an active (1) state; otherwise an inactive (0) state.</param>
    /// <returns>A digital command value.</returns>
    public static CommandValue Digital(bool active) => new(
        Kind: CommandValueKind.Digital,
        Raw: new Vector4(
            x: (active
                ? 1f
                : 0f),
            y: 0f,
            z: 0f,
            w: 0f
        )
    );

    /// <summary>Creates an <see cref="CommandValueKind.Axis1D"/> value.</summary>
    /// <param name="value">The axis magnitude, conventionally in the range -1 to 1.</param>
    /// <returns>A one-dimensional axis command value.</returns>
    public static CommandValue Axis(float value) => new(
        Kind: CommandValueKind.Axis1D,
        Raw: new Vector4(
            x: value,
            y: 0f,
            z: 0f,
            w: 0f
        )
    );

    /// <summary>Creates an <see cref="CommandValueKind.Axis2D"/> value.</summary>
    /// <param name="value">The axis components, conventionally -1 to 1 per component, or a raw delta.</param>
    /// <returns>A two-dimensional axis command value.</returns>
    public static CommandValue Axis(Vector2 value) => new(
        Kind: CommandValueKind.Axis2D,
        Raw: new Vector4(
            value,
            z: 0f,
            w: 0f
        )
    );

    /// <summary>Creates an <see cref="CommandValueKind.Axis3D"/> value.</summary>
    /// <param name="value">The motion-sensor components, such as gyroscope angular velocity or accelerometer reading.</param>
    /// <returns>A three-dimensional axis command value.</returns>
    public static CommandValue Axis(Vector3 value) => new(
        Kind: CommandValueKind.Axis3D,
        Raw: new Vector4(
            value,
            w: 0f
        )
    );

    /// <summary>Creates an <see cref="CommandValueKind.Orientation"/> value.</summary>
    /// <param name="value">The absolute orientation, expected to be a unit quaternion.</param>
    /// <returns>An orientation command value.</returns>
    public static CommandValue Orientation(Quaternion value) => new(
        Kind: CommandValueKind.Orientation,
        Raw: new Vector4(
            x: value.X,
            y: value.Y,
            z: value.Z,
            w: value.W
        )
    );

    /// <summary>Gets a value indicating whether any component of <see cref="Raw"/> is non-zero.</summary>
    public bool IsActive => (Raw != Vector4.Zero);

    /// <summary>Gets the value interpreted as a digital state, which is active when the <c>X</c> component is non-zero.</summary>
    public bool AsDigital => (Raw.X != 0f);

    /// <summary>Gets the value interpreted as a one-dimensional axis.</summary>
    public float AsAxis1D => Raw.X;

    /// <summary>Gets the value interpreted as a two-dimensional axis.</summary>
    public Vector2 AsAxis2D => new(
        x: Raw.X,
        y: Raw.Y
    );

    /// <summary>Gets the value interpreted as a three-dimensional axis.</summary>
    public Vector3 AsAxis3D => new(
        x: Raw.X,
        y: Raw.Y,
        z: Raw.Z
    );

    /// <summary>Gets the value interpreted as an absolute orientation.</summary>
    public Quaternion AsOrientation => new(
        w: Raw.W,
        x: Raw.X,
        y: Raw.Y,
        z: Raw.Z
    );
}
