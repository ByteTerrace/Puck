namespace Puck.GameBoy.Conformance;

/// <summary>
/// Self-contained checks of the host-facing pacing seam — the contract a Puck render node drives the emulator
/// through: <see cref="GameBoyMachine.Run"/> advancing an exact master-cycle budget (to instruction granularity)
/// with bounded, non-accumulating overshoot, a frame completing every frame-length budget, the framebuffer
/// presenting whole frames, identical run schedules being bit-for-bit deterministic, and joypad input being
/// reachable on a paced machine. These prove the core is ready to host inside a viewport without any of the
/// hosting plumbing existing yet.
/// </summary>
internal static class HostPacingSmokeTests {
    // A full Game Boy frame is 154 lines x 456 dots of master clock.
    private const ulong FrameDots = 70224UL;
    // The longest SM83 instruction / interrupt dispatch is six machine cycles = 24 dots, so a single Run call can
    // overshoot its budget by at most this much — and the cumulative target must hold this bound across any number
    // of calls (that is the no-drift guarantee).
    private const ulong MaxInstructionDots = 24UL;
    // A deliberately frame-misaligned budget, so the per-call overshoot is non-zero and the carry is actually exercised.
    private const ulong MisalignedBudget = 50003UL;

    public static IReadOnlyList<(string Name, Func<string?> Run)> All =>
        [
            ("Run advances the master-cycle budget within one instruction", static () => {
                var machine = NewSpinningMachine();

                machine.Run(cycles: MisalignedBudget);

                var dots = machine.Bus.ElapsedDots;

                return ((dots >= MisalignedBudget) && (dots < (MisalignedBudget + MaxInstructionDots)))
                    ? null
                    : $"ElapsedDots={dots} (expected [{MisalignedBudget}, {MisalignedBudget + MaxInstructionDots}))";
            }),
            ("repeated Run calls carry overshoot and never drift", static () => {
                var machine = NewSpinningMachine();
                const int callCount = 200;

                for (var index = 0; index < callCount; index += 1) {
                    machine.Run(cycles: MisalignedBudget);
                }

                // The cumulative target keeps the total within ONE instruction of the summed budget, no matter how
                // many calls — a broken carry would let ~callCount instructions of overshoot pile up.
                var expected = (MisalignedBudget * callCount);
                var dots = machine.Bus.ElapsedDots;

                return ((dots >= expected) && (dots < (expected + MaxInstructionDots)))
                    ? null
                    : $"after {callCount} calls ElapsedDots={dots} (expected [{expected}, {expected + MaxInstructionDots}))";
            }),
            ("a frame completes each frame-length budget", static () => {
                var machine = NewSpinningMachine();
                var framesReady = 0;

                for (var index = 0; index < 3; index += 1) {
                    machine.Run(cycles: FrameDots);

                    if (machine.Ppu.ConsumeFrameReady()) {
                        framesReady += 1;
                    }
                }

                return (framesReady == 3)
                    ? null
                    : $"framesReady={framesReady} (expected 3)";
            }),
            ("identical run schedules are bit-for-bit deterministic", static () => {
                var first = NewSpinningMachine();
                var second = NewSpinningMachine();

                for (var index = 0; index < 5; index += 1) {
                    first.Run(cycles: FrameDots);
                    second.Run(cycles: FrameDots);
                }

                var sameClock = (first.Bus.ElapsedDots == second.Bus.ElapsedDots);
                var sameFrame = (HashFramebuffer(machine: first) == HashFramebuffer(machine: second));
                var sameProgramCounter = (first.Cpu.ProgramCounter == second.Cpu.ProgramCounter);

                return (sameClock && sameFrame && sameProgramCounter)
                    ? null
                    : $"sameClock={sameClock} sameFrame={sameFrame} samePc={sameProgramCounter}";
            }),
            ("joypad input is reachable on a paced machine and stays deterministic", static () => {
                var first = NewSpinningMachine();
                var second = NewSpinningMachine();

                for (var index = 0; index < 4; index += 1) {
                    var pressed = ((index % 2) == 0);

                    first.Bus.Joypad.SetButton(button: JoypadButton.A, pressed: pressed);
                    second.Bus.Joypad.SetButton(button: JoypadButton.A, pressed: pressed);
                    first.Run(cycles: FrameDots);
                    second.Run(cycles: FrameDots);
                }

                // The spinning ROM never touches the joypad register, so the host's group select and held button
                // survive: selecting the action group reads A as held-low (0xDE, as in the joypad smoke test).
                first.Bus.Joypad.Write(value: 0x10);
                first.Bus.Joypad.SetButton(button: JoypadButton.A, pressed: true);

                var reflectsPress = (first.Bus.Joypad.Read() == 0xDE);
                var deterministic =
                    (first.Bus.ElapsedDots == second.Bus.ElapsedDots) &&
                    (HashFramebuffer(machine: first) == HashFramebuffer(machine: second));

                return (reflectsPress && deterministic)
                    ? null
                    : $"reflectsPress={reflectsPress} deterministic={deterministic}";
            }),
        ];

    // A minimal 32 KiB ROM-only image whose entry point is an infinite self-jump (JR -2 at 0x0100). The CPU spins
    // deterministically while the PPU keeps rendering, which is all the pacing/determinism gates need — no assets.
    private static GameBoyMachine NewSpinningMachine() {
        var rom = new byte[0x8000];

        rom[0x0100] = 0x18; // JR e
        rom[0x0101] = 0xFE; // e = -2, so the jump targets 0x0100 again

        // A zeroed header already decodes to cartridge type 0x00 (ROM only) with no RAM.
        return new GameBoyMachine(
            cartridge: Cartridge.Load(rom: rom),
            model: ConsoleModel.Dmg
        );
    }

    // FNV-1a over the presented framebuffer bytes: a compact fingerprint for comparing two runs' output.
    private static ulong HashFramebuffer(GameBoyMachine machine) {
        var hash = 14695981039346656037UL;
        var framebuffer = machine.Ppu.Framebuffer;

        foreach (var pixel in framebuffer) {
            for (var shift = 0; shift < 32; shift += 8) {
                hash ^= (byte)(pixel >> shift);
                hash *= 1099511628211UL;
            }
        }

        return hash;
    }
}
