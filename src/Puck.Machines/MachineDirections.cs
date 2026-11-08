using Puck.Abstractions.Machines;

namespace Puck.Machines;

/// <summary>The digital directions a neutral <see cref="MachinePadState"/> holds: the left stick past
/// <see cref="MachineInputThresholds.StickDirection"/> or an explicit d-pad bit. A console d-pad is one rocker that
/// cannot press opposite directions together, so opposites resolve here once for every machine: up wins over down
/// and right wins over left. Each machine packs the result into its own button image.</summary>
/// <param name="Up">Whether up is held.</param>
/// <param name="Down">Whether down is held; never set together with <paramref name="Up"/>.</param>
/// <param name="Left">Whether left is held; never set together with <paramref name="Right"/>.</param>
/// <param name="Right">Whether right is held.</param>
public readonly record struct MachineDirections(bool Up, bool Down, bool Left, bool Right) {
    /// <summary>Quantizes a pad image's left stick and d-pad bits to digital directions.</summary>
    /// <param name="pad">The neutral controller image.</param>
    /// <returns>The directions held, with opposites resolved to up and right.</returns>
    public static MachineDirections From(in MachinePadState pad) {
        var buttons = pad.Buttons;
        var up = ((pad.LeftStick.Y >= MachineInputThresholds.StickDirection) || buttons.HasFlag(flag: MachineButtons.DpadUp));
        var down = (
            !up &&
            ((pad.LeftStick.Y <= -MachineInputThresholds.StickDirection) || buttons.HasFlag(flag: MachineButtons.DpadDown))
        );
        var right = ((pad.LeftStick.X >= MachineInputThresholds.StickDirection) || buttons.HasFlag(flag: MachineButtons.DpadRight));
        var left = (
            !right &&
            ((pad.LeftStick.X <= -MachineInputThresholds.StickDirection) || buttons.HasFlag(flag: MachineButtons.DpadLeft))
        );

        return new MachineDirections(
            Up: up,
            Down: down,
            Left: left,
            Right: right
        );
    }
}
