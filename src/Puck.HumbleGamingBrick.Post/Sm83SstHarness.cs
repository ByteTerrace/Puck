using Microsoft.Extensions.DependencyInjection;
using Puck.HumbleGamingBrick.Interfaces;
using Puck.Snapshots;

namespace Puck.HumbleGamingBrick.Post;

/// <summary>The verdict for one SST vector.</summary>
/// <param name="Name">The vector's corpus name.</param>
/// <param name="Passed">Whether every comparison held.</param>
/// <param name="Detail">A one-line description of the first mismatches, empty when <paramref name="Passed"/>.</param>
internal readonly record struct Sm83SstVectorResult(string Name, bool Passed, string Detail);

/// <summary>
/// Isolates the shared SM83 core on <see cref="Sm83SstBus"/> for the SingleStepTests/sm83 vector battery — the DMG/CGB
/// analogue of the Advanced core's <c>FlatTestBus</c>-driven <c>Arm7Tdmi</c> smoke harness. The SM83 constructor needs
/// more collaborators than the ARM7TDMI does (an interrupt controller, the component clock, KEY1, HDMA, the joypad),
/// so the harness assembles a full machine through the normal DI composition and then overrides only the
/// <see cref="ISystemBus"/> registration: every other component (timer, PPU, APU, DMA units) still exists and still
/// ticks harmlessly, but nothing routes a register write to it, because the CPU only ever touches
/// <see cref="Sm83SstBus"/> — no real hardware register can spontaneously corrupt a vector's flat memory. One machine
/// is built once and reused for the whole corpus (500 files &#215; 1000 vectors): per-vector setup only pokes registers
/// and memory, it never rebuilds the container.
/// <para>
/// Setting the vector's IME/halted flags and reading them back uses no new core seam: <see cref="Sm83"/> already
/// implements <see cref="ISnapshotable"/>, so a hand-built 20-byte buffer in <c>SaveState</c>'s own field order round
/// -trips through <c>LoadState</c> to seed exactly the fields a register-only <c>ICpu</c> cannot reach, and a scratch
/// <c>SaveState</c> call reads them back the same way.
/// </para>
/// </summary>
internal sealed class Sm83SstHarness : IDisposable {
    private readonly Sm83SstBus m_bus = new();
    private readonly Sm83 m_cpu;
    private readonly MachineInstance m_instance;
    private readonly byte[] m_stateBuffer = new byte[Sm83StateCodec.ByteCount];
    private readonly StateWriter m_writer = new(capacity: Sm83StateCodec.ByteCount);

    /// <summary>Builds the isolated machine once.</summary>
    public Sm83SstHarness() {
        m_instance = MachineFactory.Create(
            configuration: new MachineConfiguration(model: ConsoleModel.Dmg, cartridgeRom: SyntheticRom.Create()),
            compose: services => {
                services.AddHumbleGamingBrickComponents();

                // Registered AFTER the line above, so it is the LAST ISystemBus registration and therefore the one a
                // single GetRequiredService<ISystemBus>() resolves (.NET DI: last registration wins for a non-enumerable
                // resolution). Only Sm83 consumes ISystemBus; every other component talks to SystemMemory/the cartridge
                // directly, so this substitution is total.
                services.AddScoped<ISystemBus>(implementationFactory: _ => m_bus);
            }
        );
        m_cpu = m_instance.GetRequiredService<Sm83>();
    }

    /// <summary>Runs one vector: seeds the initial registers/IME/RAM, executes exactly one instruction, and compares
    /// the resulting registers, RAM, and (best-effort) bus-pin trace against the vector's expectations.</summary>
    /// <param name="vector">The vector to run.</param>
    /// <returns>The verdict.</returns>
    public Sm83SstVectorResult Run(Sm83SstVector vector) {
        m_bus.Reset();

        foreach (var (address, value) in vector.Initial.Ram) {
            m_bus.Poke(address: address, value: value);
        }

        SeedCpuState(state: vector.Initial);

        m_cpu.StepInstruction();

        var mismatches = new List<string>(capacity: 4);

        CompareRegisters(expected: vector.Final, mismatches: mismatches);
        CompareRam(expected: vector.Final.Ram, mismatches: mismatches);
        CompareBusTrace(expectedCycles: vector.Cycles, mismatches: mismatches);

        return new Sm83SstVectorResult(Name: vector.Name, Passed: (mismatches.Count == 0), Detail: string.Join(separator: "; ", values: mismatches));
    }
    /// <inheritdoc/>
    public void Dispose() =>
        m_instance.Dispose();

    private void SeedCpuState(Sm83SstState state) =>
        // halted/haltBug/lockedUp always false: SST is single-instruction, so a vector never depends on entering the
        // harness already halted (only observable across a HALT-then-fetch pair) or wedged (illegal opcodes are
        // excluded from the corpus). interruptEnableCountdown is always 0: never nonzero in "initial" across the whole
        // shipped v1 corpus (verified).
        Sm83StateCodec.Load(
            cpu: m_cpu, scratch: m_writer,
            a: state.A, f: state.F, b: state.B, c: state.C, d: state.D, e: state.E, h: state.H, l: state.L,
            sp: state.Sp, pc: state.Pc,
            halted: false, haltBug: false, lockedUp: false, ime: (state.Ime != 0), interruptEnableCountdown: 0
        );
    private void ReadCpuTail(out bool ime, out bool eiPending) =>
        Sm83StateCodec.ReadTail(cpu: m_cpu, scratch: m_writer, buffer: m_stateBuffer, halted: out _, ime: out ime, eiPending: out eiPending);
    private void CompareRegisters(Sm83SstState expected, List<string> mismatches) {
        if (m_cpu.A != expected.A) { mismatches.Add(item: $"A={m_cpu.A:X2} want {expected.A:X2}"); }
        if (m_cpu.F != expected.F) { mismatches.Add(item: $"F={m_cpu.F:X2} want {expected.F:X2}"); }
        if (m_cpu.B != expected.B) { mismatches.Add(item: $"B={m_cpu.B:X2} want {expected.B:X2}"); }
        if (m_cpu.C != expected.C) { mismatches.Add(item: $"C={m_cpu.C:X2} want {expected.C:X2}"); }
        if (m_cpu.D != expected.D) { mismatches.Add(item: $"D={m_cpu.D:X2} want {expected.D:X2}"); }
        if (m_cpu.E != expected.E) { mismatches.Add(item: $"E={m_cpu.E:X2} want {expected.E:X2}"); }
        if (m_cpu.H != expected.H) { mismatches.Add(item: $"H={m_cpu.H:X2} want {expected.H:X2}"); }
        if (m_cpu.L != expected.L) { mismatches.Add(item: $"L={m_cpu.L:X2} want {expected.L:X2}"); }
        if (m_cpu.StackPointer != expected.Sp) { mismatches.Add(item: $"SP={m_cpu.StackPointer:X4} want {expected.Sp:X4}"); }
        if (m_cpu.ProgramCounter != expected.Pc) { mismatches.Add(item: $"PC={m_cpu.ProgramCounter:X4} want {expected.Pc:X4}"); }

        ReadCpuTail(ime: out var ime, out var eiPending);

        if (ime != (expected.Ime != 0)) { mismatches.Add(item: $"IME={(ime ? 1 : 0)} want {expected.Ime}"); }
        if (eiPending != expected.Ei) { mismatches.Add(item: $"EI-pending={eiPending} want {expected.Ei}"); }
    }
    private void CompareRam(IReadOnlyList<(ushort Address, byte Value)> expected, List<string> mismatches) {
        foreach (var (address, value) in expected) {
            var actual = m_bus.Peek(address: address);

            if (actual != value) {
                mismatches.Add(item: $"[{address:X4}]={actual:X2} want {value:X2}");
            }
        }
    }
    // Filters the vector's cycle list to the entries that actually touch the bus (flags 'r' or 'w'; an internal-only
    // "---" cycle never produces a call), then compares that sequence 1:1 against the bus's own access log. This is
    // the bus-pin activity the task asked for "if our seam exposes it cheaply" — it comes for free from wrapping
    // ISystemBus, no new core instrumentation.
    private void CompareBusTrace(IReadOnlyList<Sm83SstCycle> expectedCycles, List<string> mismatches) {
        var expectedAccesses = new List<Sm83SstCycle>(capacity: expectedCycles.Count);

        foreach (var cycle in expectedCycles) {
            if (cycle.IsRead || cycle.IsWrite) {
                expectedAccesses.Add(item: cycle);
            }
        }

        var actual = m_bus.Accesses;

        if (actual.Count != expectedAccesses.Count) {
            mismatches.Add(item: $"bus accesses={actual.Count} want {expectedAccesses.Count}");

            return;
        }

        for (var index = 0; (index < actual.Count); ++index) {
            var (address, value, isWrite) = actual[index];
            var want = expectedAccesses[index];

            if ((want.Address is not null) && (want.Address.Value != address)) {
                mismatches.Add(item: $"bus[{index}].addr={address:X4} want {want.Address.Value:X4}");
            }
            if ((want.Data is not null) && (want.Data.Value != value)) {
                mismatches.Add(item: $"bus[{index}].data={value:X2} want {want.Data.Value:X2}");
            }
            if (isWrite != want.IsWrite) {
                mismatches.Add(item: $"bus[{index}].write={isWrite} want {want.IsWrite}");
            }
        }
    }
}
