namespace Puck.HumbleGamingDeck.Post;

/// <summary>Checks reset, interrupt, RDY, and JAM timing against original bus-cycle fixtures.</summary>
internal sealed class CpuTimingStage : IPostStage<PostContext> {
    /// <inheritdoc/>
    public string Name => "cpu-interrupts-rdy";
    /// <inheritdoc/>
    public PostTier Tier => PostTier.A;

    /// <inheritdoc/>
    public PostStageOutcome Run(PostContext context) {
        var checks = new (string Name, Action Check)[] {
            ("power-on-reset-stack-reads", Reset),
            ("nmi-hijacks-brk", NmiHijack),
            ("nmi-hijacks-irq", NmiHijackIrq),
            ("taken-branch-irq-delay", BranchIrq),
            ("cli-sei-plp-latency", FlagLatency),
            ("rdy-repeated-read-and-write-through", Ready),
            ("jam-bus-sequence-replay-rdy-reset", Jam),
        };
        var cases = new List<PostCaseResult>();

        foreach (var (name, check) in checks) {
            try {
                check();
                cases.Add(item: new(Detail: "original bus-cycle fixture", Duration: TimeSpan.Zero, Name: name, Verdict: PostCaseVerdict.Pass));
            } catch (InvalidOperationException exception) {
                cases.Add(item: new(Name: name, Verdict: PostCaseVerdict.Mismatch, Detail: exception.Message, Duration: TimeSpan.Zero));
            }
        }

        return (cases.Any(predicate: item => (item.Verdict != PostCaseVerdict.Pass))
            ? PostStageOutcome.Fail(cases: cases, detail: "interrupt/RDY fixture mismatch")
            : PostStageOutcome.Pass(cases: cases, detail: "seven original reset, interrupt-poll, NMI-hijack, RDY, and JAM fixtures; all 12 JAM encodings with replay and reset at every phase"));
    }

    private static (CpuTestMemory Memory, HgdCpu<Nes6502SstBus> Cpu) Create(params byte[] program) {
        var memory = new CpuTestMemory();

        memory.Reset();
        for (var index = 0; (index < program.Length); ++index) {
            memory[((ushort)(0x8000 + index))] = program[index];
        }
        memory[0xFFFA] = 0x00;
        memory[0xFFFB] = 0xA0;
        memory[0xFFFC] = 0x00;
        memory[0xFFFD] = 0x80;
        memory[0xFFFE] = 0x00;
        memory[0xFFFF] = 0x90;
        var cpu = new HgdCpu<Nes6502SstBus>(bus: new(memory: memory));

        cpu.Seed(a: 0, p: 0x20, pc: 0x8000, s: 0xFD, x: 0, y: 0);

        return (memory, cpu);
    }
    private static void Cycles(HgdCpu<Nes6502SstBus> cpu, int count) {
        for (var index = 0; (index < count); ++index) {
            cpu.StepCycle();
        }
    }
    private static void Require(bool condition, string message) {
        if (!condition) {
            throw new InvalidOperationException(message: message);
        }
    }
    private static void Reset() {
        var (memory, _) = Create(0xEA);
        var cpu = new HgdCpu<Nes6502SstBus>(bus: new(memory: memory));

        Cycles(count: 7, cpu: cpu);
        Require(condition: ((cpu.ProgramCounter == 0x8000) && (cpu.S == 0xFD) && (cpu.P == 0x24) && cpu.AtInstructionBoundary),
            message: "power-on reset does not produce PC=$8000, S=$FD, P=$24 after seven cycles");
        Require(condition: (memory.Accesses.Select(selector: access => access.Address).SequenceEqual(second: new ushort[] { 0, 0, 0x100, 0x1FF, 0x1FE, 0xFFFC, 0xFFFD }) &&
            memory.Accesses.All(predicate: access => !access.Write)), message: "reset must read both PC cycles and all three stack cycles");
        cpu.A = 0x43;
        cpu.P |= 8;
        cpu.Reset();
        Cycles(count: 7, cpu: cpu);
        Require(condition: ((cpu.A == 0x43) && (cpu.S == 0xFA) && ((cpu.P & 8) != 0)), message: "reset discarded preserved registers or decimal flag");
        cpu.Reset();
        cpu.Ready = false;
        var count = memory.Accesses.Count;

        Cycles(count: 3, cpu: cpu);
        Require(condition: ((cpu.Microcycle == 0) && (memory.Accesses.Count == (count + 3)) &&
            memory.Accesses.Skip(count: count).All(predicate: access => ((access.Address == 0x8000) && !access.Write))),
            message: "RDY must repeat reset reads without advancing the reset sequencer");
        cpu.Ready = true;
        Cycles(count: 7, cpu: cpu);
        Require(condition: ((cpu.S == 0xF7) && cpu.AtInstructionBoundary), message: "reset did not resume when RDY was released");
    }
    private static void NmiHijack() {
        var (memory, cpu) = Create(0x00, 0xEA);
        Cycles(count: 3, cpu: cpu);
        cpu.Nmi = true;
        Cycles(count: 4, cpu: cpu);
        Require(condition: ((cpu.ProgramCounter == 0xA000) && (memory[0x1FC] == 2) && ((memory[0x1FB] & 0x10) != 0)),
            message: "NMI hijack must preserve BRK's PC+2 and pushed B flag while selecting $FFFA");
        Require(condition: ((memory.Accesses[5].Address == 0xFFFA) && (memory.Accesses[6].Address == 0xFFFB)),
            message: "BRK hijack read the wrong vector");
    }
    private static void NmiHijackIrq() {
        var (memory, cpu) = Create(0xEA, 0xEA);
        cpu.Irq = true;
        Cycles(count: 5, cpu: cpu);
        cpu.Nmi = true;
        Cycles(count: 4, cpu: cpu);
        Require(condition: ((cpu.ProgramCounter == 0xA000) && (memory[0x1FC] == 1) && ((memory[0x1FB] & 0x10) == 0)),
            message: "NMI hijack must preserve IRQ's return PC and clear pushed B flag");
    }
    private static void BranchIrq() {
        var (_, cpu) = Create(0xD0, 0x02, 0xEA, 0xEA, 0xE8, 0xEA);
        cpu.StepCycle();
        cpu.Irq = true;
        Cycles(count: 2, cpu: cpu);
        Require(condition: (cpu.AtInstructionBoundary && (cpu.ProgramCounter == 0x8004)), message: "taken same-page branch has the wrong bus length or target");
        Cycles(count: 2, cpu: cpu);
        Require(condition: ((cpu.X == 1) && (cpu.ProgramCounter == 0x8005)), message: "IRQ asserted after branch polling must wait through the target instruction");
        Cycles(count: 7, cpu: cpu);
        Require(condition: (cpu.ProgramCounter == 0x9000), message: "delayed branch IRQ was lost");
    }
    private static void FlagLatency() {
        var (_, cli) = Create(0x58, 0xE8, 0xEA);
        cli.P = 0x24;
        cli.Irq = true;
        Cycles(count: 4, cpu: cli);
        Require(condition: ((cli.X == 1) && (cli.ProgramCounter == 0x8002)), message: "CLI failed to delay a pending IRQ for one instruction");
        Cycles(count: 7, cpu: cli);
        Require(condition: (cli.ProgramCounter == 0x9000), message: "CLI delayed IRQ was lost");

        var (_, sei) = Create(0x78, 0xE8);
        sei.Irq = true;
        Cycles(count: 9, cpu: sei);
        Require(condition: ((sei.ProgramCounter == 0x9000) && (sei.X == 0)), message: "SEI suppressed an IRQ already recognized using the old I flag");

        var (memory, plp) = Create(0x28, 0xE8, 0xEA);
        plp.P = 0x24;
        memory[0x1FE] = 0x20;
        plp.Irq = true;
        Cycles(count: 6, cpu: plp);
        Require(condition: (plp.X == 1), message: "PLP failed to poll using the pre-pull I flag");
        Cycles(count: 7, cpu: plp);
        Require(condition: (plp.ProgramCounter == 0x9000), message: "PLP delayed IRQ was lost");
    }
    private static void Ready() {
        var (memory, cpu) = Create(0xA5, 0x10, 0x85, 0x11);
        memory[0x10] = 0x12;
        Cycles(count: 2, cpu: cpu);
        cpu.Ready = false;
        Cycles(count: 3, cpu: cpu);
        Require(condition: ((cpu.A == 0) && memory.Accesses.Skip(count: 2).All(predicate: access => ((access.Address == 0x10) && !access.Write))),
            message: "RDY must repeat the same bus read and leave the sequencer parked");
        memory[0x10] = 0x34;
        cpu.Ready = true;
        cpu.StepCycle();
        Require(condition: (cpu.A == 0x34), message: "RDY release must sample the current bus value");
        Cycles(count: 2, cpu: cpu);
        cpu.Ready = false;
        cpu.StepCycle();
        Require(condition: ((memory[0x11] == 0x34) && cpu.AtInstructionBoundary), message: "RDY must not halt a write cycle");
    }
    private static void Jam() {
        ReadOnlySpan<byte> opcodes = [0x02, 0x12, 0x22, 0x32, 0x42, 0x52, 0x62, 0x72, 0x92, 0xB2, 0xD2, 0xF2];
        ushort[] addresses = [0x8000, 0x8001, 0xFFFF, 0xFFFE, 0xFFFE, 0xFFFF, 0xFFFF, 0xFFFF, 0xFFFF];

        foreach (var opcode in opcodes) {
            for (var offset = 0; (offset < addresses.Length); ++offset) {
                var (memory, cpu) = Create(opcode, 0xEA);
                var writer = new Puck.Machines.StateWriter();

                Cycles(count: offset, cpu: cpu);
                cpu.Ready = false;
                cpu.SaveState(writer: writer);
                var snapshot = writer.ToArray();

                Cycles(count: 3, cpu: cpu);
                Require(condition: memory.Accesses.Skip(count: offset).All(predicate: access =>
                    ((access.Address == addresses[offset]) && !access.Write)), message: $"JAM ${opcode:X2} RDY moved at cycle {offset}");
                cpu.Ready = true;
                Cycles(cpu: cpu, count: (addresses.Length - offset));
                var replayStart = memory.Accesses.Count;

                cpu.LoadState(reader: new Puck.Machines.StateReader(buffer: snapshot));
                Require(condition: !cpu.Ready, message: "JAM replay lost the RDY input");
                Cycles(count: 3, cpu: cpu);
                cpu.Ready = true;
                Cycles(cpu: cpu, count: (addresses.Length - offset));
                Require(condition: memory.Accesses.Skip(count: offset).Take(count: (replayStart - offset))
                    .SequenceEqual(second: memory.Accesses.Skip(count: replayStart)), message: $"JAM ${opcode:X2} replay differs at cycle {offset}");
                Require(condition: memory.Accesses.Take(count: offset).Concat(second: memory.Accesses.Skip(count: (offset + 3))
                    .Take(count: (addresses.Length - offset))).Select(selector: access => access.Address).SequenceEqual(second: addresses),
                    message: $"JAM ${opcode:X2} bus sequence differs at cycle {offset}");
                Require(condition: (cpu.IsJammed && !cpu.AtInstructionBoundary && (cpu.ProgramCounter == 0x8001)),
                    message: $"JAM ${opcode:X2} must keep PC at the byte after the opcode");
                cpu.Irq = true;
                cpu.Nmi = true;
                Cycles(count: 7, cpu: cpu);
                Require(condition: (cpu.IsJammed && (cpu.ProgramCounter == 0x8001) &&
                    memory.Accesses.TakeLast(count: 7).All(predicate: access => ((access.Address == 0xFFFF) && !access.Write))),
                    message: "Interrupt inputs must not leave JAM");

                cpu.LoadState(reader: new Puck.Machines.StateReader(buffer: snapshot));
                cpu.Ready = true;
                cpu.Reset();
                Cycles(count: 7, cpu: cpu);
                Require(condition: (!cpu.IsJammed && cpu.AtInstructionBoundary && (cpu.ProgramCounter == 0x8000) && (cpu.S == 0xFA)),
                    message: $"Reset did not leave JAM ${opcode:X2} at cycle {offset}");
                Require(condition: memory.Accesses.TakeLast(count: 7).All(predicate: access => !access.Write),
                    message: "Reset after JAM must not write to the stack");
            }
        }
    }
}
