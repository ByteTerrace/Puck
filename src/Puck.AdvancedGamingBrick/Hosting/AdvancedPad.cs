using Puck.Abstractions.Machines;

namespace Puck.AdvancedGamingBrick;

/// <summary>Folds a neutral controller image into the Advanced GamingBrick's active-low KEYINPUT register.</summary>
internal static class AdvancedPad {
    private const float StickThreshold = 0.5f;

    /// <summary>Maps the supported face, system, shoulder, d-pad, and left-stick channels to KEYINPUT.</summary>
    public static ushort ToKeyInput(in MachinePadState pad) {
        var keys = 0x03FF;
        var buttons = pad.Buttons;

        keys = Press(keys: keys, pressed: buttons.HasFlag(flag: MachineButtons.South), bit: 0); // A
        keys = Press(keys: keys, pressed: buttons.HasFlag(flag: MachineButtons.East), bit: 1); // B
        keys = Press(keys: keys, pressed: buttons.HasFlag(flag: MachineButtons.Back), bit: 2); // Select
        keys = Press(keys: keys, pressed: buttons.HasFlag(flag: MachineButtons.Start), bit: 3);
        keys = Press(keys: keys, pressed: buttons.HasFlag(flag: MachineButtons.DpadRight) || (pad.LeftStick.X >= StickThreshold), bit: 4);
        keys = Press(keys: keys, pressed: buttons.HasFlag(flag: MachineButtons.DpadLeft) || (pad.LeftStick.X <= -StickThreshold), bit: 5);
        keys = Press(keys: keys, pressed: buttons.HasFlag(flag: MachineButtons.DpadUp) || (pad.LeftStick.Y >= StickThreshold), bit: 6);
        keys = Press(keys: keys, pressed: buttons.HasFlag(flag: MachineButtons.DpadDown) || (pad.LeftStick.Y <= -StickThreshold), bit: 7);
        keys = Press(keys: keys, pressed: buttons.HasFlag(flag: MachineButtons.RightShoulder), bit: 8);
        keys = Press(keys: keys, pressed: buttons.HasFlag(flag: MachineButtons.LeftShoulder), bit: 9);

        return (ushort)keys;
    }

    private static int Press(int keys, bool pressed, int bit) => (pressed ? (keys & ~(1 << bit)) : keys);
}
