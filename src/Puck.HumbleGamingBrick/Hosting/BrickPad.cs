using Puck.Abstractions.Machines;

namespace Puck.HumbleGamingBrick;

/// <summary>Folds a neutral <see cref="MachinePadState"/> down to the SM83 brick's own <see cref="JoypadButtons"/> image
/// — the engine-internal adapter between the host's standard-controller vocabulary and the eight console buttons. The
/// brick's d-pad is digital, so the left stick quantizes through a threshold; any explicit d-pad bits on the pad image
/// pass through as well. The face/system buttons map by position: South→A, East→B, Start→Start, Back→Select. The right
/// stick and triggers have no console equivalent and are ignored.</summary>
internal static class BrickPad {
    /// <summary>Maps a normalized pad image to the brick's joypad image.</summary>
    /// <param name="pad">The neutral controller image.</param>
    /// <returns>The console button image the brick holds.</returns>
    public static JoypadButtons ToJoypad(in MachinePadState pad) {
        var buttons = JoypadButtons.None;

        // Directions: the left stick quantized to the d-pad, plus any explicit d-pad bits already on the image.
        if (
            (pad.LeftStick.Y >= MachineInputThresholds.StickDirection) ||
            pad.Buttons.HasFlag(flag: MachineButtons.DpadUp)
        ) {
            buttons |= JoypadButtons.Up;
        } else if (
            (pad.LeftStick.Y <= -MachineInputThresholds.StickDirection) ||
            pad.Buttons.HasFlag(flag: MachineButtons.DpadDown)
        ) {
            buttons |= JoypadButtons.Down;
        }

        if (
            (pad.LeftStick.X >= MachineInputThresholds.StickDirection) ||
            pad.Buttons.HasFlag(flag: MachineButtons.DpadRight)
        ) {
            buttons |= JoypadButtons.Right;
        } else if (
            (pad.LeftStick.X <= -MachineInputThresholds.StickDirection) ||
            pad.Buttons.HasFlag(flag: MachineButtons.DpadLeft)
        ) {
            buttons |= JoypadButtons.Left;
        }

        // Actions and system buttons by position.
        if (pad.Buttons.HasFlag(flag: MachineButtons.South)) {
            buttons |= JoypadButtons.A;
        }

        if (pad.Buttons.HasFlag(flag: MachineButtons.East)) {
            buttons |= JoypadButtons.B;
        }

        if (pad.Buttons.HasFlag(flag: MachineButtons.Start)) {
            buttons |= JoypadButtons.Start;
        }

        if (pad.Buttons.HasFlag(flag: MachineButtons.Back)) {
            buttons |= JoypadButtons.Select;
        }

        return buttons;
    }
}
