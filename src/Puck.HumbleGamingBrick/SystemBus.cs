using Puck.HumbleGamingBrick.Interfaces;

namespace Puck.HumbleGamingBrick;

/// <summary>
/// The concrete address decoder. It owns no memory of its own beyond the I/O register page that no dedicated component
/// has claimed yet; everything else routes to the cartridge, the internal RAM, or the interrupt controller. As real
/// components come online they take over their register ranges from the fallback page.
/// </summary>
public sealed class SystemBus : ISystemBus, ISnapshotable, IModeSwitchable {
    // The boot overlay's read windows: every model maps the first 256 bytes; Color additionally maps 0x200-0x8FF,
    // leaving the cartridge header visible through the 0x100-0x1FF hole.
    private const ushort BootRomLowEnd = 0x00FF;
    private const ushort CgbBootRomHighEnd = 0x08FF;
    private const ushort CgbBootRomHighStart = 0x0200;
    // The two ROM windows' byte spans (0x0000-0x3FFF and 0x4000-0x7FFF, each 16 KiB) the derived cache resolves.
    private const int RomWindowByteCount = 0x4000;

    private readonly IApu m_apu;
    private readonly byte[]? m_bootRom;
    private readonly ICartridgeSlot m_cartridgeSlot;
    private readonly IHdma m_hdma;
    private readonly IInfrared m_infrared;
    private readonly IInterruptController m_interrupts;
    private readonly byte[] m_ioRegisters;
    private readonly IJoypad m_joypad;
    private readonly IKey1 m_key1;
    private readonly SystemMemory m_memory;
    private readonly IOamDma m_oamDma;
    private readonly IPpu m_ppu;
    private readonly ISerial m_serial;
    // Mutable so a LIVE device swap re-gates the Color I/O page: with this false, every color register write (palette
    // RAM, KEY1, HDMA, VRAM/WRAM bank selects, PCM ports) is already dropped by the existing `if (m_supportsColor)`
    // guards and reads return 0xFF — sealing off Color hardware after a demote with no per-register change.
    private bool m_supportsColor;
    private readonly ITimer m_timer;

    // The FF50 latch: the boot ROM overlay is readable until the first nonzero write, which unmaps it for the life of
    // the machine (only a reset — a fresh machine — brings it back). A machine configured without a boot ROM starts
    // with the latch already tripped, so the seeded post-boot path reads FF50 exactly as hardware does after boot.
    private bool m_bootRomMapped;

    // The derived cartridge-window cache (F2): ROM fetch — the dominant bus traffic — and the pure-array-access
    // mappers' RAM window are resolved once per control write instead of chasing the slot property + mapper virtual
    // dispatch on every byte. NEVER serialized: RefreshCartridgeWindowCache rebuilds it from the just-loaded mapper
    // registers at construction, after every control write, and everywhere ApplyModel already re-derives capability
    // gates (a live model swap, a snapshot restore). A -1 offset (ROM) or a zero length (RAM) means the window is not
    // a plain array offset this cartridge/state, so reads and writes fall back to the interface path.
    private byte[] m_ramImage = [];
    private int m_ramWindowLength;
    private int m_ramWindowOffset;
    private byte[] m_romImage = [];
    private int m_romBank0Offset = -1;
    private int m_romBankNOffset = -1;

    /// <summary>Assembles the bus from the devices it routes to.</summary>
    /// <param name="apu">The audio processing unit backing NR10–NR52 and wave RAM.</param>
    /// <param name="cartridgeSlot">The cartridge slot the bus reads the current cartridge through.</param>
    /// <param name="hdma">The Color VRAM DMA unit backing HDMA1–HDMA5.</param>
    /// <param name="infrared">The infrared transceiver backing the Color RP register.</param>
    /// <param name="interrupts">The interrupt controller backing IF and IE.</param>
    /// <param name="joypad">The joypad backing the P1/JOYP register.</param>
    /// <param name="key1">The Color speed-switch backing the KEY1 register.</param>
    /// <param name="memory">The internal RAM.</param>
    /// <param name="oamDma">The OAM DMA unit backing the DMA register and gating OAM while a transfer runs.</param>
    /// <param name="ppu">The picture processing unit backing the LCDC, STAT, LY, and LYC registers.</param>
    /// <param name="serial">The serial port backing SB and SC.</param>
    /// <param name="timer">The divider/timer block backing DIV, TIMA, TMA, and TAC.</param>
    /// <param name="configuration">The machine configuration, which gates Color-only registers.</param>
    /// <exception cref="ArgumentNullException">Any argument is <see langword="null"/>.</exception>
    public SystemBus(IApu apu, ICartridgeSlot cartridgeSlot, IHdma hdma, IInfrared infrared, IInterruptController interrupts, IJoypad joypad, IKey1 key1, SystemMemory memory, IOamDma oamDma, IPpu ppu, ISerial serial, ITimer timer, MachineConfiguration configuration) {
        ArgumentNullException.ThrowIfNull(argument: apu);
        ArgumentNullException.ThrowIfNull(argument: cartridgeSlot);
        ArgumentNullException.ThrowIfNull(argument: hdma);
        ArgumentNullException.ThrowIfNull(argument: infrared);
        ArgumentNullException.ThrowIfNull(argument: interrupts);
        ArgumentNullException.ThrowIfNull(argument: joypad);
        ArgumentNullException.ThrowIfNull(argument: key1);
        ArgumentNullException.ThrowIfNull(argument: memory);
        ArgumentNullException.ThrowIfNull(argument: oamDma);
        ArgumentNullException.ThrowIfNull(argument: ppu);
        ArgumentNullException.ThrowIfNull(argument: serial);
        ArgumentNullException.ThrowIfNull(argument: timer);
        ArgumentNullException.ThrowIfNull(argument: configuration);

        m_apu = apu;
        m_bootRom = configuration.BootRom;
        m_bootRomMapped = (configuration.BootRom is not null);
        m_cartridgeSlot = cartridgeSlot;
        m_hdma = hdma;
        m_infrared = infrared;
        m_interrupts = interrupts;
        m_ioRegisters = new byte[((MemoryMap.IoRegistersEnd - MemoryMap.IoRegistersStart) + 1)];
        m_joypad = joypad;
        m_key1 = key1;
        m_memory = memory;
        m_oamDma = oamDma;
        m_ppu = ppu;
        m_serial = serial;
        m_supportsColor = configuration.Model.SupportsColor();
        m_timer = timer;

        Array.Fill(array: m_ioRegisters, value: (byte)0xFF);

        RefreshCartridgeWindowCache();
    }

    /// <inheritdoc/>
    public byte ReadByte(ushort address) {
        // Debug read watchpoints: dormant (one predicted-not-taken field test) until a hgb.watch arms one, so the hot
        // fetch path is unchanged when nothing is armed (the ThroughputStage / zero-alloc gates prove it).
        if (m_watchArmed) {
            TrackWatchRead(address: address);
        }

        // On Color, a running OAM DMA occupies its source's bus; a CPU read that collides is hijacked — it sees open
        // bus or the DMA's own bus (read on the DMA's path, so the redirect cannot recurse).
        if (m_oamDma.TryReadConflict(address: address, forceOpenBus: out var forceOpenBus, redirect: out var redirect)) {
            return (forceOpenBus ? (byte)0xFF : DmaSource.Read(cartridgeSlot: m_cartridgeSlot, memory: m_memory, address: redirect));
        }

        if (address <= MemoryMap.RomBankNEnd) {
            // The boot overlay is read-only: reads inside its windows come from the image; writes always pass through
            // to the cartridge below, exactly as if the overlay were not there.
            if (IsBootRomAddress(address: address)) {
                return m_bootRom![address];
            }

            // The banked ROM windows are cached as a direct array + offset, refreshed only on the rare mapper control
            // write (RefreshCartridgeWindowCache) instead of re-resolving the bank through the slot property and the
            // mapper's virtual dispatch on every fetch — ROM is the dominant bus traffic and the hottest read on the
            // bus. A -1 offset means this cartridge/bank combination was not cacheable (out-of-range image), so the
            // read falls back to the interface path, still bit-identical.
            return ((address <= MemoryMap.RomBank0End)
                ? ((m_romBank0Offset >= 0) ? m_romImage[(m_romBank0Offset + address)] : m_cartridgeSlot.Cartridge.ReadRom(address: address))
                : ((m_romBankNOffset >= 0) ? m_romImage[(m_romBankNOffset + (address - MemoryMap.RomBankNStart))] : m_cartridgeSlot.Cartridge.ReadRom(address: address)));
        }

        if (address <= MemoryMap.VideoRamEnd) {
            // The PPU owns VRAM during drawing (mode 3), so the CPU reads open bus there; the release trails the
            // internal mode-0 edge by the PPU's unlock lag.
            return (m_ppu.BlocksVideoRamReads ? (byte)0xFF : m_memory.ReadVideoRam(address: address));
        }

        if (address <= MemoryMap.ExternalRamEnd) {
            // Cached the same way as ROM, but only for a mapper whose window is pure array access (RomOnly, MBC1,
            // MBC5, MMM01 — TryComputeRamWindow); a mode-selected or side-effectful window (RTC register, EEPROM,
            // camera, IR) always reports zero length here and stays on the interface path.
            var ramRelative = (address - MemoryMap.ExternalRamStart);

            return ((ramRelative < m_ramWindowLength)
                ? m_ramImage[(m_ramWindowOffset + ramRelative)]
                : m_cartridgeSlot.Cartridge.ReadRam(address: address));
        }

        if (address <= MemoryMap.WorkRamBankNEnd) {
            return m_memory.ReadWorkRam(address: address);
        }

        if (address <= MemoryMap.EchoRamEnd) {
            return m_memory.ReadWorkRam(address: (ushort)(address - MemoryMap.EchoRamMirrorOffset));
        }

        if (address <= MemoryMap.ObjectAttributeMemoryEnd) {
            // OAM is unreadable to the CPU while a DMA copies into it, or while the PPU holds its read lock (the scan,
            // drawing, and the trailing unlock lag).
            return ((m_oamDma.IsActive || m_ppu.BlocksOamReads) ? (byte)0xFF : m_memory.ReadObjectAttributeMemory(address: address));
        }

        if (address <= MemoryMap.UnusableEnd) {
            return 0xFF;
        }

        if (address <= MemoryMap.IoRegistersEnd) {
            if (IsAudioBlock(address: address)) {
                return m_apu.ReadRegister(address: address);
            }

            // The Color-only PCM output registers route to the APU; on a DMG they read as open bus.
            if ((address == MemoryMap.PcmAmplitude12) || (address == MemoryMap.PcmAmplitude34)) {
                return (m_supportsColor ? m_apu.ReadPcm(address: address) : (byte)0xFF);
            }

            return ReadIoRegister(address: address);
        }

        if (address <= MemoryMap.HighRamEnd) {
            return m_memory.ReadHighRam(address: address);
        }

        return (byte)m_interrupts.Enabled;
    }
    /// <inheritdoc/>
    public void WriteByte(ushort address, byte value) {
        // Debug write watchpoints: dormant until a hgb.watch arms one (the read side documents the zero-cost guard).
        if (m_watchArmed) {
            TrackWatchWrite(address: address, value: value);
        }

        // On Color, a CPU write that collides with a running OAM DMA's bus is dropped, lands on the DMA's bus instead,
        // or additionally zeroes the OAM byte in flight. The redirected store goes straight to memory — the target is
        // the DMA's own bus, which by construction is not re-classified.
        switch (m_oamDma.ClassifyWriteConflict(address: address, target: out var conflictTarget)) {
            case OamDmaWriteConflict.Drop:
                return;
            case OamDmaWriteConflict.Store:
                WriteConflictTarget(address: conflictTarget, value: value);

                return;
            case OamDmaWriteConflict.StoreAndPoisonOam:
                WriteConflictTarget(address: conflictTarget, value: value);
                m_oamDma.PoisonCurrentOamByte();

                return;
            default:
                break;
        }

        if (address <= MemoryMap.RomBankNEnd) {
            // A control write is the ONLY thing that can move a bank (every mapper's registers, including MBC1's mode
            // switch and MMM01's lock/multiplex bits, are only reachable through this call), so it uniformly refreshes
            // the derived window cache — no per-mapper "does this register affect banking" tracking needed, and a
            // future mapper gets the invalidation for free.
            m_cartridgeSlot.Cartridge.WriteControl(address: address, value: value);
            RefreshCartridgeWindowCache();
        } else if (address <= MemoryMap.VideoRamEnd) {
            // Writes are dropped while the PPU owns VRAM (drawing plus the trailing unlock lag).
            if (!m_ppu.BlocksVideoRamWrites) {
                m_memory.WriteVideoRam(address: address, value: value);
            }
        } else if (address <= MemoryMap.ExternalRamEnd) {
            var ramRelative = (address - MemoryMap.ExternalRamStart);

            if (ramRelative < m_ramWindowLength) {
                m_ramImage[(m_ramWindowOffset + ramRelative)] = value;
                m_cartridgeSlot.Cartridge.MarkExternalRamDirty();
            } else {
                m_cartridgeSlot.Cartridge.WriteRam(address: address, value: value);
            }
        } else if (address <= MemoryMap.WorkRamBankNEnd) {
            m_memory.WriteWorkRam(address: address, value: value);
        } else if (address <= MemoryMap.EchoRamEnd) {
            m_memory.WriteWorkRam(address: (ushort)(address - MemoryMap.EchoRamMirrorOffset), value: value);
        } else if (address <= MemoryMap.ObjectAttributeMemoryEnd) {
            // The CPU cannot reach OAM while a DMA transfer owns it — including the transfer's warm-up delay — or
            // while the PPU holds its write lock (which briefly opens between the scan and the pipeline engaging).
            if (!m_oamDma.IsActiveOrWarmingUp && !m_ppu.BlocksOamWrites) {
                m_memory.WriteObjectAttributeMemory(address: address, value: value);
            }
        } else if (address <= MemoryMap.UnusableEnd) {
            // The unusable region drops writes.
        } else if (address <= MemoryMap.IoRegistersEnd) {
            if (IsAudioBlock(address: address)) {
                m_apu.WriteRegister(address: address, value: value);
            } else {
                WriteIoRegister(address: address, value: value);
            }
        } else if (address <= MemoryMap.HighRamEnd) {
            m_memory.WriteHighRam(address: address, value: value);
        } else {
            m_interrupts.Enabled = (InterruptKind)value;
        }
    }
    /// <inheritdoc/>
    public void ApplyModel(ConsoleModel model) {
        m_supportsColor = model.SupportsColor();

        // ApplyModel runs at exactly the two points the mapper's own registers can have just changed underneath the
        // cache without a WriteControl call passing through this bus: a snapshot restore (after every component,
        // including the cartridge slot, has loaded its bytes) and a live model swap. Rebuilding here — rather than
        // trying to catch every such call site individually — keeps the cache correct by construction.
        RefreshCartridgeWindowCache();
    }

    /// <summary>Reads one byte from anywhere in the bus address space WITHOUT the side effects of a live fetch — no
    /// clock advance, no OAM-DMA conflict tracking, and none of the PPU/DMA lock masking that returns open bus during a
    /// live access: it shows the true byte a region holds (RAM/ROM/OAM/HRAM as stored, I/O through the register getters).
    /// The side-effect-free read behind <c>hgb.peek</c> / <c>screen.peek</c> and the debug disassembler's byte source.</summary>
    /// <param name="address">The 16-bit bus address.</param>
    /// <returns>The byte the region holds.</returns>
    public byte DebugReadByte(ushort address) {
        if (address <= MemoryMap.RomBankNEnd) {
            if (IsBootRomAddress(address: address)) {
                return m_bootRom![address];
            }

            return ((address <= MemoryMap.RomBank0End)
                ? ((m_romBank0Offset >= 0) ? m_romImage[(m_romBank0Offset + address)] : m_cartridgeSlot.Cartridge.ReadRom(address: address))
                : ((m_romBankNOffset >= 0) ? m_romImage[(m_romBankNOffset + (address - MemoryMap.RomBankNStart))] : m_cartridgeSlot.Cartridge.ReadRom(address: address)));
        }

        if (address <= MemoryMap.VideoRamEnd) {
            return m_memory.ReadVideoRam(address: address);
        }

        if (address <= MemoryMap.ExternalRamEnd) {
            var ramRelative = (address - MemoryMap.ExternalRamStart);

            return ((ramRelative < m_ramWindowLength)
                ? m_ramImage[(m_ramWindowOffset + ramRelative)]
                : m_cartridgeSlot.Cartridge.ReadRam(address: address));
        }

        if (address <= MemoryMap.WorkRamBankNEnd) {
            return m_memory.ReadWorkRam(address: address);
        }

        if (address <= MemoryMap.EchoRamEnd) {
            return m_memory.ReadWorkRam(address: (ushort)(address - MemoryMap.EchoRamMirrorOffset));
        }

        if (address <= MemoryMap.ObjectAttributeMemoryEnd) {
            return m_memory.ReadObjectAttributeMemory(address: address);
        }

        if (address <= MemoryMap.UnusableEnd) {
            return 0xFF;
        }

        if (address <= MemoryMap.IoRegistersEnd) {
            if (IsAudioBlock(address: address)) {
                return m_apu.ReadRegister(address: address);
            }

            if ((address == MemoryMap.PcmAmplitude12) || (address == MemoryMap.PcmAmplitude34)) {
                return (m_supportsColor ? m_apu.ReadPcm(address: address) : (byte)0xFF);
            }

            return ReadIoRegister(address: address);
        }

        if (address <= MemoryMap.HighRamEnd) {
            return m_memory.ReadHighRam(address: address);
        }

        return (byte)m_interrupts.Enabled;
    }

    /// <summary>Forces one byte into a WRITABLE bus region — the debug MUTATION behind <c>hgb.poke</c>, outside the
    /// replay-determinism contract. RAM regions (VRAM, external RAM, work RAM + echo, OAM, high RAM) and the IE latch
    /// take the value; the ROM-region mapper-control window and the I/O page are refused (a memory poke must not drive
    /// banking or trip a hardware register). A caller that pokes must drop any captured rewind/replay history.</summary>
    /// <param name="address">The 16-bit bus address.</param>
    /// <param name="value">The byte to store.</param>
    public void DebugWriteByte(ushort address, byte value) {
        if (address <= MemoryMap.RomBankNEnd) {
            return;
        }

        if (address <= MemoryMap.VideoRamEnd) {
            m_memory.WriteVideoRam(address: address, value: value);
        } else if (address <= MemoryMap.ExternalRamEnd) {
            var ramRelative = (address - MemoryMap.ExternalRamStart);

            if (ramRelative < m_ramWindowLength) {
                m_ramImage[(m_ramWindowOffset + ramRelative)] = value;
                m_cartridgeSlot.Cartridge.MarkExternalRamDirty();
            } else {
                m_cartridgeSlot.Cartridge.WriteRam(address: address, value: value);
            }
        } else if (address <= MemoryMap.WorkRamBankNEnd) {
            m_memory.WriteWorkRam(address: address, value: value);
        } else if (address <= MemoryMap.EchoRamEnd) {
            m_memory.WriteWorkRam(address: (ushort)(address - MemoryMap.EchoRamMirrorOffset), value: value);
        } else if (address <= MemoryMap.ObjectAttributeMemoryEnd) {
            m_memory.WriteObjectAttributeMemory(address: address, value: value);
        } else if (address <= MemoryMap.UnusableEnd) {
            // Dropped: the unusable region is not memory.
        } else if (address <= MemoryMap.IoRegistersEnd) {
            // Refused: an I/O poke would drive hardware, not memory.
        } else if (address <= MemoryMap.HighRamEnd) {
            m_memory.WriteHighRam(address: address, value: value);
        } else {
            m_interrupts.Enabled = (InterruptKind)value;
        }
    }

    // ---- Debug watchpoints ------------------------------------------------------------------------------------------
    // Host-side debug state, NEVER serialized (excluded from every snapshot, exactly like the AGB bus's DebugRead peeks):
    // a poke/rewind never carries them, and their presence cannot perturb the simulation. m_watchArmed is the single
    // dormant guard the hot ReadByte/WriteByte paths test; when false (the default, and the batteries' every run) the
    // watch machinery is untouched. A hit latches ONE pending record (first hit wins until drained) so the host can
    // report PC + access + value and pause the cabinet.
    private bool m_watchArmed;
    private readonly List<(ushort Address, bool Read, bool Write)> m_watches = [];
    private bool m_watchHit;
    private ushort m_watchHitAddress;
    private byte m_watchHitValue;
    private bool m_watchHitWrite;
    private ushort m_watchHitPc;
    // The CURRENT instruction dispatch's start PC (M-06): the CPU calls NoteInstructionStart once per StepInstruction,
    // before any access that dispatch makes, so a mid-instruction watch hit latches the PC of the instruction actually
    // making the access rather than whatever the CPU's live PC has advanced to by drain time.
    private ushort m_currentInstructionPc;

    /// <inheritdoc/>
    public void NoteInstructionStart(ushort pc) =>
        m_currentInstructionPc = pc;

    /// <summary>Arms (or re-arms, replacing the same address's kinds) a read/write watchpoint. Dormant until the first
    /// arm flips the hot-path guard on.</summary>
    /// <param name="address">The watched bus address.</param>
    /// <param name="read">Whether a read of the address fires the watch.</param>
    /// <param name="write">Whether a write to the address fires the watch.</param>
    public void AddWatch(ushort address, bool read, bool write) {
        for (var index = 0; (index < m_watches.Count); ++index) {
            if (m_watches[index].Address == address) {
                m_watches[index] = (address, read, write);
                m_watchArmed = true;

                return;
            }
        }

        m_watches.Add(item: (address, read, write));
        m_watchArmed = true;
    }

    /// <summary>Clears every watchpoint and returns the hot path to its dormant (zero-cost) state.</summary>
    public void ClearWatches() {
        m_watches.Clear();
        m_watchArmed = false;
        m_watchHit = false;
    }

    /// <summary>Gets the number of armed watchpoints.</summary>
    public int WatchCount => m_watches.Count;

    /// <summary>Describes the armed watchpoints as <c>0xADDR:kind</c> tokens (kind = r/w/rw), for <c>hgb.watch.list</c>.</summary>
    /// <returns>A space-joined description, or an empty string when none are armed.</returns>
    public string DescribeWatches() {
        if (m_watches.Count == 0) {
            return "";
        }

        var parts = new string[m_watches.Count];

        for (var index = 0; (index < m_watches.Count); ++index) {
            var watch = m_watches[index];

            parts[index] = $"0x{watch.Address:X4}:{(watch.Read ? "r" : "")}{(watch.Write ? "w" : "")}";
        }

        return string.Join(separator: ' ', values: parts);
    }

    /// <summary>Takes the one pending watch hit (if any), reporting its address, the byte, whether it was a write, and
    /// the accessing instruction's PC. Clears the pending slot so a subsequent access can latch the next hit.</summary>
    /// <param name="address">The hit address.</param>
    /// <param name="value">The byte read or written.</param>
    /// <param name="isWrite">Whether the hit was a write (else a read).</param>
    /// <param name="pc">The program counter of the instruction that made the access.</param>
    /// <returns>Whether a hit was pending.</returns>
    public bool TryTakeWatchHit(out ushort address, out byte value, out bool isWrite, out ushort pc) {
        if (!m_watchHit) {
            address = 0;
            value = 0;
            isWrite = false;
            pc = 0;

            return false;
        }

        m_watchHit = false;
        address = m_watchHitAddress;
        value = m_watchHitValue;
        isWrite = m_watchHitWrite;
        pc = m_watchHitPc;

        return true;
    }

    private void TrackWatchRead(ushort address) {
        if (m_watchHit) {
            return;
        }

        foreach (var watch in m_watches) {
            if (watch.Read && (watch.Address == address)) {
                m_watchHit = true;
                m_watchHitAddress = address;
                m_watchHitValue = DebugReadByte(address: address);
                m_watchHitWrite = false;
                m_watchHitPc = m_currentInstructionPc;

                return;
            }
        }
    }

    private void TrackWatchWrite(ushort address, byte value) {
        if (m_watchHit) {
            return;
        }

        foreach (var watch in m_watches) {
            if (watch.Write && (watch.Address == address)) {
                m_watchHit = true;
                m_watchHitAddress = address;
                m_watchHitValue = value;
                m_watchHitWrite = true;
                m_watchHitPc = m_currentInstructionPc;

                return;
            }
        }
    }

    /// <inheritdoc/>
    public void SaveState(StateWriter writer) {
        writer.WriteBytes(value: m_ioRegisters);
        writer.WriteBoolean(value: m_bootRomMapped);
    }
    /// <inheritdoc/>
    public void LoadState(StateReader reader) {
        reader.ReadBytes(destination: m_ioRegisters);
        m_bootRomMapped = reader.ReadBoolean();
    }

    // Whether an address falls inside the boot overlay's read windows while it is still mapped. The image itself is
    // immutable configuration; only the FF50 latch is machine state.
    private bool IsBootRomAddress(ushort address) {
        if (!m_bootRomMapped || (m_bootRom is null)) {
            return false;
        }

        return ((address <= BootRomLowEnd)
            || (m_supportsColor && (address >= CgbBootRomHighStart) && (address <= CgbBootRomHighEnd)));
    }

    // Land a conflict-redirected store directly on the DMA's bus: the mapper for the ROM region, then VRAM (still
    // subject to the PPU's drawing lock), external RAM, and work RAM with its echo fold.
    private void WriteConflictTarget(ushort address, byte value) {
        if (address <= MemoryMap.RomBankNEnd) {
            m_cartridgeSlot.Cartridge.WriteControl(address: address, value: value);
            RefreshCartridgeWindowCache();
        } else if (address <= MemoryMap.VideoRamEnd) {
            if (!m_ppu.BlocksVideoRamWrites) {
                m_memory.WriteVideoRam(address: address, value: value);
            }
        } else if (address <= MemoryMap.ExternalRamEnd) {
            var ramRelative = (address - MemoryMap.ExternalRamStart);

            if (ramRelative < m_ramWindowLength) {
                m_ramImage[(m_ramWindowOffset + ramRelative)] = value;
                m_cartridgeSlot.Cartridge.MarkExternalRamDirty();
            } else {
                m_cartridgeSlot.Cartridge.WriteRam(address: address, value: value);
            }
        } else if (address <= MemoryMap.WorkRamBankNEnd) {
            m_memory.WriteWorkRam(address: address, value: value);
        } else {
            m_memory.WriteWorkRam(address: (ushort)(address - MemoryMap.EchoRamMirrorOffset), value: value);
        }
    }
    // Recomputes the derived ROM/RAM window cache from the currently-inserted cartridge's live bank registers. A
    // window is used only when it stays within the cartridge's actual image/RAM bounds (RomImage.Length / RamImage
    // slice) — an out-of-range result (a malformed or non-bank-aligned image) falls back to the interface path rather
    // than indexing past the array, so the fast path never trades correctness for speed.
    private void RefreshCartridgeWindowCache() {
        var cartridge = m_cartridgeSlot.Cartridge;

        m_romImage = cartridge.RomImage;

        cartridge.ComputeRomWindows(bank0Offset: out var bank0Offset, bankNOffset: out var bankNOffset);

        var romLength = m_romImage.Length;

        m_romBank0Offset = (((bank0Offset >= 0) && ((bank0Offset + RomWindowByteCount) <= romLength)) ? bank0Offset : -1);
        m_romBankNOffset = (((bankNOffset >= 0) && ((bankNOffset + RomWindowByteCount) <= romLength)) ? bankNOffset : -1);

        m_ramImage = cartridge.RamImage;

        if (cartridge.TryComputeRamWindow(offset: out var ramOffset, length: out var ramLength)) {
            m_ramWindowOffset = ramOffset;
            m_ramWindowLength = ramLength;
        } else {
            m_ramWindowOffset = 0;
            m_ramWindowLength = 0;
        }
    }

    // The audio registers and wave RAM form one contiguous block the APU owns end to end.
    private static bool IsAudioBlock(ushort address) =>
        ((address >= MemoryMap.AudioStart) && (address <= MemoryMap.WaveRamEnd));
    private byte ReadIoRegister(ushort address) {
        switch (address) {
            case MemoryMap.Joypad:
                return m_joypad.ReadRegister();
            case MemoryMap.SerialData:
            case MemoryMap.SerialControl:
                return m_serial.ReadRegister(address: address);
            case MemoryMap.Divider:
            case MemoryMap.TimerCounter:
            case MemoryMap.TimerModulo:
            case MemoryMap.TimerControl:
                return m_timer.ReadRegister(address: address);
            case MemoryMap.OamDmaSource:
                return m_oamDma.ReadRegister();
            case MemoryMap.LcdControl:
            case MemoryMap.LcdStatus:
            case MemoryMap.ScrollY:
            case MemoryMap.ScrollX:
            case MemoryMap.LcdY:
            case MemoryMap.LcdYCompare:
            case MemoryMap.BackgroundPalette:
            case MemoryMap.ObjectPalette0:
            case MemoryMap.ObjectPalette1:
            case MemoryMap.WindowY:
            case MemoryMap.WindowX:
                return m_ppu.ReadRegister(address: address);
            case MemoryMap.InterruptFlag:
                return (byte)(0xE0 | (byte)m_interrupts.Requested);
            case MemoryMap.BootRomDisable:
                // Bit 0 is the latch (set once the overlay is gone); the undecoded bits read high.
                return (byte)(0xFE | (m_bootRomMapped ? 0x00 : 0x01));
            default:
                return ReadColorIoRegister(address: address);
        }
    }
    // The Color-only register page: everything here reads open bus (0xFF) on a monochrome machine, as does any
    // unmapped I/O address on either model, regardless of any write that landed there.
    private byte ReadColorIoRegister(ushort address) {
        if (!m_supportsColor) {
            return 0xFF;
        }

        switch (address) {
            case MemoryMap.BackgroundColorPaletteIndex:
            case MemoryMap.BackgroundColorPaletteData:
            case MemoryMap.ObjectColorPaletteIndex:
            case MemoryMap.ObjectColorPaletteData:
                return m_ppu.ReadRegister(address: address);
            case MemoryMap.SpeedSwitch:
                return m_key1.ReadRegister();
            case MemoryMap.HdmaSourceHigh:
            case MemoryMap.HdmaSourceLow:
            case MemoryMap.HdmaDestinationHigh:
            case MemoryMap.HdmaDestinationLow:
            case MemoryMap.HdmaControl:
                return m_hdma.ReadRegister(address: address);
            case MemoryMap.InfraredPort:
                return m_infrared.ReadRegister();
            case MemoryMap.VramBankSelect:
                return (byte)(0xFE | m_memory.VideoRamBank);
            case MemoryMap.WorkRamBankSelect:
                return (byte)(0xF8 | m_memory.WorkRamBank);
            case 0xFF72:
            case 0xFF73:
            case 0xFF74:
                // The Color's undocumented fully-readable/writable registers.
                return m_ioRegisters[(address - MemoryMap.IoRegistersStart)];
            case 0xFF75:
                // Only bits 4-6 are backed; the rest read as ones.
                return (byte)(0x8F | m_ioRegisters[(address - MemoryMap.IoRegistersStart)]);
            default:
                return 0xFF;
        }
    }
    private void WriteIoRegister(ushort address, byte value) {
        switch (address) {
            case MemoryMap.Joypad:
                m_joypad.WriteRegister(value: value);

                break;
            case MemoryMap.SerialData:
            case MemoryMap.SerialControl:
                m_serial.WriteRegister(address: address, value: value);

                break;
            case MemoryMap.Divider:
            case MemoryMap.TimerCounter:
            case MemoryMap.TimerModulo:
            case MemoryMap.TimerControl:
                m_timer.WriteRegister(address: address, value: value);

                break;
            case MemoryMap.OamDmaSource:
                m_oamDma.WriteRegister(value: value);

                break;
            case MemoryMap.LcdControl:
            case MemoryMap.LcdStatus:
            case MemoryMap.ScrollY:
            case MemoryMap.ScrollX:
            case MemoryMap.LcdY:
            case MemoryMap.LcdYCompare:
            case MemoryMap.BackgroundPalette:
            case MemoryMap.ObjectPalette0:
            case MemoryMap.ObjectPalette1:
            case MemoryMap.WindowY:
            case MemoryMap.WindowX:
                m_ppu.WriteRegister(address: address, value: value);

                break;
            case MemoryMap.InterruptFlag:
                m_interrupts.Requested = (InterruptKind)value;

                break;
            case MemoryMap.BootRomDisable:
                // A one-way latch: any nonzero write unmaps the boot overlay permanently; zero writes are ignored and
                // nothing ever maps it back.
                if (value != 0) {
                    m_bootRomMapped = false;
                }

                break;
            default:
                WriteColorIoRegister(address: address, value: value);

                break;
        }
    }
    // The Color-only register page's write side, mirroring ReadColorIoRegister: the decoded registers are dropped on a
    // monochrome machine, and anything undecoded lands in the fallback byte page on either model.
    private void WriteColorIoRegister(ushort address, byte value) {
        switch (address) {
            case MemoryMap.BackgroundColorPaletteIndex:
            case MemoryMap.BackgroundColorPaletteData:
            case MemoryMap.ObjectColorPaletteIndex:
            case MemoryMap.ObjectColorPaletteData:
                if (m_supportsColor) {
                    m_ppu.WriteRegister(address: address, value: value);
                }

                break;
            case MemoryMap.SpeedSwitch:
                if (m_supportsColor) {
                    m_key1.WriteRegister(value: value);
                }

                break;
            case MemoryMap.HdmaSourceHigh:
            case MemoryMap.HdmaSourceLow:
            case MemoryMap.HdmaDestinationHigh:
            case MemoryMap.HdmaDestinationLow:
            case MemoryMap.HdmaControl:
                if (m_supportsColor) {
                    m_hdma.WriteRegister(address: address, value: value);
                }

                break;
            case MemoryMap.InfraredPort:
                if (m_supportsColor) {
                    m_infrared.WriteRegister(value: value);
                }

                break;
            case MemoryMap.VramBankSelect:
                if (m_supportsColor) {
                    m_memory.VideoRamBank = value;
                }

                break;
            case MemoryMap.WorkRamBankSelect:
                if (m_supportsColor) {
                    m_memory.WorkRamBank = value;
                }

                break;
            default:
                m_ioRegisters[(address - MemoryMap.IoRegistersStart)] = value;

                break;
        }
    }
}
