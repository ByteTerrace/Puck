using System.Numerics;

namespace Puck.Abstractions.Machines;

/// <summary>
/// A validated normalized standard-controller image for one <see cref="IScreenMachine.Step"/>. Stick and tilt axes are
/// finite values in -1..1 and triggers are finite values in 0..1. The default value is the neutral image.
/// </summary>
public readonly record struct MachinePadState {
    /// <summary>Initializes a normalized pad image.</summary>
    public MachinePadState(
        MachineButtons Buttons,
        Vector2 LeftStick,
        Vector2 RightStick,
        float LeftTrigger,
        float RightTrigger,
        Vector2 Tilt = default,
        byte LightLevel = 0
    ) {
        ValidateAxes(value: LeftStick, paramName: nameof(LeftStick));
        ValidateAxes(value: RightStick, paramName: nameof(RightStick));
        ValidateUnit(value: LeftTrigger, paramName: nameof(LeftTrigger));
        ValidateUnit(value: RightTrigger, paramName: nameof(RightTrigger));
        ValidateAxes(value: Tilt, paramName: nameof(Tilt));

        this.Buttons = Buttons;
        this.LeftStick = LeftStick;
        this.RightStick = RightStick;
        this.LeftTrigger = LeftTrigger;
        this.RightTrigger = RightTrigger;
        this.Tilt = Tilt;
        this.LightLevel = LightLevel;
    }

    /// <summary>Gets the digital buttons held this frame.</summary>
    public MachineButtons Buttons { get; init; }
    /// <summary>Gets the left stick, normalized per axis to -1..1.</summary>
    public Vector2 LeftStick { get; }
    /// <summary>Gets the right stick, normalized per axis to -1..1.</summary>
    public Vector2 RightStick { get; }
    /// <summary>Gets the left trigger in 0..1.</summary>
    public float LeftTrigger { get; }
    /// <summary>Gets the right trigger in 0..1.</summary>
    public float RightTrigger { get; }
    /// <summary>Gets the recorded tilt sample, normalized per axis to -1..1.</summary>
    public Vector2 Tilt { get; }
    /// <summary>Gets the ambient-light sample, from 0 (darkest) to 255 (brightest).</summary>
    public byte LightLevel { get; }

    /// <summary>Gets a neutral image with no active input.</summary>
    public static MachinePadState Neutral => default;

    /// <summary>Merges two validated pad images, clamping summed analog channels to their normalized domains.</summary>
    public static MachinePadState Merge(in MachinePadState first, in MachinePadState second) => new(
        Buttons: first.Buttons | second.Buttons,
        LeftStick: ClampAxes(first.LeftStick + second.LeftStick),
        RightStick: ClampAxes(first.RightStick + second.RightStick),
        LeftTrigger: Math.Clamp(first.LeftTrigger + second.LeftTrigger, 0f, 1f),
        RightTrigger: Math.Clamp(first.RightTrigger + second.RightTrigger, 0f, 1f),
        Tilt: ClampAxes(first.Tilt + second.Tilt),
        LightLevel: Math.Max(first.LightLevel, second.LightLevel)
    );

    private static Vector2 ClampAxes(Vector2 value) => new(
        Math.Clamp(value.X, -1f, 1f),
        Math.Clamp(value.Y, -1f, 1f)
    );

    private static void ValidateAxes(Vector2 value, string paramName) {
        if (!float.IsFinite(value.X) || !float.IsFinite(value.Y) || (value.X < -1f) || (value.X > 1f) || (value.Y < -1f) || (value.Y > 1f)) {
            throw new ArgumentOutOfRangeException(paramName, value, "Each axis must be finite and in the inclusive range -1..1.");
        }
    }

    private static void ValidateUnit(float value, string paramName) {
        if (!float.IsFinite(value) || (value < 0f) || (value > 1f)) {
            throw new ArgumentOutOfRangeException(paramName, value, "The value must be finite and in the inclusive range 0..1.");
        }
    }
}
