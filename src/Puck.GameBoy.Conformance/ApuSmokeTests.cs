namespace Puck.GameBoy.Conformance;

/// <summary>
/// Self-contained audio register-plane checks driving an <see cref="Apu"/> directly: the hardware read masks
/// (each register exposes only some bits, the rest read as one), the <c>NR52</c> power switch (powering down
/// zeroes the registers and reports no active channels, and while down the registers ignore writes), wave-RAM
/// access independent of power, and the documented post-boot register state.
/// </summary>
internal static class ApuSmokeTests {
    private const ushort Nr10 = 0xFF10;
    private const ushort Nr11 = 0xFF11;
    private const ushort Nr30 = 0xFF1A;
    private const ushort Nr50 = 0xFF24;
    private const ushort Nr52 = 0xFF26;
    private const ushort WaveRam0 = 0xFF30;

    public static IReadOnlyList<(string Name, Func<string?> Run)> All =>
        [
            ("a powered-down APU reads NR52 as 0x70", static () => {
                var apu = new Apu(systemCounter: static () => 0);

                return (apu.Read(address: Nr52) == 0x70)
                    ? null
                    : $"0x{apu.Read(address: Nr52):X2} (expected 0x70)";
            }),
            ("powering on sets NR52 bit 7", static () => {
                var apu = new Apu(systemCounter: static () => 0);

                apu.Write(address: Nr52, value: 0x80);

                return (apu.Read(address: Nr52) == 0xF0)
                    ? null
                    : $"0x{apu.Read(address: Nr52):X2} (expected 0xF0)";
            }),
            ("read masks apply the always-one bits", static () => {
                var apu = new Apu(systemCounter: static () => 0);

                apu.Write(address: Nr52, value: 0x80); // power on so writes land

                apu.Write(address: Nr10, value: 0x00); // NR10 mask 0x80
                apu.Write(address: Nr11, value: 0x00); // NR11 mask 0x3F
                apu.Write(address: Nr30, value: 0x00); // NR30 mask 0x7F

                var nr10 = apu.Read(address: Nr10);
                var nr11 = apu.Read(address: Nr11);
                var nr30 = apu.Read(address: Nr30);

                return ((nr10 == 0x80) && (nr11 == 0x3F) && (nr30 == 0x7F))
                    ? null
                    : $"NR10=0x{nr10:X2} NR11=0x{nr11:X2} NR30=0x{nr30:X2} (expected 0x80 0x3F 0x7F)";
            }),
            ("a fully readable register round-trips while powered", static () => {
                var apu = new Apu(systemCounter: static () => 0);

                apu.Write(address: Nr52, value: 0x80);
                apu.Write(address: Nr50, value: 0x77); // NR50 mask 0x00

                return (apu.Read(address: Nr50) == 0x77)
                    ? null
                    : $"0x{apu.Read(address: Nr50):X2} (expected 0x77)";
            }),
            ("powering off zeroes the registers", static () => {
                var apu = new Apu(systemCounter: static () => 0);

                apu.Write(address: Nr52, value: 0x80);
                apu.Write(address: Nr50, value: 0x77);
                apu.Write(address: Nr52, value: 0x00); // power off clears NR10-NR51

                return (apu.Read(address: Nr50) == 0x00)
                    ? null
                    : $"0x{apu.Read(address: Nr50):X2} (expected 0x00 after power off)";
            }),
            ("a powered-down APU ignores register writes", static () => {
                var apu = new Apu(systemCounter: static () => 0); // starts powered down

                apu.Write(address: Nr50, value: 0x77);

                return (apu.Read(address: Nr50) == 0x00)
                    ? null
                    : $"0x{apu.Read(address: Nr50):X2} (expected 0x00; write ignored while off)";
            }),
            ("wave RAM is accessible regardless of power", static () => {
                var apu = new Apu(systemCounter: static () => 0); // powered down

                apu.Write(address: WaveRam0, value: 0xAB);

                return (apu.Read(address: WaveRam0) == 0xAB)
                    ? null
                    : $"0x{apu.Read(address: WaveRam0):X2} (expected 0xAB)";
            }),
            ("a length-enabled channel disables when its length counter expires", static () => {
                var counter = 0;
                var apu = new Apu(systemCounter: () => counter);

                apu.Write(address: Nr52, value: 0x80);  // power on
                // Channel 2, as Blargg's sync_apu does: length = 2, DAC on (silent), trigger with length enabled.
                apu.Write(address: 0xFF19, value: 0x00); // NR24: clear length-enable
                apu.Write(address: 0xFF16, value: 0x3E); // NR21: length load -> 2
                apu.Write(address: 0xFF17, value: 0x08); // NR22: DAC on, volume 0
                apu.Write(address: 0xFF19, value: 0xC0); // NR24: trigger + length enable

                var enabledAtTrigger = ((apu.Read(address: Nr52) & 0x02) != 0);

                // Drive the system counter so the frame sequencer (bit 12 falling edge) clocks the length counter.
                for (var cycle = 0; cycle < 40000; cycle += 4) {
                    counter = ((counter + 4) & 0xFFFF);
                    apu.Step(tCycles: 4);
                }

                var disabledAfter = ((apu.Read(address: Nr52) & 0x02) == 0);

                return (enabledAtTrigger && disabledAfter)
                    ? null
                    : $"enabledAtTrigger={enabledAtTrigger} disabledAfterLength={disabledAfter}";
            }),
            ("the post-boot seed reports channel 1 active and the documented registers", static () => {
                var apu = new Apu(systemCounter: static () => 0);

                apu.InitializePostBoot();

                var nr52 = apu.Read(address: Nr52);
                var nr10 = apu.Read(address: Nr10);
                var nr50 = apu.Read(address: Nr50);

                return ((nr52 == 0xF1) && (nr10 == 0x80) && (nr50 == 0x77))
                    ? null
                    : $"NR52=0x{nr52:X2} NR10=0x{nr10:X2} NR50=0x{nr50:X2} (expected 0xF1 0x80 0x77)";
            }),
        ];
}
