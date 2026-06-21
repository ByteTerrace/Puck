using System.Numerics;

namespace Puck.Input.Devices;

/// <summary>
/// One finger tracked by a controller touchpad (the DualSense reports two). <see cref="Position"/> is
/// normalized to 0..1 per component with the origin at the top-left of the pad (X grows right, Y grows down),
/// the touch-surface convention. When <see cref="IsActive"/> is <see langword="false"/> no finger occupies the
/// slot and the other fields carry no meaning.
/// </summary>
/// <param name="IsActive">Whether a finger is currently touching this slot.</param>
/// <param name="Id">The contact id the controller increments for each new touch, for tracking continuity across frames.</param>
/// <param name="Position">The normalized contact position, 0..1 per component, origin top-left.</param>
public readonly record struct GamepadTouchPoint(bool IsActive, byte Id, Vector2 Position);
