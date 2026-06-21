using System.Numerics;
using Puck.Commands;

namespace Puck.Input.Devices;

/// <summary>
/// One device's coalesced contribution for a single frame, as returned by the manager's per-frame drain.
/// </summary>
/// <param name="DeviceId">The device the snapshot belongs to.</param>
/// <param name="Latest">The most recent normalized state (sticks/triggers/buttons-held).</param>
/// <param name="Pressed">Buttons that transitioned to pressed since the previous frame.</param>
/// <param name="Released">Buttons that transitioned to released since the previous frame.</param>
/// <param name="Gyro">The mean gyro angular velocity (radians/second) over the reports seen since the previous frame.</param>
public readonly record struct GamepadDrain(
    InputDeviceId DeviceId,
    GamepadState Latest,
    GamepadButtons Pressed,
    GamepadButtons Released,
    Vector3 Gyro
);
