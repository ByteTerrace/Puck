using System.Numerics;

namespace Puck.Abstractions.Machines;

/// <summary>
/// A normalized standard-controller image for one <see cref="IScreenMachine.Step"/> — the neutral input a host hands a
/// machine every frame, independent of the physical source (a live pad, a network stream, an engaged player's translated
/// intent, or a scripted test). Sticks are -1..1 per component (left stick <c>Y</c> = forward / up, <c>X</c> = right),
/// triggers 0..1, and <see cref="Buttons"/> a <see cref="MachineButtons"/> mask. A machine consumes only the channels it
/// understands: an SM83-class brick reads the left stick and the face/system buttons; an N64/GameCube-class machine reads
/// both sticks and triggers too.
/// </summary>
/// <param name="Buttons">The digital buttons held this frame.</param>
/// <param name="LeftStick">The left analog stick, -1..1 per component (<c>Y</c> = forward/up, <c>X</c> = right).</param>
/// <param name="RightStick">The right analog stick, -1..1 per component (the camera / C-stick channel).</param>
/// <param name="LeftTrigger">The left trigger, 0..1.</param>
/// <param name="RightTrigger">The right trigger, 0..1.</param>
/// <param name="Tilt">The recorded tilt/gyro sample, -1..1 per component, centered at the origin — the deterministic
/// per-segment channel a cartridge's accelerometer/gyro sensor (SM83 MBC7, the AGB address-mapped tilt device) reads.
/// A machine with no tilt hardware ignores it entirely.</param>
/// <param name="LightLevel">The recorded ambient-light sample, 0 (darkest) to 255 (brightest) — the deterministic
/// per-segment channel a cartridge's solar sensor (the AGB GPIO light device) reads. Zero (the neutral default) reads
/// as darkness, matching the sensor hardware's own reset state; a machine with no solar hardware ignores it.</param>
public readonly record struct MachinePadState(
    MachineButtons Buttons,
    Vector2 LeftStick,
    Vector2 RightStick,
    float LeftTrigger,
    float RightTrigger,
    Vector2 Tilt = default,
    byte LightLevel = 0
) {
    /// <summary>A neutral image: no buttons, centered sticks, released triggers, motionless tilt, darkest light — the
    /// input for a frame with no signal.</summary>
    public static MachinePadState Neutral => default;

    /// <summary>Merges two pad images into one — the multi-driver shape (several engaged players on one machine): buttons
    /// OR together, stick and tilt axes sum and clamp per component to -1..1, triggers sum and clamp to 0..1, and the
    /// light level takes the brighter of the two (a single physical quantity, not a per-player sum — the "darkest
    /// default" invariant still holds, since darkness is the additive identity for a max). Merging any pad with
    /// <see cref="Neutral"/> returns the other unchanged.</summary>
    /// <param name="first">The first image.</param>
    /// <param name="second">The second image.</param>
    /// <returns>The merged image.</returns>
    public static MachinePadState Merge(in MachinePadState first, in MachinePadState second) {
        return new MachinePadState(
            Buttons: first.Buttons | second.Buttons,
            LeftStick: ClampStick(stick: (first.LeftStick + second.LeftStick)),
            RightStick: ClampStick(stick: (first.RightStick + second.RightStick)),
            LeftTrigger: Math.Clamp(value: (first.LeftTrigger + second.LeftTrigger), min: 0f, max: 1f),
            RightTrigger: Math.Clamp(value: (first.RightTrigger + second.RightTrigger), min: 0f, max: 1f),
            Tilt: ClampStick(stick: (first.Tilt + second.Tilt)),
            LightLevel: Math.Max(val1: first.LightLevel, val2: second.LightLevel)
        );
    }

    private static Vector2 ClampStick(Vector2 stick) {
        return new Vector2(
            x: Math.Clamp(value: stick.X, min: -1f, max: 1f),
            y: Math.Clamp(value: stick.Y, min: -1f, max: 1f)
        );
    }
}
