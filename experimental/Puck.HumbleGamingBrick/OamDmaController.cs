using Puck.HumbleGamingBrick.Interfaces;
using Puck.HumbleGamingBrick.Timing;

namespace Puck.HumbleGamingBrick;

/// <summary>
/// The object-attribute-memory DMA unit, a CPU-domain clocked component. A write to the DMA register arms a transfer
/// that, after a short startup delay, copies 160 bytes from the selected source page into OAM at one byte per machine
/// cycle (four CPU T-cycles). While the transfer runs the CPU cannot see OAM — the bus reads it back as <c>0xFF</c> and
/// drops writes to it (writes are dropped through the warm-up delay too) — but the DMA reads its source directly from
/// memory, on its own bus path, so it is never gated by that block. A source at or past <c>0xE000</c> folds back into
/// work RAM on a monochrome machine but reads open bus (<c>0xFF</c>) on Color. On Color the transfer also occupies its
/// source's bus, and a CPU access that collides is hijacked: reads return the DMA's bus (redirected to the cell before
/// the one in flight), writes are dropped, land on the redirected cell, or poison the OAM byte being written — the
/// oracle-measured conflict rules. A write during a running transfer restarts it after the same delay, the old transfer
/// continuing until the new one takes over. All state is plain fields captured in a fixed order.
/// </summary>
public sealed class OamDmaController : IOamDma, IClockedComponent, ISnapshotable, IModeSwitchable {
    private const int ByteCount = 0xA0;
    private const int TCyclesPerByte = 4;
    // The startup delay between the register write and the transfer taking over OAM, in CPU T-cycles (two machine
    // cycles). Derived against the hardware-verified OAM-DMA-start timing, not guessed from any reference's internals.
    private const int StartupDelayTCycles = 8;

    private readonly ICartridgeSlot m_cartridgeSlot;
    private readonly IKey1 m_key1;
    private readonly SystemMemory m_memory;
    // Mutable so a LIVE device swap re-gates the color-only DMA rules (echo-RAM source reads 0xFF, the bus-conflict
    // window). Idempotent single-field push; no boot-only model read here.
    private bool m_supportsColor;

    private bool m_active;
    private ushort m_activeBase;
    private ushort m_activeBaseUnclamped;
    private int m_delay;
    private int m_index;
    private bool m_pending;
    private ushort m_pendingBase;
    private ushort m_pendingBaseUnclamped;
    private int m_phase;
    private bool m_poisonCurrentByte;
    private byte m_register;

    /// <summary>Creates the DMA unit over the memory it copies out of and into.</summary>
    /// <param name="cartridgeSlot">The cartridge slot, a source when the page selects ROM or external RAM.</param>
    /// <param name="memory">The internal RAM, both a source (VRAM, work RAM, echo) and the OAM destination.</param>
    /// <param name="key1">The speed-switch/stop unit that freezes the transfer in stop mode.</param>
    /// <param name="configuration">The machine configuration; the bus-conflict rules are Color-only.</param>
    /// <exception cref="ArgumentNullException">Any argument is <see langword="null"/>.</exception>
    public OamDmaController(ICartridgeSlot cartridgeSlot, SystemMemory memory, IKey1 key1, MachineConfiguration configuration) {
        ArgumentNullException.ThrowIfNull(argument: cartridgeSlot);
        ArgumentNullException.ThrowIfNull(argument: memory);
        ArgumentNullException.ThrowIfNull(argument: key1);
        ArgumentNullException.ThrowIfNull(argument: configuration);

        m_cartridgeSlot = cartridgeSlot;
        m_key1 = key1;
        m_memory = memory;
        m_supportsColor = configuration.Model.SupportsColor();
    }

    /// <inheritdoc/>
    public ClockDomain Domain =>
        ClockDomain.Cpu;
    /// <inheritdoc/>
    public bool IsActive =>
        m_active;
    /// <inheritdoc/>
    public bool IsActiveOrWarmingUp =>
        (m_active || m_pending);

    /// <inheritdoc/>
    public void Tick() {
        if (m_key1.IsStopped) {
            return;
        }

        // The startup delay counts down whether or not a transfer is already running; on the tick it expires the new
        // transfer takes over. An in-flight transfer keeps running through a restart's delay, so OAM stays gated.
        if (m_pending && (--m_delay == 0)) {
            m_active = true;
            m_activeBase = m_pendingBase;
            m_activeBaseUnclamped = m_pendingBaseUnclamped;
            m_index = 0;
            m_pending = false;
            m_phase = 0;
            m_poisonCurrentByte = false;
        }

        if (!m_active) {
            return;
        }

        if (++m_phase == TCyclesPerByte) {
            m_phase = 0;

            m_memory.WriteObjectAttributeMemory(
                address: (ushort)(MemoryMap.ObjectAttributeMemoryStart + m_index),
                value: ReadSourceByte()
            );

            if (++m_index == ByteCount) {
                m_active = false;
            }
        }
    }
    /// <inheritdoc/>
    public byte ReadRegister() =>
        m_register;
    /// <inheritdoc/>
    public void WriteRegister(byte value) {
        var baseAddress = (ushort)(value << 8);

        m_register = value;
        m_delay = StartupDelayTCycles;
        m_pending = true;
        m_pendingBaseUnclamped = baseAddress;

        // A source at or past 0xE000 folds its echo bit away; what those reads return differs per model (see
        // ReadSourceByte).
        if (baseAddress >= MemoryMap.EchoRamStart) {
            baseAddress &= unchecked((ushort)~0x2000);
        }

        m_pendingBase = baseAddress;
    }
    /// <inheritdoc/>
    public bool TryReadConflict(ushort address, out bool forceOpenBus, out ushort redirect) {
        forceOpenBus = false;
        redirect = 0;

        if (!IsAddressInDmaUse(address: address)) {
            return false;
        }

        var source = CurrentSource();

        // A main-bus read while the DMA streams from the echo region sees open bus.
        if ((BusForAddress(address: address) == DmaBus.Main) && (source >= MemoryMap.EchoRamStart)) {
            forceOpenBus = true;

            return true;
        }

        // A colliding work-RAM access lands in the DMA's work-RAM cell; anything else sees the cell before the byte in
        // flight.
        redirect = ((address >= MemoryMap.WorkRamBank0Start) && ((BusForAddress(address: source) != DmaBus.Ram) || (source >= MemoryMap.EchoRamStart)))
            ? RedirectedWorkRamCell(source: source, address: address)
            : (ushort)(source - 1);

        return true;
    }
    /// <inheritdoc/>
    public OamDmaWriteConflict ClassifyWriteConflict(ushort address, out ushort target) {
        target = address;

        if (!IsAddressInDmaUse(address: address)) {
            return OamDmaWriteConflict.None;
        }

        var source = CurrentSource();

        if ((BusForAddress(address: address) == DmaBus.Main) && (source >= MemoryMap.EchoRamStart)) {
            return OamDmaWriteConflict.Drop;
        }

        if (((source < MemoryMap.WorkRamBank0Start) || (source >= MemoryMap.EchoRamStart)) && (address >= MemoryMap.WorkRamBank0Start)) {
            target = RedirectedWorkRamCell(source: source, address: address);

            return OamDmaWriteConflict.Store;
        }

        var redirect = ((source >= MemoryMap.EchoRamStart) && (address >= MemoryMap.WorkRamBank0Start))
            ? RedirectedWorkRamCell(source: source, address: address)
            : (ushort)(source - 1);

        if (redirect < MemoryMap.ExternalRamStart) {
            target = redirect;

            return OamDmaWriteConflict.StoreAndPoisonOam;
        }

        return OamDmaWriteConflict.Drop;
    }
    /// <inheritdoc/>
    public void PoisonCurrentOamByte() =>
        m_poisonCurrentByte = true;
    /// <inheritdoc/>
    public void ApplyModel(ConsoleModel model) =>
        m_supportsColor = model.SupportsColor();

    /// <inheritdoc/>
    public void SaveState(StateWriter writer) {
        writer.WriteBoolean(value: m_active);
        writer.WriteUInt16(value: m_activeBase);
        writer.WriteUInt16(value: m_activeBaseUnclamped);
        writer.WriteInt32(value: m_delay);
        writer.WriteInt32(value: m_index);
        writer.WriteBoolean(value: m_pending);
        writer.WriteUInt16(value: m_pendingBase);
        writer.WriteUInt16(value: m_pendingBaseUnclamped);
        writer.WriteInt32(value: m_phase);
        writer.WriteBoolean(value: m_poisonCurrentByte);
        writer.WriteByte(value: m_register);
    }
    /// <inheritdoc/>
    public void LoadState(StateReader reader) {
        m_active = reader.ReadBoolean();
        m_activeBase = reader.ReadUInt16();
        m_activeBaseUnclamped = reader.ReadUInt16();
        m_delay = reader.ReadInt32();
        m_index = reader.ReadInt32();
        m_pending = reader.ReadBoolean();
        m_pendingBase = reader.ReadUInt16();
        m_pendingBaseUnclamped = reader.ReadUInt16();
        m_phase = reader.ReadInt32();
        m_poisonCurrentByte = reader.ReadBoolean();
        m_register = reader.ReadByte();
    }

    // The buses the DMA and the CPU can collide on: the external/main bus (ROM + external RAM), video RAM, and work RAM.
    private enum DmaBus {
        Main,
        Ram,
        Vram,
    }

    // The source byte for the current OAM cell: open bus (all-ones) when a Color machine streams from the echo region,
    // zero when a colliding CPU write poisoned this byte, otherwise the addressed memory.
    private byte ReadSourceByte() {
        if (m_poisonCurrentByte) {
            m_poisonCurrentByte = false;

            return 0x00;
        }

        if (m_supportsColor && (m_activeBaseUnclamped >= MemoryMap.EchoRamStart)) {
            return 0xFF;
        }

        return DmaSource.Read(cartridgeSlot: m_cartridgeSlot, memory: m_memory, address: (ushort)(m_activeBase + m_index));
    }
    // The unclamped source pointer at the byte currently in flight.
    private ushort CurrentSource() =>
        (ushort)(m_activeBaseUnclamped + m_index);
    // The work-RAM cell a colliding access is steered to: the transfer's bank-select bit with the CPU's page offset.
    private static ushort RedirectedWorkRamCell(ushort source, ushort address) =>
        (ushort)(((source - 1) & 0x1000) | (address & 0xFFF) | 0xC000);
    // Whether a CPU access at the address collides with the in-flight transfer's bus: Color only, only once the first
    // byte has actually moved, never in the OAM/IO page, and never on the source cell itself.
    private bool IsAddressInDmaUse(ushort address) {
        if (!m_supportsColor || !m_active || (m_index == 0) || (address >= MemoryMap.ObjectAttributeMemoryStart)) {
            return false;
        }

        var source = CurrentSource();

        if (source == address) {
            return false;
        }

        if ((source >= MemoryMap.EchoRamStart) && ((source & ~0x2000) == address)) {
            return false;
        }

        if (address >= MemoryMap.WorkRamBank0Start) {
            return BusForAddress(address: source) != DmaBus.Vram;
        }

        if (source >= MemoryMap.EchoRamStart) {
            return BusForAddress(address: address) != DmaBus.Vram;
        }

        return BusForAddress(address: address) == BusForAddress(address: source);
    }
    private static DmaBus BusForAddress(ushort address) {
        if (address < MemoryMap.VideoRamStart) {
            return DmaBus.Main;
        }

        if (address < MemoryMap.ExternalRamStart) {
            return DmaBus.Vram;
        }

        if (address < MemoryMap.WorkRamBank0Start) {
            return DmaBus.Main;
        }

        return DmaBus.Ram;
    }
}
