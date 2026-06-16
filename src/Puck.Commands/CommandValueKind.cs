namespace Puck.Commands;

/// <summary>
/// Specifies the shape of the value a command carries and, by extension, how consumers interpret it.
/// </summary>
/// <remarks>
/// The kind is a property of the value rather than of the producer. This lets a single command be
/// driven as either a discrete action or a continuous, per-frame control without the consumer
/// needing to know where the value originated.
/// </remarks>
public enum CommandValueKind {
    /// <summary>A boolean state (0 or 1), such as a press/release action like <c>jump</c> or <c>exit</c>.</summary>
    Digital = 0,

    /// <summary>A single scalar, conventionally in the range -1 to 1.</summary>
    Axis1D,

    /// <summary>
    /// A two-component vector, conventionally -1 to 1 per component (for example, movement) or a raw
    /// delta (for example, a look offset).
    /// </summary>
    Axis2D,

    /// <summary>
    /// A three-component vector for motion-sensor input, such as gyroscope angular velocity (radians
    /// per second) or accelerometer reading (g).
    /// </summary>
    Axis3D,

    /// <summary>
    /// A fused absolute orientation expressed as a unit quaternion, used for point-at-screen or
    /// flick-stick style aiming.
    /// </summary>
    Orientation,
}
