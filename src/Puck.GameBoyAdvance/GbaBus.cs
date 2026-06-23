namespace Puck.GameBoyAdvance;

/// <summary>
/// The Game Boy Advance system bus: the decoded memory map every ARM7TDMI access flows through. It owns the
/// internal memories (BIOS, on-board and on-chip WRAM, palette, VRAM, OAM, I/O), routes cartridge ROM and save
/// accesses, applies each region's mirroring and wait-states, and advances the clocked peripherals before each
/// access — the deferred-cycle discipline carried over from the DMG/CGB core. Width-typed accessors model the
/// 16-bit external bus (a 32-bit access to a 16-bit region costs two transfers).
/// </summary>
public sealed class GbaBus : IGbaBus {
    private const uint RegionBios = 0x0;
    private const uint RegionEwram = 0x2;
    private const uint RegionIwram = 0x3;
    private const uint RegionIo = 0x4;
    private const uint RegionPalette = 0x5;
    private const uint RegionVram = 0x6;
    private const uint RegionOam = 0x7;

    // Game-pak wait-state cycle tables (GBATEK): non-sequential first access and sequential follow-on, per
    // WAITCNT setting. Indexed by the 2-bit (N) / 1-bit (S) field for each of the three ROM mirrors.
    private static readonly int[] s_romNonSeq = { 4, 3, 2, 8 };
    private static readonly int[] s_ws0Seq = { 2, 1 };
    private static readonly int[] s_ws1Seq = { 4, 1 };
    private static readonly int[] s_ws2Seq = { 8, 1 };
    private static readonly int[] s_sramWait = { 4, 3, 2, 8 };

    private readonly byte[] m_bios;
    private readonly byte[] m_ewram = new byte[0x40000];
    private readonly byte[] m_iwram = new byte[0x8000];
    private readonly byte[] m_io = new byte[0x400];
    private readonly GbaCartridge m_cartridge;
    private readonly IGbaInterruptController m_interrupts;
    private readonly IGbaTimerController m_timers;
    private readonly IGbaDmaController m_dma;
    private readonly IGbaPpu m_ppu;
    private readonly IGbaApu m_apu;

    private uint m_openBus;
    private int m_ws0N;
    private int m_ws0S;
    private int m_ws1N;
    private int m_ws1S;
    private int m_ws2N;
    private int m_ws2S;
    private int m_sram;

    // Game-pak prefetch buffer (WAITCNT bit 14). When enabled, the cartridge speculatively reads sequential
    // halfwords ahead of the CPU into an 8-entry buffer during cycles the CPU is not using the game-pak bus
    // (internal cycles, non-ROM accesses). A sequential code fetch already buffered costs 1 cycle; otherwise the
    // CPU stalls for the in-flight load. Any non-sequential fetch or ROM data access flushes the buffer.
    private const int PrefetchCapacity = 8;
    private bool m_prefetchEnabled;
    private bool m_prefetchActive;
    private int m_prefetchCount;
    private int m_prefetchCountdown;
    private int m_prefetchSeq;
    private uint m_prefetchHead;

    /// <summary>Creates the bus over a BIOS image, a cartridge, and the I/O peripherals it routes to.</summary>
    /// <param name="bios">The system BIOS provider.</param>
    /// <param name="cartridge">The inserted cartridge.</param>
    /// <param name="interrupts">The interrupt controller (IE/IF/IME).</param>
    /// <param name="timers">The timer block.</param>
    /// <param name="dma">The DMA block.</param>
    /// <param name="ppu">The picture-processing unit (owns palette/VRAM/OAM and the display registers).</param>
    /// <param name="apu">The audio-processing unit.</param>
    /// <exception cref="ArgumentNullException">Any argument is <see langword="null"/>.</exception>
    public GbaBus(IBios bios, GbaCartridge cartridge, IGbaInterruptController interrupts, IGbaTimerController timers, IGbaDmaController dma, IGbaPpu ppu, IGbaApu apu) {
        ArgumentNullException.ThrowIfNull(bios);
        ArgumentNullException.ThrowIfNull(cartridge);
        ArgumentNullException.ThrowIfNull(interrupts);
        ArgumentNullException.ThrowIfNull(timers);
        ArgumentNullException.ThrowIfNull(dma);
        ArgumentNullException.ThrowIfNull(ppu);
        ArgumentNullException.ThrowIfNull(apu);

        m_bios = bios.Image.ToArray();
        m_cartridge = cartridge;
        m_interrupts = interrupts;
        m_timers = timers;
        m_dma = dma;
        m_ppu = ppu;
        m_apu = apu;

        UpdateWaitControl(value: 0);

        // KEYINPUT (0x04000130) reads 0x03FF at rest — every button is active-low and released.
        m_io[0x130] = 0xFF;
        m_io[0x131] = 0x03;
    }

    /// <summary>Gets the total master clock cycles charged to the bus since construction.</summary>
    public long Cycles { get; private set; }

    /// <inheritdoc/>
    public bool IrqPending => m_interrupts.LineAsserted;

    /// <inheritdoc/>
    public byte Read8(uint address, BusAccessType access) {
        DataAccess(address: address, cycles: AccessCycles(address: address, width: 1, access: access));

        return (byte)ReadRegion(address: address, width: 1);
    }

    /// <inheritdoc/>
    public ushort Read16(uint address, BusAccessType access) {
        DataAccess(address: address, cycles: AccessCycles(address: address, width: 2, access: access));

        return (ushort)ReadRegion(address: address & ~1u, width: 2);
    }

    /// <inheritdoc/>
    public uint Read32(uint address, BusAccessType access) {
        DataAccess(address: address, cycles: AccessCycles(address: address, width: 4, access: access));

        return ReadRegion(address: address & ~3u, width: 4);
    }

    /// <inheritdoc/>
    public ushort ReadCode16(uint address, BusAccessType access) {
        Tick(cycles: CodeFetchCycles(address: address, width: 2, access: access));

        return (ushort)ReadRegion(address: address & ~1u, width: 2);
    }

    /// <inheritdoc/>
    public uint ReadCode32(uint address, BusAccessType access) {
        Tick(cycles: CodeFetchCycles(address: address, width: 4, access: access));

        return ReadRegion(address: address & ~3u, width: 4);
    }

    /// <inheritdoc/>
    public void Write8(uint address, byte value, BusAccessType access) {
        DataAccess(address: address, cycles: AccessCycles(address: address, width: 1, access: access));

        WriteRegion(address: address, width: 1, value: value);
    }

    /// <inheritdoc/>
    public void Write16(uint address, ushort value, BusAccessType access) {
        DataAccess(address: address, cycles: AccessCycles(address: address, width: 2, access: access));

        WriteRegion(address: address & ~1u, width: 2, value: value);
    }

    /// <inheritdoc/>
    public void Write32(uint address, uint value, BusAccessType access) {
        DataAccess(address: address, cycles: AccessCycles(address: address, width: 4, access: access));

        WriteRegion(address: address & ~3u, width: 4, value: value);
    }

    /// <inheritdoc/>
    public void Idle(int cycles) {
        // Internal cycles leave the game-pak bus free, so the prefetch buffer fills.
        StepPrefetch(cycles: cycles);
        Tick(cycles: cycles);
    }

    private void Tick(int cycles) {
        Cycles += cycles;

        // The peripherals observe the access at the point it happens (deferred-cycle model).
        m_timers.Step(cycles: cycles);
        m_ppu.Step(cycles: cycles);
        m_apu.Step(cycles: cycles);

        // The PPU only flags the blank transitions; the bus fires the timed DMAs so the PPU stays bus-free.
        if (m_ppu.ConsumeVBlankStarted()) {
            m_dma.OnVBlank(bus: this);
        }

        if (m_ppu.ConsumeHBlankStarted()) {
            m_dma.OnHBlank(bus: this);
        }

        if (m_ppu.ConsumeVideoCaptureStarted()) {
            m_dma.OnVideoCapture(bus: this);
        }

        if (m_ppu.ConsumeVideoCaptureEnded()) {
            m_dma.OnVideoCaptureEnd();
        }

        // Drained Direct Sound FIFOs are topped up by their special-timing DMA channels.
        if (m_apu.ConsumeFifoARefill()) {
            m_dma.OnFifo(fifo: 0, bus: this);
        }

        if (m_apu.ConsumeFifoBRefill()) {
            m_dma.OnFifo(fifo: 1, bus: this);
        }
    }

    private uint ReadRegion(uint address, int width) {
        var value = (address >> 24) switch {
            RegionBios => (address < 0x4000u) ? ReadArray(array: m_bios, index: address & 0x3FFFu, width: width) : m_openBus,
            RegionEwram => ReadArray(array: m_ewram, index: address & 0x3FFFFu, width: width),
            RegionIwram => ReadArray(array: m_iwram, index: address & 0x7FFFu, width: width),
            RegionIo => ReadIo(address: address, width: width),
            RegionPalette or RegionVram or RegionOam => m_ppu.ReadVideo(address: address, width: width),
            0x8 or 0x9 or 0xA or 0xB or 0xC or 0xD => ReadRom(address: address, width: width),
            0xE or 0xF => ReadSave(address: address, width: width),
            _ => m_openBus,
        };

        m_openBus = value;

        return value;
    }

    private void WriteRegion(uint address, int width, uint value) {
        switch (address >> 24) {
            case RegionEwram:
                WriteArray(array: m_ewram, index: address & 0x3FFFFu, width: width, value: value);

                break;
            case RegionIwram:
                WriteArray(array: m_iwram, index: address & 0x7FFFu, width: width, value: value);

                break;
            case RegionIo:
                WriteIo(address: address, width: width, value: value);

                break;
            case RegionPalette:
            case RegionVram:
            case RegionOam:
                m_ppu.WriteVideo(address: address, width: width, value: value);

                break;
            case 0xE:
            case 0xF:
                // The save region is an 8-bit bus; wider stores write the byte selected by the low address bits.
                m_cartridge.WriteSave(address: address & 0xFFFFu, value: (byte)(value >> (int)((address & (uint)(width - 1)) * 8u)));

                break;
            default:
                // BIOS and ROM are read-only; writes elsewhere are dropped.
                break;
        }
    }

    private uint ReadRom(uint address, int width) {
        var offset = address & 0x01FFFFFFu;

        return width switch {
            1 => m_cartridge.ReadRom(offset: offset),
            2 => (uint)(m_cartridge.ReadRom(offset: offset) | (m_cartridge.ReadRom(offset: offset + 1u) << 8)),
            _ => (uint)(m_cartridge.ReadRom(offset: offset)
                | (m_cartridge.ReadRom(offset: offset + 1u) << 8)
                | (m_cartridge.ReadRom(offset: offset + 2u) << 16)
                | (m_cartridge.ReadRom(offset: offset + 3u) << 24)),
        };
    }

    private uint ReadSave(uint address, int width) {
        var b = m_cartridge.ReadSave(address: address & 0xFFFFu);

        // The 8-bit save bus repeats its byte across a wider read.
        return width switch {
            1 => b,
            2 => (uint)(b * 0x0101u),
            _ => b * 0x01010101u,
        };
    }

    private uint ReadIo(uint address, int width) {
        var offset = address & 0xFFFFFFu;

        if (offset >= 0x400u) {
            return m_openBus;
        }

        return width switch {
            1 => ((offset & 1u) == 0u) ? (byte)ReadIoHalf(offset: offset & ~1u) : (byte)(ReadIoHalf(offset: offset & ~1u) >> 8),
            2 => ReadIoHalf(offset: offset),
            _ => ReadIoHalf(offset: offset) | ((uint)ReadIoHalf(offset: offset + 2u) << 16),
        };
    }

    private void WriteIo(uint address, int width, uint value) {
        var offset = address & 0xFFFFFFu;

        if (offset >= 0x400u) {
            return;
        }

        switch (width) {
            case 1: {
                var aligned = offset & ~1u;
                var half = ReadIoHalf(offset: aligned);

                half = ((offset & 1u) == 0u)
                    ? (ushort)((half & 0xFF00u) | (value & 0xFFu))
                    : (ushort)((half & 0x00FFu) | ((value & 0xFFu) << 8));

                WriteIoHalf(offset: aligned, value: half);

                break;
            }
            case 2:
                WriteIoHalf(offset: offset, value: (ushort)value);

                break;
            default:
                WriteIoHalf(offset: offset, value: (ushort)value);
                WriteIoHalf(offset: offset + 2u, value: (ushort)(value >> 16));

                break;
        }
    }

    // The I/O page is a 16-bit space; reads and writes of other widths decompose into halfword accesses, which
    // are routed to the owning peripheral or fall through to the raw register store.
    private ushort ReadIoHalf(uint offset) {
        if (offset >= 0x400u) {
            return 0;
        }

        if (offset < 0x58u) {
            return m_ppu.ReadRegister(offset: offset);
        }

        if ((offset >= 0x60u) && (offset < 0xA8u)) {
            return m_apu.ReadRegister(offset: offset);
        }

        if ((offset >= 0x100u) && (offset < 0x110u)) {
            return m_timers.ReadRegister(offset: offset);
        }

        if ((offset >= 0xB0u) && (offset < 0xE0u)) {
            return m_dma.ReadRegister(offset: offset);
        }

        if ((offset == 0x200u) || (offset == 0x202u) || (offset == 0x208u)) {
            return m_interrupts.ReadRegister(offset: offset);
        }

        return (ushort)(m_io[offset] | (m_io[offset + 1u] << 8));
    }

    private void WriteIoHalf(uint offset, ushort value) {
        if (offset >= 0x400u) {
            return;
        }

        if (offset < 0x58u) {
            m_ppu.WriteRegister(offset: offset, value: value);

            return;
        }

        if ((offset >= 0x60u) && (offset < 0xA8u)) {
            m_apu.WriteRegister(offset: offset, value: value);

            return;
        }

        if ((offset >= 0x100u) && (offset < 0x110u)) {
            m_timers.WriteRegister(offset: offset, value: value);

            return;
        }

        if ((offset >= 0xB0u) && (offset < 0xE0u)) {
            m_dma.WriteRegister(offset: offset, value: value, bus: this);

            return;
        }

        if ((offset == 0x200u) || (offset == 0x202u) || (offset == 0x208u)) {
            m_interrupts.WriteRegister(offset: offset, value: value);

            return;
        }

        m_io[offset] = (byte)value;
        m_io[offset + 1u] = (byte)(value >> 8);

        // WAITCNT (0x04000204) reconfigures the game-pak wait-states.
        if (offset == 0x204u) {
            UpdateWaitControl(value: value);
        }
    }

    private void UpdateWaitControl(ushort value) {
        m_prefetchEnabled = (value & 0x4000) != 0;
        m_sram = s_sramWait[value & 0x3];
        m_ws0N = s_romNonSeq[(value >> 2) & 0x3];
        m_ws0S = s_ws0Seq[(value >> 4) & 0x1];
        m_ws1N = s_romNonSeq[(value >> 5) & 0x3];
        m_ws1S = s_ws1Seq[(value >> 7) & 0x1];
        m_ws2N = s_romNonSeq[(value >> 8) & 0x3];
        m_ws2S = s_ws2Seq[(value >> 10) & 0x1];
    }

    // A CPU data access uses the game-pak bus when it targets ROM or SRAM, which flushes the prefetch buffer;
    // anywhere else the game-pak bus is free, so the buffer fills during this access.
    private void DataAccess(uint address, int cycles) {
        var region = address >> 24;

        if ((region >= 0x08u) && (region <= 0x0Fu)) {
            m_prefetchActive = false;
        }
        else {
            StepPrefetch(cycles: cycles);
        }

        Tick(cycles: cycles);
    }

    // Cycle cost of an instruction fetch. Outside ROM (or with prefetch disabled) this is the plain wait-state;
    // inside ROM with prefetch on, sequential fetches are served from the buffer.
    private int CodeFetchCycles(uint address, int width, BusAccessType access) {
        var region = address >> 24;

        if ((region < 0x08u) || (region > 0x0Du) || !m_prefetchEnabled) {
            m_prefetchActive = false;

            return AccessCycles(address: address, width: width, access: access);
        }

        return (width == 4)
            ? ConsumeHalf(address: address, access: access) + ConsumeHalf(address: address + 2u, access: BusAccessType.Sequential)
            : ConsumeHalf(address: address, access: access);
    }

    // Serves one opcode halfword from the prefetch buffer, returning its cycle cost and advancing the buffer head.
    private int ConsumeHalf(uint address, BusAccessType access) {
        if (!m_prefetchActive || (address != m_prefetchHead)) {
            // A branch (or the first fetch into ROM) restarts the stream: pay the full non-sequential access, then
            // begin prefetching the following halfwords. Restart is driven by address discontinuity, not the N/S
            // type — a data access to non-ROM sets the next fetch non-sequential but the prefetch unit keeps
            // running, so a contiguous opcode fetch after it is still served from the buffer.
            m_prefetchActive = true;
            m_prefetchSeq = RomSeqCycles(region: address >> 24);
            m_prefetchCount = 0;
            m_prefetchCountdown = 0;
            m_prefetchHead = address + 2u;

            return RomNonSeqCycles(region: address >> 24);
        }

        int cost;

        if (m_prefetchCount > 0) {
            // Already buffered ahead of the CPU: a single cycle to hand it over.
            --m_prefetchCount;
            cost = 1;
        }
        else if (m_prefetchCountdown > 0) {
            // The buffer is mid-load of exactly this halfword; the CPU waits out the remainder.
            cost = m_prefetchCountdown;
            m_prefetchCountdown = 0;
        }
        else {
            // The buffer had no head start, so this fetch pays a full sequential access.
            cost = m_prefetchSeq;
        }

        m_prefetchHead = address + 2u;

        return cost;
    }

    // Advances the prefetch buffer by the given free cycles, loading sequential halfwords up to its capacity.
    private void StepPrefetch(int cycles) {
        if (!m_prefetchActive || !m_prefetchEnabled) {
            return;
        }

        while ((cycles > 0) && (m_prefetchCount < PrefetchCapacity)) {
            if (m_prefetchCountdown <= 0) {
                m_prefetchCountdown = m_prefetchSeq;
            }

            var step = Math.Min(cycles, m_prefetchCountdown);

            m_prefetchCountdown -= step;
            cycles -= step;

            if (m_prefetchCountdown == 0) {
                ++m_prefetchCount;
            }
        }
    }

    private int RomSeqCycles(uint region) => region switch {
        0x8u or 0x9u => m_ws0S,
        0xAu or 0xBu => m_ws1S,
        _ => m_ws2S,
    };

    private int RomNonSeqCycles(uint region) => region switch {
        0x8u or 0x9u => m_ws0N,
        0xAu or 0xBu => m_ws1N,
        _ => m_ws2N,
    };

    private int AccessCycles(uint address, int width, BusAccessType access) {
        switch (address >> 24) {
            case RegionEwram:
                // On-board WRAM sits behind a 16-bit bus with two default wait-states.
                return (width == 4) ? 6 : 3;
            case RegionPalette:
            case RegionVram:
                return (width == 4) ? 2 : 1;
            case 0x8:
            case 0x9:
                return RomCycles(nonSeq: m_ws0N, seq: m_ws0S, width: width, access: access);
            case 0xA:
            case 0xB:
                return RomCycles(nonSeq: m_ws1N, seq: m_ws1S, width: width, access: access);
            case 0xC:
            case 0xD:
                return RomCycles(nonSeq: m_ws2N, seq: m_ws2S, width: width, access: access);
            case 0xE:
            case 0xF:
                // The 8-bit save bus pays the same first-access overhead as a ROM word access.
                return m_sram + 1;
            default:
                // BIOS, on-chip WRAM, I/O, OAM: single-cycle 32-bit bus.
                return 1;
        }
    }

    private static int RomCycles(int nonSeq, int seq, int width, BusAccessType access) {
        // On the 16-bit game-pak bus a 32-bit access is two halfword transfers plus a one-cycle merge penalty,
        // and the game-pak adds one further first-access cycle to every word access — code (when the prefetch
        // buffer is off) and data alike. So a non-sequential word costs N+S+2 and a sequential word 2S+2, matching
        // mGBA and the AGS wait-state/prefetch timer values (verified per-instruction via the cycle co-sim).
        if (width == 4) {
            return (access == BusAccessType.Sequential)
                ? (seq + seq + 2)
                : (nonSeq + seq + 2);
        }

        return (access == BusAccessType.Sequential)
            ? seq
            : nonSeq;
    }

    private static uint ReadArray(byte[] array, uint index, int width) {
        return width switch {
            1 => array[index],
            2 => (uint)(array[index] | (array[index + 1u] << 8)),
            _ => (uint)(array[index]
                | (array[index + 1u] << 8)
                | (array[index + 2u] << 16)
                | (array[index + 3u] << 24)),
        };
    }

    private static void WriteArray(byte[] array, uint index, int width, uint value) {
        array[index] = (byte)value;

        if (width >= 2) {
            array[index + 1u] = (byte)(value >> 8);
        }

        if (width == 4) {
            array[index + 2u] = (byte)(value >> 16);
            array[index + 3u] = (byte)(value >> 24);
        }
    }
}
