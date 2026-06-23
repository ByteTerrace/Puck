namespace Puck.GameBoy.Conformance;

/// <summary>
/// A flat 64&#160;KiB RAM bus for driving the CPU in isolation: every address is plain readable/writable memory
/// with no decode, banking, or components. It counts machine cycles so timing can be asserted, and it is the
/// memory model the per-opcode SingleStepTests vectors assume.
/// </summary>
internal sealed class FlatRamBus : ICpuBus {
    private readonly byte[] m_memory = new byte[0x10000];
    private readonly InterruptController m_interrupts = new();

    public InterruptController Interrupts =>
        m_interrupts;
    public long MachineCycles { get; private set; }

    public byte this[int address] {
        get => m_memory[address];
        set => m_memory[address] = value;
    }

    public void LoadProgram(int address, params byte[] bytes) =>
        bytes.CopyTo(
            array: m_memory,
            index: address
        );

    public byte ReadCycle(ushort address) {
        MachineCycles += 1;

        return m_memory[address];
    }
    public void WriteCycle(ushort address, byte value) {
        MachineCycles += 1;
        m_memory[address] = value;
    }
    public void InternalCycle() =>
        MachineCycles += 1;
    public void TriggerOamBug(ushort address, bool isWrite) {
        // The flat test bus has no PPU, so there is no OAM corruption to model.
    }
    public void FlushPendingCycles() {
        // The flat test bus counts each access immediately and ticks no peripherals, so there is nothing to defer.
    }
    public bool ApplyPreparedSpeedSwitch() =>
        false;
}
