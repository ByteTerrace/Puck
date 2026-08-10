using System.Numerics;

namespace Puck.SdfVm.Debug;

/// <summary>
/// One frame of neutral orbit-camera input (device-agnostic): a host adapts its own controller or keyboard state to
/// this record before driving <see cref="SdfDebugController.Advance"/> or <see cref="SdfDebugMode.AdvanceInput"/>.
/// This indirection keeps <c>Puck.SdfVm</c> free of a direct <c>Puck.Input</c> project reference.
/// </summary>
/// <param name="LeftStick">Orbit yaw/pitch (X = yaw rate, Y = pitch rate).</param>
/// <param name="RightStick">Pan (camera-relative planar axes).</param>
/// <param name="LeftTrigger">Zooms in exponentially at full deflection.</param>
/// <param name="RightTrigger">Zooms out exponentially at full deflection.</param>
/// <param name="ExitButton">Edge-tracked inside the controller — a press exits the mode.</param>
/// <param name="CarveButton">Edge-tracked inside the controller — a press fires a one-shot carve (guarded by <see cref="CarveGuardButton"/>).</param>
/// <param name="CarveGuardButton">The held guard for the carve chord (so an orbiting stick / a lone <see cref="CarveButton"/> press never fires a destructive carve).</param>
public readonly record struct SdfOrbitInput(
    Vector2 LeftStick,
    Vector2 RightStick,
    float LeftTrigger,
    float RightTrigger,
    bool ExitButton,
    bool CarveButton,
    bool CarveGuardButton
);
