using Puck.Abstractions.Machines;

namespace Puck.Machines;

/// <summary>The four action and system buttons every console in the family shares, read from a neutral
/// <see cref="MachinePadState"/> by position: the bottom face button is A, the right one B, Back is Select and Start is
/// Start. The shoulders, the right stick and the triggers have no place here, because not every console has them. Each
/// machine packs the result, beside <see cref="MachineDirections"/>, into its own button image.</summary>
/// <param name="A">Whether the bottom face button is held.</param>
/// <param name="B">Whether the right face button is held.</param>
/// <param name="Select">Whether Back is held.</param>
/// <param name="Start">Whether Start is held.</param>
public readonly record struct MachineActionButtons(bool A, bool B, bool Select, bool Start) {
    /// <summary>Reads the action buttons from a pad image.</summary>
    /// <param name="pad">The neutral controller image.</param>
    /// <returns>The action buttons held.</returns>
    public static MachineActionButtons From(in MachinePadState pad) {
        var buttons = pad.Buttons;

        return new MachineActionButtons(
            A: buttons.HasFlag(flag: MachineButtons.South),
            B: buttons.HasFlag(flag: MachineButtons.East),
            Select: buttons.HasFlag(flag: MachineButtons.Back),
            Start: buttons.HasFlag(flag: MachineButtons.Start)
        );
    }
}
