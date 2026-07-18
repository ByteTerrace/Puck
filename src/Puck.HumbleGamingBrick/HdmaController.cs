using Puck.HumbleGamingBrick.Interfaces;
using Puck.HumbleGamingBrick.Timing;

namespace Puck.HumbleGamingBrick;

/// <summary>
/// The Color VRAM DMA unit, a CPU-domain clocked component that is now timed: a transfer freezes the CPU (the CPU idles
/// while <see cref="IsCpuStalled"/> holds) and moves one byte every two dots — so a sixteen-byte block costs eight
/// machine cycles at normal speed and sixteen at double speed, plus a short start-up and wind-down, matching the
/// hardware stall measured by the hardware-accurate DMA timing tests. It reads its source on its own bus path (holding the cartridge slot
/// and internal RAM directly, never the system bus, so no dependency cycle forms) and writes into the currently
/// selected VRAM bank; a source inside VRAM is invalid and reads open bus. A write to HDMA5 with bit 7 clear runs a
/// general-purpose transfer; with bit 7 set it starts an HBlank transfer that moves one block each time the PPU enters
/// mode 0, and a later bit-7-clear write stops it. The speed switch's DMA-block window and stop mode pause the unit.
/// All state is plain fields captured in a fixed order.
/// </summary>
public sealed class HdmaController : IHdma, IClockedComponent, ISnapshotable {
    private const int BlockSize = 0x10;
    private const int HBlankMode = 0;
    // The unit steps once per two dots: a step is one byte moved (or one start-up/wind-down phase). Two CPU T-cycles
    // per step at normal speed, four at double speed (the CPU T-cycle is half a dot there).
    private const int StepTCyclesNormal = 2;
    private const int StepTCyclesDouble = 4;

    // The transfer state machine, mirroring the hardware's start-up latency: a requested transfer passes through
    // Pending and Ready (one step each) before bytes move; an HBlank transfer parks in Paused between blocks.
    private const byte StateNone = 0;
    private const byte StatePaused = 5;
    private const byte StatePending = 2;
    private const byte StateReady = 3;
    private const byte StateRequested = 1;
    private const byte StateTransferring = 4;

    private readonly ICartridgeSlot m_cartridgeSlot;
    private readonly IKey1 m_key1;
    private readonly SystemMemory m_memory;
    private readonly IPpu m_ppu;
    private bool m_active;
    private bool m_allowWakeArm;
    private byte m_chunks;
    private bool m_cpuHalted;
    private ushort m_destinationCursor;
    private byte m_destinationHigh;
    private byte m_destinationLow;
    private bool m_hblankMode;
    private int m_previousMode;
    private ushort m_remainingBytes;
    private ushort m_sourceCursor;
    private byte m_sourceHigh;
    private byte m_sourceLow;
    private bool m_stallAcknowledged;
    private byte m_state;
    private int m_stepCounter;
    private bool m_windDownPending;

    /// <summary>Creates the VRAM DMA unit over the memory it copies out of and into, the PPU whose HBlank it follows,
    /// and the speed-switch/stop unit whose block windows pause it.</summary>
    /// <param name="cartridgeSlot">The cartridge slot, a source when the page selects ROM or external RAM.</param>
    /// <param name="memory">The internal RAM: a source (work RAM, echo) and the VRAM destination.</param>
    /// <param name="ppu">The PPU whose mode drives HBlank transfers.</param>
    /// <param name="key1">The speed-switch/stop unit.</param>
    /// <exception cref="ArgumentNullException">Any argument is <see langword="null"/>.</exception>
    public HdmaController(ICartridgeSlot cartridgeSlot, SystemMemory memory, IPpu ppu, IKey1 key1) {
        ArgumentNullException.ThrowIfNull(argument: cartridgeSlot);
        ArgumentNullException.ThrowIfNull(argument: memory);
        ArgumentNullException.ThrowIfNull(argument: ppu);
        ArgumentNullException.ThrowIfNull(argument: key1);

        m_cartridgeSlot = cartridgeSlot;
        m_key1 = key1;
        m_memory = memory;
        m_ppu = ppu;
        m_chunks = 0xFF;
    }

    /// <inheritdoc/>
    public ClockDomain Domain =>
        ClockDomain.Cpu;
    /// <inheritdoc/>
    public bool IsCpuStalled =>
        (((m_state != StateNone) && (m_state != StatePaused)) || m_active);
    /// <inheritdoc/>
    public bool IsTransferLocked =>
        (m_stallAcknowledged || m_active);

    /// <inheritdoc/>
    public void AcknowledgeStall() {
        m_stallAcknowledged = true;
    }
    /// <inheritdoc/>
    public void OnCpuHalted() {
        m_allowWakeArm = (m_ppu.Mode != HBlankMode);
        m_cpuHalted = true;
    }
    /// <inheritdoc/>
    public void OnCpuWoke() {
        m_cpuHalted = false;

        if ((m_state == StatePaused) && m_allowWakeArm && (m_ppu.Mode == HBlankMode)) {
            m_state = StateRequested;
        }
    }
    /// <inheritdoc/>
    public void Tick() {
        // An HBlank transfer wakes on the entry edge into mode 0. The edge is tracked every tick so pausing the unit
        // (stop mode, the speed switch's block window) cannot fabricate an edge on resume. A halted CPU keeps the unit
        // parked through the edge — whether the wake may start it instead is decided by the halt-entry mode.
        var mode = m_ppu.Mode;
        var enteredHBlank = ((mode == HBlankMode) && (m_previousMode != HBlankMode));

        m_previousMode = mode;

        if (m_key1.IsStopped || m_key1.IsHdmaBlocked) {
            return;
        }

        if ((m_state == StatePaused) && enteredHBlank && !m_cpuHalted) {
            m_state = StateRequested;
        }

        if (++m_stepCounter < (m_key1.IsDoubleSpeed ? StepTCyclesDouble : StepTCyclesNormal)) {
            return;
        }

        m_stepCounter = 0;

        Step();
    }
    /// <inheritdoc/>
    public byte ReadRegister(ushort address) {
        if (address != MemoryMap.HdmaControl) {
            return 0xFF; // HDMA1–HDMA4 are write-only.
        }

        // Bit 7 is clear while a transfer is live (including parked between HBlank blocks); the low bits are the
        // remaining block count minus one. A completed transfer reads 0xFF, a stopped one 0x80 | remaining.
        return (byte)(((m_state != StateNone) ? 0x00 : 0x80) | (m_chunks & 0x7F));
    }
    /// <inheritdoc/>
    public void WriteRegister(ushort address, byte value) {
        switch (address) {
            case MemoryMap.HdmaSourceHigh:
                m_sourceHigh = value;

                break;
            case MemoryMap.HdmaSourceLow:
                m_sourceLow = (byte)(value & 0xF0);
                m_sourceCursor = 0;

                break;
            case MemoryMap.HdmaDestinationHigh:
                m_destinationHigh = value;

                break;
            case MemoryMap.HdmaDestinationLow:
                m_destinationLow = (byte)(value & 0xF0);
                m_destinationCursor = 0;

                break;
            default:
                StartOrStop(control: value);

                break;
        }
    }
    /// <inheritdoc/>
    public void SaveState(StateWriter writer) {
        writer.WriteBoolean(value: m_active);
        writer.WriteBoolean(value: m_allowWakeArm);
        writer.WriteByte(value: m_chunks);
        writer.WriteBoolean(value: m_cpuHalted);
        writer.WriteUInt16(value: m_destinationCursor);
        writer.WriteByte(value: m_destinationHigh);
        writer.WriteByte(value: m_destinationLow);
        writer.WriteBoolean(value: m_hblankMode);
        writer.WriteInt32(value: m_previousMode);
        writer.WriteUInt16(value: m_remainingBytes);
        writer.WriteUInt16(value: m_sourceCursor);
        writer.WriteByte(value: m_sourceHigh);
        writer.WriteByte(value: m_sourceLow);
        writer.WriteBoolean(value: m_stallAcknowledged);
        writer.WriteByte(value: m_state);
        writer.WriteInt32(value: m_stepCounter);
        writer.WriteBoolean(value: m_windDownPending);
    }
    /// <inheritdoc/>
    public void LoadState(StateReader reader) {
        m_active = reader.ReadBoolean();
        m_allowWakeArm = reader.ReadBoolean();
        m_chunks = reader.ReadByte();
        m_cpuHalted = reader.ReadBoolean();
        m_destinationCursor = reader.ReadUInt16();
        m_destinationHigh = reader.ReadByte();
        m_destinationLow = reader.ReadByte();
        m_hblankMode = reader.ReadBoolean();
        m_previousMode = reader.ReadInt32();
        m_remainingBytes = reader.ReadUInt16();
        m_sourceCursor = reader.ReadUInt16();
        m_sourceHigh = reader.ReadByte();
        m_sourceLow = reader.ReadByte();
        m_stallAcknowledged = reader.ReadBoolean();
        m_state = reader.ReadByte();
        m_stepCounter = reader.ReadInt32();
        m_windDownPending = reader.ReadBoolean();
    }

    // One two-dot step of the unit: advance the start-up chain, move one byte, or wind down. The chain holds in
    // Requested until the CPU acknowledges the freeze, so a pending interrupt's dispatch runs to completion first and
    // the lead-in is measured from the CPU's own yield point — hardware only freezes the CPU at its next fetch.
    private void Step() {
        switch (m_state) {
            case StateRequested:
                if (m_stallAcknowledged) {
                    m_state = StatePending;
                }

                break;
            case StatePending:
                m_state = StateReady;

                break;
            case StateReady:
                m_state = StateTransferring;
                m_active = true;

                break;
            case StateTransferring:
                TransferByte();

                break;
            default:
                // The wind-down: the CPU stays frozen one step past the last byte of a block, then releases.
                if (m_windDownPending) {
                    m_windDownPending = false;
                    m_active = false;
                    m_stallAcknowledged = false;
                }

                break;
        }
    }
    private void TransferByte() {
        var source = (ushort)(SourceAddress() + m_sourceCursor);
        // A source inside VRAM cannot be read by the unit (it owns VRAM as its destination); those bytes read open bus.
        var invalidSource = ((source >= MemoryMap.VideoRamStart) && (source <= MemoryMap.VideoRamEnd));
        var value = (invalidSource ? (byte)0xFF : DmaSource.Read(cartridgeSlot: m_cartridgeSlot, memory: m_memory, address: source));
        var destination = (ushort)(MemoryMap.VideoRamStart | ((DestinationAddress() + m_destinationCursor) & 0x1FFF));

        m_memory.WriteVideoRam(address: destination, value: value);

        ++m_sourceCursor;
        ++m_destinationCursor;
        --m_remainingBytes;

        if ((m_remainingBytes & (BlockSize - 1)) != 0) {
            return;
        }

        // A block boundary: retire the block. A destination that wrapped past the top of VRAM aborts the transfer and
        // clears the destination registers; otherwise an HBlank transfer parks until the next mode-0 entry.
        if ((ushort)(DestinationAddress() + m_destinationCursor) == 0) {
            m_state = StateNone;
            m_remainingBytes = 0;
            m_destinationHigh = 0x00;
            m_destinationLow = 0x00;
            m_destinationCursor = 0;
        } else if (m_remainingBytes == 0) {
            m_state = StateNone;
        } else if (m_hblankMode) {
            m_state = StatePaused;
        }

        --m_chunks;

        if (m_state != StateTransferring) {
            m_windDownPending = true;
        }
    }
    private void StartOrStop(byte control) {
        var hblankMode = ((control & 0x80) != 0);

        m_chunks = (byte)(control & 0x7F);
        m_remainingBytes = (ushort)(BlockSize * (m_chunks + 1));

        if (m_state != StateNone) {
            // Bit 7 clear stops an in-flight HBlank transfer; a bit-7-set write while live is ignored.
            if (!hblankMode) {
                m_state = StateNone;
                m_stallAcknowledged = false;
            }

            return;
        }

        if (hblankMode) {
            m_hblankMode = true;
            m_state = ((m_ppu.Mode == HBlankMode) ? StateRequested : StatePaused);
        } else {
            m_hblankMode = false;
            m_state = StateRequested;
        }
    }
    private ushort SourceAddress() =>
        (ushort)((m_sourceHigh << 8) | m_sourceLow);
    private ushort DestinationAddress() =>
        (ushort)(MemoryMap.VideoRamStart | ((m_destinationHigh & 0x1F) << 8) | m_destinationLow);
}
