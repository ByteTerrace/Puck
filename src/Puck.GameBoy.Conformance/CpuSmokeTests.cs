namespace Puck.GameBoy.Conformance;

/// <summary>
/// Self-contained CPU checks that need no external assets: small hand-assembled programs run on a
/// <see cref="FlatRamBus"/>, asserting register results, exact flag bytes, and machine-cycle counts. They
/// target the error-prone areas — flag edge cases (half-carry/borrow), cycle timing, the accumulator-rotate vs
/// CB-rotate zero-flag difference, DAA, and control flow — as a fast first gate before the SingleStepTests
/// vectors (which need the external asset path).
/// </summary>
internal static class CpuSmokeTests {
    private const byte FlagZero = 0x80;
    private const byte FlagSubtract = 0x40;
    private const byte FlagHalfCarry = 0x20;
    private const byte FlagCarry = 0x10;

    public static IReadOnlyList<(string Name, Func<string?> Run)> All =>
        [
            ("ADD A,n half-carry", static () => {
                var (cpu, bus) = Run(steps: 2, program: [0x3E, 0x0F, 0xC6, 0x01]);

                return Expect(cpu, bus, a: 0x10, f: FlagHalfCarry, cycles: 4);
            }),
            ("ADD A,n carry+zero", static () => {
                var (cpu, bus) = Run(steps: 2, program: [0x3E, 0xFF, 0xC6, 0x01]);

                return Expect(cpu, bus, a: 0x00, f: (FlagZero | FlagHalfCarry | FlagCarry), cycles: 4);
            }),
            ("SUB n half-borrow", static () => {
                var (cpu, bus) = Run(steps: 2, program: [0x3E, 0x10, 0xD6, 0x01]);

                return Expect(cpu, bus, a: 0x0F, f: (FlagSubtract | FlagHalfCarry), cycles: 4);
            }),
            ("INC r preserves carry, sets H/Z", static () => {
                // SCF; LD B,0xFF; INC B -> B=0x00, Z+H set, N clear, carry preserved.
                var (cpu, _) = Run(steps: 3, program: [0x37, 0x06, 0xFF, 0x04]);

                return ((cpu.B == 0x00) && (cpu.F == (FlagZero | FlagHalfCarry | FlagCarry)))
                    ? null
                    : $"B=0x{cpu.B:X2} F=0x{cpu.F:X2}";
            }),
            ("INC rr value and timing", static () => {
                var (cpu, bus) = Run(steps: 2, program: [0x01, 0xFF, 0x00, 0x03]);

                return ((cpu.BC == 0x0100) && (bus.MachineCycles == 5))
                    ? null
                    : $"BC=0x{cpu.BC:X4} cycles={bus.MachineCycles}";
            }),
            ("CALL then RET restores SP and PC", static () => {
                // LD SP,FFFE; CALL 0008; (ret target) LD A,42; at 0008: LD B,99; RET.
                var (cpu, _) = Run(
                    steps: 5,
                    program: [0x31, 0xFE, 0xFF, 0xCD, 0x08, 0x00, 0x3E, 0x42, 0x06, 0x99, 0xC9]
                );

                return ((cpu.A == 0x42) && (cpu.B == 0x99) && (cpu.StackPointer == 0xFFFE))
                    ? null
                    : $"A=0x{cpu.A:X2} B=0x{cpu.B:X2} SP=0x{cpu.StackPointer:X4}";
            }),
            ("PUSH/POP AF drops F low nibble", static () => {
                // LD SP,FFFE; LD BC,0x12FF; PUSH BC; POP AF; PUSH AF; POP HL -> HL=0x12F0 (not 0x12FF).
                var (cpu, _) = Run(
                    steps: 6,
                    program: [0x31, 0xFE, 0xFF, 0x01, 0xFF, 0x12, 0xC5, 0xF1, 0xF5, 0xE1]
                );

                return (cpu.HL == 0x12F0)
                    ? null
                    : $"HL=0x{cpu.HL:X4} (expected 0x12F0)";
            }),
            ("RLCA always clears Z", static () => {
                var (cpu, _) = Run(steps: 2, program: [0x3E, 0x00, 0x07]);

                return ((cpu.F & FlagZero) == 0)
                    ? null
                    : $"F=0x{cpu.F:X2} (RLCA must clear Z)";
            }),
            ("CB RLC sets Z from result", static () => {
                var (cpu, _) = Run(steps: 2, program: [0x3E, 0x00, 0xCB, 0x07]);

                return ((cpu.F & FlagZero) != 0)
                    ? null
                    : $"F=0x{cpu.F:X2} (CB RLC must set Z when result is 0)";
            }),
            ("DAA after 0x99+0x01 = 0x00, C set", static () => {
                var (cpu, _) = Run(steps: 3, program: [0x3E, 0x99, 0xC6, 0x01, 0x27]);

                return ((cpu.A == 0x00) && (cpu.F == (FlagZero | FlagCarry)))
                    ? null
                    : $"A=0x{cpu.A:X2} F=0x{cpu.F:X2}";
            }),
            ("ADD SP,e positive half-carry", static () => {
                var (cpu, bus) = Run(steps: 2, program: [0x31, 0x0F, 0x00, 0xE8, 0x01]);

                return ((cpu.StackPointer == 0x0010) && (cpu.F == FlagHalfCarry) && (bus.MachineCycles == 7))
                    ? null
                    : $"SP=0x{cpu.StackPointer:X4} F=0x{cpu.F:X2} cycles={bus.MachineCycles}";
            }),
            ("ADD SP,e negative byte-carry", static () => {
                var (cpu, _) = Run(steps: 2, program: [0x31, 0x10, 0x00, 0xE8, 0xFF]);

                return ((cpu.StackPointer == 0x000F) && (cpu.F == FlagCarry))
                    ? null
                    : $"SP=0x{cpu.StackPointer:X4} F=0x{cpu.F:X2}";
            }),
            ("CP equal sets Z and N, keeps A", static () => {
                var (cpu, _) = Run(steps: 2, program: [0x3E, 0x42, 0xFE, 0x42]);

                return ((cpu.A == 0x42) && (cpu.F == (FlagZero | FlagSubtract)))
                    ? null
                    : $"A=0x{cpu.A:X2} F=0x{cpu.F:X2}";
            }),
            ("BIT set leaves carry untouched", static () => {
                // SCF; LD A,0x10; BIT 4,A -> Z clear (bit set), H set, carry preserved.
                var (cpu, _) = Run(steps: 3, program: [0x37, 0x3E, 0x10, 0xCB, 0x67]);

                return (cpu.F == (FlagHalfCarry | FlagCarry))
                    ? null
                    : $"F=0x{cpu.F:X2} (expected H+C, Z clear)";
            }),
            ("DEC/JR NZ loop terminates", static () => {
                // LD B,3; loop: DEC B; JR NZ,loop; LD A,0xAA.
                var (cpu, _) = Run(
                    steps: 8,
                    program: [0x06, 0x03, 0x05, 0x20, 0xFD, 0x3E, 0xAA]
                );

                return ((cpu.B == 0x00) && (cpu.A == 0xAA))
                    ? null
                    : $"B=0x{cpu.B:X2} A=0x{cpu.A:X2}";
            }),
        ];

    private static (Sm83 Cpu, FlatRamBus Bus) Run(int steps, params byte[] program) {
        var bus = new FlatRamBus();

        bus.LoadProgram(
            address: 0x0000,
            bytes: program
        );

        var cpu = new Sm83(bus: bus);

        for (var index = 0; index < steps; index += 1) {
            cpu.Step();
        }

        return (cpu, bus);
    }

    private static string? Expect(Sm83 cpu, FlatRamBus bus, byte a, byte f, long cycles) =>
        ((cpu.A == a) && (cpu.F == f) && (bus.MachineCycles == cycles))
            ? null
            : $"A=0x{cpu.A:X2} (want 0x{a:X2}), F=0x{cpu.F:X2} (want 0x{f:X2}), cycles={bus.MachineCycles} (want {cycles})";
}
