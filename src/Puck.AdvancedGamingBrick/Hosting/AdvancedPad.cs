using Puck.Abstractions.Machines;

namespace Puck.AdvancedGamingBrick;

/// <summary>Folds a neutral controller image into the Advanced GamingBrick's active-low KEYINPUT register.</summary>
internal static class AdvancedPad {
    /// <summary>Applies one recorded pad image to a machine's input devices: the KEYINPUT image plus the recorded solar
    /// light level and tilt sample. The authoritative core and its lookahead fork drive this one seam, so the predicted
    /// branch applies the SAME full input image the authority does; the sensor writes are no-ops on a cartridge with no
    /// matching sensor.</summary>
    /// <param name="pad">The neutral controller image.</param>
    /// <param name="machine">The machine whose KEYINPUT register the pad drives.</param>
    /// <param name="cartridge">The cartridge carrying the sensor channels.</param>
    public static void Apply(in MachinePadState pad, AdvancedGamingBrickMachine machine, AgbCartridge cartridge) {
        machine.SetKeyInput(keys: ToKeyInput(pad: in pad));
        cartridge.SetLightLevel(level: pad.LightLevel);
        cartridge.SetTilt(
            x: pad.Tilt.X,
            y: pad.Tilt.Y
        );
    }
    /// <summary>Maps the supported face, system, shoulder, d-pad, and left-stick channels to KEYINPUT.</summary>
    public static ushort ToKeyInput(in MachinePadState pad) {
        var keys = 0x03FF;
        var buttons = pad.Buttons;

        keys = Press(
            keys: keys,
            pressed: buttons.HasFlag(flag: MachineButtons.South),
            bit: 0
        ); // A
        keys = Press(
            keys: keys,
            pressed: buttons.HasFlag(flag: MachineButtons.East),
            bit: 1
        ); // B
        keys = Press(
            keys: keys,
            pressed: buttons.HasFlag(flag: MachineButtons.Back),
            bit: 2
        ); // Select
        keys = Press(
            keys: keys,
            pressed: buttons.HasFlag(flag: MachineButtons.Start),
            bit: 3
        );
        keys = Press(
            keys: keys,
            pressed: (buttons.HasFlag(flag: MachineButtons.DpadRight) || (pad.LeftStick.X >= MachineInputThresholds.StickDirection)),
            bit: 4
        );
        keys = Press(
            keys: keys,
            pressed: (buttons.HasFlag(flag: MachineButtons.DpadLeft) || (pad.LeftStick.X <= -MachineInputThresholds.StickDirection)),
            bit: 5
        );
        keys = Press(
            keys: keys,
            pressed: (buttons.HasFlag(flag: MachineButtons.DpadUp) || (pad.LeftStick.Y >= MachineInputThresholds.StickDirection)),
            bit: 6
        );
        keys = Press(
            keys: keys,
            pressed: (buttons.HasFlag(flag: MachineButtons.DpadDown) || (pad.LeftStick.Y <= -MachineInputThresholds.StickDirection)),
            bit: 7
        );
        keys = Press(
            keys: keys,
            pressed: buttons.HasFlag(flag: MachineButtons.RightShoulder),
            bit: 8
        );
        keys = Press(
            keys: keys,
            pressed: buttons.HasFlag(flag: MachineButtons.LeftShoulder),
            bit: 9
        );

        return ((ushort)keys);
    }

    private static int Press(int keys, bool pressed, int bit) => (pressed
        ? keys & ~(1 << bit)
        : keys);
}
