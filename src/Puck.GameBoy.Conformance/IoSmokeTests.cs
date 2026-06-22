namespace Puck.GameBoy.Conformance;

/// <summary>
/// Self-contained joypad and serial-port checks: the joypad's group select and active-low button reads plus its
/// press interrupt, and a serial transfer completing (shifting in ones with no partner), raising the serial
/// interrupt, and surfacing the transmitted byte.
/// </summary>
internal static class IoSmokeTests {
    private const byte SelectActions = 0x10;
    private const byte SelectDirections = 0x20;

    public static IReadOnlyList<(string Name, Func<string?> Run)> All =>
        [
            ("joypad reads 0xCF with no buttons pressed", static () => {
                var joypad = new Joypad(interrupts: new InterruptController());

                return (joypad.Read() == 0xCF)
                    ? null
                    : $"0x{joypad.Read():X2} (expected 0xCF)";
            }),
            ("a pressed direction reads low in the direction group", static () => {
                var joypad = new Joypad(interrupts: new InterruptController());

                joypad.Write(value: SelectDirections);
                joypad.SetButton(button: JoypadButton.Right, pressed: true);

                // 0xC0 | select(0x20) | (Right held -> bit 0 low -> 0x0E).
                return (joypad.Read() == 0xEE)
                    ? null
                    : $"0x{joypad.Read():X2} (expected 0xEE)";
            }),
            ("a pressed action reads low in the action group", static () => {
                var joypad = new Joypad(interrupts: new InterruptController());

                joypad.Write(value: SelectActions);
                joypad.SetButton(button: JoypadButton.A, pressed: true);

                return (joypad.Read() == 0xDE)
                    ? null
                    : $"0x{joypad.Read():X2} (expected 0xDE)";
            }),
            ("a fresh press requests the joypad interrupt", static () => {
                var interrupts = new InterruptController();
                var joypad = new Joypad(interrupts: interrupts);

                joypad.SetButton(button: JoypadButton.Start, pressed: true);

                return ((interrupts.InterruptFlag & (byte)InterruptKind.Joypad) != 0)
                    ? null
                    : "no joypad interrupt on press";
            }),
            ("serial transfer completes, interrupts, and shifts in ones", static () => {
                var interrupts = new InterruptController();
                var counter = 0;
                var serial = new Serial(interrupts: interrupts, systemCounter: () => counter);

                serial.WriteData(value: 0x42);
                serial.WriteControl(value: 0x81); // start + internal clock

                // The shift clock is the system counter's bit 8 (a falling edge every 512 T-cycles), so the eight
                // bits complete after 8 x 512 = 4096 T-cycles. Drive the counter a machine cycle at a time, as the
                // bus does (the timer advances it before the serial samples it).
                for (var cycle = 0; cycle < 4096; cycle += 4) {
                    counter = ((counter + 4) & 0xFFFF);
                    serial.Step(tCycles: 4);
                }

                var finished = ((serial.ReadControl() & 0x80) == 0);
                var raised = ((interrupts.InterruptFlag & (byte)InterruptKind.Serial) != 0);
                var shiftedIn = (serial.ReadData() == 0xFF);

                return (finished && raised && shiftedIn)
                    ? null
                    : $"finished={finished} irq={raised} SB=0x{serial.ReadData():X2}";
            }),
            ("serial surfaces the transmitted byte", static () => {
                var serial = new Serial(interrupts: new InterruptController(), systemCounter: static () => 0);
                var captured = (byte)0;

                serial.ByteTransmitted = value => captured = value;
                serial.WriteData(value: 0x5A);
                serial.WriteControl(value: 0x81);

                return (captured == 0x5A)
                    ? null
                    : $"captured 0x{captured:X2} (expected 0x5A)";
            }),
        ];
}
