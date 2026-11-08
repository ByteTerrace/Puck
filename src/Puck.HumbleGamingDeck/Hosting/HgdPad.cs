using Puck.Abstractions.Machines;

namespace Puck.HumbleGamingDeck;

/// <summary>Folds a neutral <see cref="MachinePadState"/> to a standard controller's buttons, with the same positional
/// mapping the bricks use: the bottom face button is A, the right one B, Back is Select, and the d-pad or the left stick
/// past <see cref="MachineInputThresholds.StickDirection"/> is the control pad.</summary>
public static class HgdPad {
    /// <summary>Returns the buttons a pad image holds.</summary>
    /// <param name="pad">The neutral pad image.</param>
    /// <returns>The standard controller's buttons.</returns>
    public static HgdButtons ToButtons(in MachinePadState pad) {
        var buttons = HgdButtons.None;

        var directions = MachineDirections.From(pad: in pad);

        if (directions.Up) {
            buttons |= HgdButtons.Up;
        }
        if (directions.Down) {
            buttons |= HgdButtons.Down;
        }
        if (directions.Left) {
            buttons |= HgdButtons.Left;
        }
        if (directions.Right) {
            buttons |= HgdButtons.Right;
        }
        if (pad.Buttons.HasFlag(flag: MachineButtons.South)) {
            buttons |= HgdButtons.A;
        }
        if (pad.Buttons.HasFlag(flag: MachineButtons.East)) {
            buttons |= HgdButtons.B;
        }
        if (pad.Buttons.HasFlag(flag: MachineButtons.Start)) {
            buttons |= HgdButtons.Start;
        }
        if (pad.Buttons.HasFlag(flag: MachineButtons.Back)) {
            buttons |= HgdButtons.Select;
        }

        return buttons;
    }
    /// <summary>Loads both controller ports from the first two seats of a seat image.</summary>
    /// <param name="pads">The seat image; seat 0 is the $4016 controller and seat 1 the $4017 one.</param>
    /// <param name="controllers">The controller ports to load.</param>
    public static void Apply(in MachinePads pads, HgdControllers controllers) {
        controllers.SetButtons(
            buttons: ToButtons(pad: in pads[0]),
            port: 0
        );
        controllers.SetButtons(
            buttons: ToButtons(pad: in pads[1]),
            port: 1
        );
    }
}
