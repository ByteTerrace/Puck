using System.Numerics;

namespace Puck.Input.Devices;

/// <summary>
/// A normalized snapshot of a controller for a single poll, independent of the physical device. Axes are
/// normalized: sticks to the range -1..1 per component (deadzone applied), triggers to 0..1. <see cref="Gyro"/>
/// is angular velocity in radians per second (zero on devices without a motion sensor), and
/// <see cref="Orientation"/> is the fused absolute pose (identity until a fusion step populates it).
/// </summary>
/// <param name="Buttons">The set of digital buttons currently held.</param>
/// <param name="LeftStick">The left analog stick, -1..1 per component.</param>
/// <param name="RightStick">The right analog stick, -1..1 per component.</param>
/// <param name="LeftTrigger">The left trigger, 0..1.</param>
/// <param name="RightTrigger">The right trigger, 0..1.</param>
/// <param name="Gyro">The gyroscope angular velocity, in radians per second.</param>
/// <param name="Orientation">The fused absolute orientation, as a unit quaternion.</param>
/// <param name="Touch0">The first touchpad contact (inactive on devices without a touchpad).</param>
/// <param name="Touch1">The second touchpad contact, for multitouch (inactive on devices without a touchpad).</param>
/// <param name="Accelerometer">The accelerometer specific force, in g (zero on devices without one); at rest it reads gravity, so it doubles as a tilt sensor.</param>
/// <param name="SensorTimestamp">The device's own motion-sensor timestamp, in the device's native units (DualSense: 1/3 µs; zero on devices that report none). Carried as data for sub-frame rhythm timing; the parser sets it.</param>
/// <param name="ArrivalTicks">The engine-tick instant the report arrived, stamped on the device I/O thread right after parsing (zero until stamped). The authority for attributing input to a fixed-step tick at sub-frame precision.</param>
/// <param name="SequenceNumber">A per-device monotonic report counter, stamped on the I/O thread (zero until stamped); orders reports a coalesced drain folded together.</param>
public readonly record struct GamepadState(
    GamepadButtons Buttons,
    Vector2 LeftStick,
    Vector2 RightStick,
    float LeftTrigger,
    float RightTrigger,
    Vector3 Gyro,
    Quaternion Orientation,
    GamepadTouchPoint Touch0 = default,
    GamepadTouchPoint Touch1 = default,
    Vector3 Accelerometer = default,
    uint SensorTimestamp = 0u,
    ulong ArrivalTicks = 0UL,
    ulong SequenceNumber = 0UL
) {
    /// <summary>A neutral state: no buttons, centered sticks, released triggers, no motion, identity pose.</summary>
    public static GamepadState Neutral => new(
        Buttons: GamepadButtons.None,
        Gyro: Vector3.Zero,
        LeftStick: Vector2.Zero,
        LeftTrigger: 0f,
        Orientation: Quaternion.Identity,
        RightStick: Vector2.Zero,
        RightTrigger: 0f
    );
}
