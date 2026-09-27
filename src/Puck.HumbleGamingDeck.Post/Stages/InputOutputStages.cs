using System.Numerics;
using Puck.Abstractions.Machines;
using Puck.Machines;

namespace Puck.HumbleGamingDeck.Post;

/// <summary>
/// Reads both controller ports the way a game does — strobe $4016 high then low, then ten reads of each port — with
/// buttons held through the neutral pad mapping, and checks the eight button bits in order, the ones a standard
/// controller reports after them, and the open-bus bits a read of $4016 carries.
/// </summary>
internal sealed class ControllerStage : IPostStage<PostContext> {
    /// <inheritdoc/>
    public string Name => "controllers";
    /// <inheritdoc/>
    public PostTier Tier => PostTier.A;

    /// <inheritdoc/>
    public PostStageOutcome Run(PostContext context) {
        var program = new DeckProgram()
            .Bytes(0x78, 0xD8).Op(opcode: 0xA2, operand: ((byte)0xFF)).Bytes(0x9A)
            .Label(name: "poll")
            .Op(opcode: 0xA9, operand: ((byte)0x01)).Op(address: 0x4016, opcode: 0x8D)
            .Op(opcode: 0xA9, operand: ((byte)0x00)).Op(address: 0x4016, opcode: 0x8D)
            .Op(opcode: 0xA2, operand: ((byte)0x00))
            .Label(name: "read")
            .Op(address: 0x4016, opcode: 0xAD).Op(opcode: 0x29, operand: ((byte)0x01)).Op(opcode: 0x95, operand: ((byte)0x20))
            .Op(address: 0x4017, opcode: 0xAD).Op(opcode: 0x29, operand: ((byte)0x01)).Op(opcode: 0x95, operand: ((byte)0x30))
            .Bytes(0xE8).Op(opcode: 0xE0, operand: ((byte)10)).Branch(label: "read", opcode: 0xD0)
            .Op(address: 0x4016, opcode: 0xAD).Op(opcode: 0x85, operand: ((byte)0x40))
            .Jump(label: "poll", opcode: 0x4C);
        using var instance = HgdMachineFactory.Create(configuration: new HgdMachineConfiguration(
            cartridge: HgdCartridge.Load(image: program.Build(chr: []))));
        var machine = instance.Machine;
        var pads = MachinePads.From(inputs: [
            new MachinePadState(
                Buttons: MachineButtons.South | MachineButtons.Start | MachineButtons.DpadLeft,
                LeftStick: Vector2.Zero,
                LeftTrigger: 0f,
                RightStick: Vector2.Zero,
                RightTrigger: 0f
            ),
            new MachinePadState(
                Buttons: MachineButtons.East,
                LeftStick: new Vector2(x: 1f, y: 0f),
                LeftTrigger: 0f,
                RightStick: Vector2.Zero,
                RightTrigger: 0f
            ),
        ]);

        HgdPad.Apply(
            controllers: machine.Controllers,
            pads: in pads
        );
        machine.RunCycles(masterTicks: (3 * 357_366UL));

        // A, B, Select, Start, Up, Down, Left, Right, then the ones a standard controller shifts in.
        ReadOnlySpan<byte> first = [1, 0, 0, 1, 0, 0, 1, 0, 1, 1];
        ReadOnlySpan<byte> second = [0, 1, 0, 0, 0, 0, 0, 1, 1, 1];

        for (var bit = 0; (bit < 10); ++bit) {
            var one = machine.Bus.Peek(address: ((ushort)(0x20 + bit)));
            var two = machine.Bus.Peek(address: ((ushort)(0x30 + bit)));

            if ((one != first[bit]) || (two != second[bit])) {
                return PostStageOutcome.Fail(detail: $"read {bit}: ports {one}/{two}, expected {first[bit]}/{second[bit]}");
            }
        }

        // LDA $4016 leaves $40, the address's high byte, on the bus, so bits 5-7 read back as 010.
        var raw = machine.Bus.Peek(address: 0x40);

        if ((raw & 0xE0) != 0x40) {
            return PostStageOutcome.Fail(detail: $"$4016 read ${raw:X2}; expected open-bus bits 5-7 of $40");
        }

        return PostStageOutcome.Pass(detail: "both ports report A, B, Select, Start, Up, Down, Left, Right in order through the pad mapping, then 1s; $4016 bits 5-7 are open bus");
    }
}
/// <summary>
/// Times an OAM DMA started on each CPU cycle parity and checks its copy: the $4014 write's four cycles plus the DMA's
/// 513 when it starts on one parity and 514 on the other, and object memory equal to the source page afterwards.
/// https://www.nesdev.org/wiki/PPU_registers#OAMDMA
/// </summary>
internal sealed class OamDmaStage : IPostStage<PostContext> {
    /// <inheritdoc/>
    public string Name => "oam-dma";
    /// <inheritdoc/>
    public PostTier Tier => PostTier.A;

    /// <inheritdoc/>
    public PostStageOutcome Run(PostContext context) {
        var durations = new HashSet<ulong>();

        foreach (var shift in ((bool[])[false, true])) {
            var program = new DeckProgram()
                .Bytes(0x78, 0xD8).Op(opcode: 0xA2, operand: ((byte)0xFF)).Bytes(0x9A)
                .Op(opcode: 0xA2, operand: ((byte)0x00))
                .Label(name: "fill").Bytes(0x8A).Op(opcode: 0x49, operand: ((byte)0x5A)).Op(address: 0x0300, opcode: 0x9D)
                .Bytes(0xE8).Branch(label: "fill", opcode: 0xD0)
                .Op(opcode: 0xA9, operand: ((byte)0x00)).Op(address: 0x2003, opcode: 0x8D);

            // A three-cycle load ahead of the page load moves the DMA onto the other CPU cycle parity.
            if (shift) {
                program.Op(opcode: 0xA5, operand: ((byte)0x00));
            }
            program.Op(opcode: 0xA9, operand: ((byte)0x03))
                .Label(name: "dma").Op(address: 0x4014, opcode: 0x8D)
                .Label(name: "after").Bytes(0xEA)
                .Label(name: "idle").Jump(label: "idle", opcode: 0x4C);

            using var instance = HgdMachineFactory.Create(configuration: new HgdMachineConfiguration(
                cartridge: HgdCartridge.Load(image: program.Build(chr: []))));
            var machine = instance.Machine;
            // The halted CPU sits on the NOP's opcode fetch for the whole transfer, so the span runs to the instruction
            // after the NOP: the write's four cycles, the DMA, and the NOP's two.
            var start = RunToInstruction(machine: machine, address: program.AddressOf(name: "dma"));
            var end = RunToInstruction(machine: machine, address: program.AddressOf(name: "idle"));
            var cycles = (((end - start) / 12UL) - 6UL);

            if ((cycles != 513) && (cycles != 514)) {
                return PostStageOutcome.Fail(detail: $"the DMA took {cycles} CPU cycles; expected 513 or 514");
            }
            durations.Add(item: cycles);
            for (var index = 0; (index < 256); ++index) {
                if (machine.Ppu.ObjectMemory[index] != ((byte)(index ^ 0x5A))) {
                    return PostStageOutcome.Fail(detail: $"object memory byte {index} is ${machine.Ppu.ObjectMemory[index]:X2}; expected ${index ^ 0x5A:X2}");
                }
            }
        }
        if (durations.Count != 2) {
            return PostStageOutcome.Fail(detail: $"both parities took {durations.First()} cycles; the DMA's alignment cycle never moved");
        }

        return PostStageOutcome.Pass(detail: "OAM DMA takes 513 and 514 cycles on the two parities and copies the whole page into object memory");
    }

    private static ulong RunToInstruction(HgdMachine machine, ushort address) {
        while (!(machine.Cpu.AtInstructionBoundary && (machine.Cpu.ProgramCounter == address) && (machine.Clock.CpuPhase == 0))) {
            machine.StepMasterTick();
        }

        return machine.MasterTicks;
    }
}
/// <summary>
/// Checks APU behaviour against the published NTSC figures: a 50% pulse at timer period 127 holds each level for
/// 8 × 128 APU cycles (1024 CPU cycles), a length counter loaded with 10 empties on the tenth half-frame clock after the
/// load, and in four-step mode the frame interrupt recurs every 29,830 CPU cycles.
/// https://www.nesdev.org/wiki/APU_Pulse and https://www.nesdev.org/wiki/APU_Frame_Counter
/// </summary>
internal sealed class ApuStage : IPostStage<PostContext> {
    /// <inheritdoc/>
    public string Name => "apu";
    /// <inheritdoc/>
    public PostTier Tier => PostTier.A;

    /// <inheritdoc/>
    public PostStageOutcome Run(PostContext context) =>
        (CheckPulse() ?? (CheckLength() ?? (CheckFrameInterrupt() ??
            PostStageOutcome.Pass(detail: "pulse 1 holds 1024-cycle levels of 15 and 0 at period 127, duty 2; a length of 10 empties on the tenth half-frame clock; the four-step frame interrupt recurs every 29830 cycles"))));

    private static HgdMachine Build(DeckProgram program, out IDisposable owner) {
        var instance = HgdMachineFactory.Create(configuration: new HgdMachineConfiguration(
            cartridge: HgdCartridge.Load(image: program.Build(chr: []))));

        owner = instance;

        return instance.Machine;
    }
    private static DeckProgram Program(params ReadOnlySpan<(int Address, int Value)> writes) {
        var program = new DeckProgram().Bytes(0x78, 0xD8).Op(opcode: 0xA2, operand: ((byte)0xFF)).Bytes(0x9A);

        foreach (var (address, value) in writes) {
            program.Op(opcode: 0xA9, operand: ((byte)value)).Op(address: ((ushort)address), opcode: 0x8D);
        }

        return program.Label(name: "idle").Jump(label: "idle", opcode: 0x4C);
    }
    private static void StepCpuCycle(HgdMachine machine) {
        do {
            machine.StepMasterTick();
        } while (machine.Clock.CpuPhase != 0);
    }
    private static PostStageOutcome? CheckPulse() {
        var machine = Build(
            owner: out var owner,
            program: Program((0x4015, 0x01), (0x4000, 0xBF), (0x4001, 0x00), (0x4002, 0x7F), (0x4003, 0x00))
        );

        using (owner) {
            machine.RunCycles(masterTicks: (12UL * 200));

            var runs = new List<(byte Level, int Length)>();
            var level = machine.Apu.PulseOneLevel;
            var length = 0;

            for (var cycle = 0; (cycle < 12_000); ++cycle) {
                StepCpuCycle(machine: machine);
                if (machine.Apu.PulseOneLevel == level) {
                    ++length;
                } else {
                    runs.Add(item: (level, length));
                    level = machine.Apu.PulseOneLevel;
                    length = 1;
                }
            }

            // The first run began before sampling started; the rest are whole.
            var whole = runs.Skip(count: 1).ToArray();

            if ((whole.Length < 4) || whole.Any(predicate: static run => ((run.Length != 1024) || ((run.Level != 15) && (run.Level != 0))))) {
                return PostStageOutcome.Fail(detail: $"pulse runs {string.Join(separator: ", ", values: whole.Select(selector: static run => $"{run.Level}x{run.Length}"))}; expected alternating 15x1024 and 0x1024");
            }
        }

        return null;
    }
    private static PostStageOutcome? CheckLength() {
        var machine = Build(
            owner: out var owner,
            program: Program((0x4015, 0x01), (0x4000, 0x9F), (0x4003, 0x00))
        );

        using (owner) {
            // Run to the end of the $4003 write, the moment the counter loads 10.
            while ((machine.Apu.PeekStatus(openBus: 0) & 1) == 0) {
                StepCpuCycle(machine: machine);
            }

            var loaded = machine.Cpu.Cycles;
            var clocks = 0;
            var silenced = 0UL;

            // Half-frame clocks land on frame-sequence cycles 14913 and 29829 of each 29830-cycle sequence, which
            // began at power-on and has not been reset.
            for (var cycle = loaded; (clocks < 10); ++cycle) {
                var position = (cycle % 29830UL);

                if ((position == 14913) || (position == 29829)) {
                    ++clocks;
                    silenced = cycle;
                }
            }
            while (machine.Cpu.Cycles < (silenced - 1)) {
                StepCpuCycle(machine: machine);
            }
            if ((machine.Apu.PeekStatus(openBus: 0) & 1) == 0) {
                return PostStageOutcome.Fail(detail: $"the length counter emptied before cycle {silenced}, the tenth half-frame clock after its load at {loaded}");
            }
            StepCpuCycle(machine: machine);
            if ((machine.Apu.PeekStatus(openBus: 0) & 1) != 0) {
                return PostStageOutcome.Fail(detail: $"the length counter was still running after cycle {silenced}, the tenth half-frame clock after its load at {loaded}");
            }
        }

        return null;
    }
    private static PostStageOutcome? CheckFrameInterrupt() {
        var machine = Build(
            owner: out var owner,
            program: Program()
        );

        using (owner) {
            var onsets = new List<ulong>();

            while (onsets.Count < 4) {
                StepCpuCycle(machine: machine);
                if (machine.Apu.Irq) {
                    onsets.Add(item: machine.Cpu.Cycles);
                    // Acknowledge through $4015, then let the flag's three-cycle assertion window pass.
                    for (var cycle = 0; (cycle < 4); ++cycle) {
                        _ = machine.Apu.ReadStatus(openBus: 0);
                        StepCpuCycle(machine: machine);
                    }
                }
            }
            for (var index = 1; (index < onsets.Count); ++index) {
                if ((onsets[index] - onsets[(index - 1)]) != 29830UL) {
                    return PostStageOutcome.Fail(detail: $"frame interrupts at cycles {string.Join(separator: ", ", values: onsets)}; expected a 29830-cycle period");
                }
            }
        }

        return null;
    }
}
