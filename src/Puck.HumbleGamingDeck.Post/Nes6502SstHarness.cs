using System.Text.Json;

namespace Puck.HumbleGamingDeck.Post;

/// <summary>Executes one instruction vector on a reusable CPU and flat memory bus.</summary>
internal sealed class Nes6502SstHarness {
    private readonly CpuTestMemory m_memory = new() { RequireSeededAccess = true };
    private readonly HgdCpu<Nes6502SstBus> m_cpu;

    /// <summary>Initializes a new instance of the <see cref="Nes6502SstHarness"/> class.</summary>
    public Nes6502SstHarness() {
        m_cpu = new(bus: new(memory: m_memory));
    }

    /// <summary>Compares an instruction's registers, declared memory, and each bus cycle with its vector.</summary>
    /// <param name="vector">The complete instruction vector in the pinned nes6502 schema.</param>
    /// <returns>The first disagreement, or <see langword="null"/> when every comparison matches.</returns>
    /// <exception cref="KeyNotFoundException">A required vector property is missing.</exception>
    /// <exception cref="InvalidOperationException">A vector value has the wrong JSON kind.</exception>
    /// <exception cref="FormatException">A numeric vector value is outside its register or address range.</exception>
    public string? Run(JsonElement vector) {
        var initial = vector.GetProperty(propertyName: "initial");
        var final = vector.GetProperty(propertyName: "final");
        var cycles = vector.GetProperty(propertyName: "cycles");

        m_memory.Reset();
        // The corpus declares an address in the initial list when an instruction reads it and in the final list when
        // it writes it, so an access is legal when either list names its address.
        foreach (var pair in final.GetProperty(propertyName: "ram").EnumerateArray()) {
            m_memory.Declare(address: pair[0].GetUInt16());
        }
        foreach (var pair in initial.GetProperty(propertyName: "ram").EnumerateArray()) {
            m_memory[pair[0].GetUInt16()] = pair[1].GetByte();
        }
        m_cpu.Seed(pc: initial.GetProperty(propertyName: "pc").GetUInt16(),
            a: initial.GetProperty(propertyName: "a").GetByte(), x: initial.GetProperty(propertyName: "x").GetByte(),
            y: initial.GetProperty(propertyName: "y").GetByte(), p: initial.GetProperty(propertyName: "p").GetByte(),
            s: initial.GetProperty(propertyName: "s").GetByte());
        var seedError = CompareRegisters(state: initial);

        if (seedError is not null) {
            return ("initial " + seedError);
        }
        var index = 0;

        foreach (var cycle in cycles.EnumerateArray()) {
            if ((index > 0) && m_cpu.AtInstructionBoundary) {
                return $"instruction ended after {index} cycles; expected {cycles.GetArrayLength()}";
            }
            m_cpu.StepCycle();
            if (m_memory.Accesses.Count != (index + 1)) {
                return $"cycle {index}: CPU did not perform exactly one access";
            }
            var actual = m_memory.Accesses[index];
            var expected = new CpuAccess(Address: cycle[0].GetUInt16(), Value: cycle[1].GetByte(), Write: (cycle[2].GetString() == "write"));

            if (actual != expected) {
                return $"cycle {index}: ${actual.Address:X4} ${actual.Value:X2} {(actual.Write ? "write" : "read")}; expected ${expected.Address:X4} ${expected.Value:X2} {(expected.Write ? "write" : "read")}";
            }
            ++index;
        }
        if (!m_cpu.AtInstructionBoundary && !m_cpu.IsJammed) {
            return $"instruction did not finish within {index} expected cycles";
        }
        if (m_memory.UnseededAccess) {
            return "CPU accessed RAM not declared in either the initial or final vector";
        }
        var registerError = CompareRegisters(state: final);

        if (registerError is not null) {
            return ("final " + registerError);
        }
        foreach (var pair in final.GetProperty(propertyName: "ram").EnumerateArray()) {
            var address = pair[0].GetUInt16();
            var expected = pair[1].GetByte();

            if (m_memory[address] != expected) {
                return $"RAM[${address:X4}]=${m_memory[address]:X2}, expected ${expected:X2}";
            }
        }

        return null;
    }

    // Bit 4 of a pushed status byte is the B flag, but the status register has no bit 4 of its own; some vectors seed
    // and expect it set, so the comparison ignores it. A push still has to drive it, which the bus trace checks.
    private const int BreakBit = 0x10;

    private string? CompareRegisters(JsonElement state) {
        if ((m_cpu.ProgramCounter != state.GetProperty(propertyName: "pc").GetUInt16()) ||
            (m_cpu.A != state.GetProperty(propertyName: "a").GetByte()) ||
            (m_cpu.X != state.GetProperty(propertyName: "x").GetByte()) ||
            (m_cpu.Y != state.GetProperty(propertyName: "y").GetByte()) ||
            ((m_cpu.P & ~BreakBit) != (state.GetProperty(propertyName: "p").GetByte() & ~BreakBit)) ||
            (m_cpu.S != state.GetProperty(propertyName: "s").GetByte())) {
            return $"registers PC={m_cpu.ProgramCounter:X4} A={m_cpu.A:X2} X={m_cpu.X:X2} Y={m_cpu.Y:X2} P={m_cpu.P:X2} S={m_cpu.S:X2}; expected {state.GetRawText()}";
        }

        return null;
    }
}
