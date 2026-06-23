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
            ("CGB undocumented registers FF72/FF73/FF74 round-trip fully", static () => {
                var bus = CgbBus();

                bus.WriteByte(address: 0xFF72, value: 0x5A);
                bus.WriteByte(address: 0xFF73, value: 0xA5);
                bus.WriteByte(address: 0xFF74, value: 0x3C);

                if ((bus.ReadByte(address: 0xFF72) != 0x5A) || (bus.ReadByte(address: 0xFF73) != 0xA5) || (bus.ReadByte(address: 0xFF74) != 0x3C)) {
                    return $"FF72=0x{bus.ReadByte(address: 0xFF72):X2} FF73=0x{bus.ReadByte(address: 0xFF73):X2} FF74=0x{bus.ReadByte(address: 0xFF74):X2}";
                }

                return null;
            }),
            ("CGB FF75 stores only bits 4-6, rest read as one", static () => {
                var bus = CgbBus();

                bus.WriteByte(address: 0xFF75, value: 0x40); // bit 6 only

                // Read = 0x8F (unused bits set) | the stored bits 4-6.
                return (bus.ReadByte(address: 0xFF75) == 0xCF)
                    ? null
                    : $"FF75 = 0x{bus.ReadByte(address: 0xFF75):X2} (expected 0xCF)";
            }),
            ("CGB RP infrared port reports no incoming signal", static () => {
                var bus = CgbBus();

                // Bit 1 (receiving) reads 1 when no IR light is present, which it never is with no peer.
                return ((bus.ReadByte(address: 0xFF56) & 0x02) != 0)
                    ? null
                    : $"RP = 0x{bus.ReadByte(address: 0xFF56):X2} (bit 1 should be set)";
            }),
            ("the CGB-only registers are inert on a DMG machine", static () => {
                var bus = DmgBus();

                bus.WriteByte(address: 0xFF72, value: 0x5A); // dropped on DMG

                return (bus.ReadByte(address: 0xFF72) == 0xFF)
                    ? null
                    : $"FF72 = 0x{bus.ReadByte(address: 0xFF72):X2} (expected open-bus 0xFF on DMG)";
            }),
        ];

    private static SystemBus CgbBus() =>
        new GameBoyMachine(model: ConsoleModel.Cgb, cartridge: Cartridge.Load(rom: HeaderedRom())).Bus;

    private static SystemBus DmgBus() =>
        new GameBoyMachine(model: ConsoleModel.Dmg, cartridge: Cartridge.Load(rom: HeaderedRom())).Bus;

    private static byte[] HeaderedRom() {
        var rom = new byte[0x8000];

        rom[0x0147] = 0x00; // ROM only
        rom[0x0148] = 0x00; // 32 KiB

        return rom;
    }
}
