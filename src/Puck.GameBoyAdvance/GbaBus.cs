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
    private readonly GbaScheduler m_scheduler;
    private readonly GbaCartridge m_cartridge;
    private readonly IGbaInterruptController m_interrupts;
    private readonly IGbaTimerController m_timers;
    private readonly IGbaDmaController m_dma;
    private readonly IGbaPpu m_ppu;
    private readonly IGbaApu m_apu;

    private uint m_openBus;
    private uint m_lastBiosOpcode;
    private bool m_inCodeFetch;
    private bool m_executingInBios;
    private bool m_dmaActive;
    private byte m_postFlag;
    private ushort m_sioCnt;
    private ushort m_sioMulti0 = 0xFFFF;
    private ushort m_sioMulti1 = 0xFFFF;
    private ushort m_sioMulti2 = 0xFFFF;
    private ushort m_sioMulti3 = 0xFFFF;
    private ushort m_sioSend = 0xFFFF;
    private ushort m_rcnt;
    private ushort m_keyControl;
    private int m_ws0N;
    private int m_ws0S;
    private int m_ws1N;
    private int m_ws1S;
    private int m_ws2N;
    private int m_ws2S;
    private int m_sram;

    // Game-pak prefetch (WAITCNT bit 14), modelled exactly as mGBA's GBAMemoryStall: code fetches pay the raw
    // wait-state, but a non-game-pak data access made while executing from ROM lets the prefetcher stream
    // sequential ROM halfwords during it, so the wait-states of the code fetches it covers are credited back from
    // the access (and the pending fetch's N becomes an S) — a credit that can drive the access cost negative,
    // which the scheduler's relative-cycle counter absorbs. m_codeFetchPc is a stable PC basis (the constant
    // pipeline offset cancels in the distance subtraction); m_lastPrefetchedPc tracks how far the prefetcher ran.
    private bool m_prefetchEnabled;
    private bool m_executingInRom;
    private uint m_codeFetchPc;
    private uint m_lastPrefetchedPc;
    private int m_romSeqWait;
    private int m_romNonSeqWait;

    /// <summary>Creates the bus over a BIOS image, a cartridge, and the I/O peripherals it routes to.</summary>
    /// <param name="bios">The system BIOS provider.</param>
    /// <param name="cartridge">The inserted cartridge.</param>
    /// <param name="interrupts">The interrupt controller (IE/IF/IME).</param>
    /// <param name="timers">The timer block.</param>
    /// <param name="dma">The DMA block.</param>
    /// <param name="ppu">The picture-processing unit (owns palette/VRAM/OAM and the display registers).</param>
    /// <param name="apu">The audio-processing unit.</param>
    /// <exception cref="ArgumentNullException">Any argument is <see langword="null"/>.</exception>
    public GbaBus(GbaScheduler scheduler, IBios bios, GbaCartridge cartridge, IGbaInterruptController interrupts, IGbaTimerController timers, IGbaDmaController dma, IGbaPpu ppu, IGbaApu apu) {
        ArgumentNullException.ThrowIfNull(scheduler);
        ArgumentNullException.ThrowIfNull(bios);
        ArgumentNullException.ThrowIfNull(cartridge);
        ArgumentNullException.ThrowIfNull(interrupts);
        ArgumentNullException.ThrowIfNull(timers);
        ArgumentNullException.ThrowIfNull(dma);
        ArgumentNullException.ThrowIfNull(ppu);
        ArgumentNullException.ThrowIfNull(apu);

        m_scheduler = scheduler;
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

    /// <summary>Gets the current master-clock time (committed clock plus the CPU's running offset).</summary>
    public long Cycles => m_scheduler.Now;

    /// <inheritdoc/>
    public bool IrqPending => m_interrupts.LineAsserted;

    /// <inheritdoc/>
    public void ProcessEvents() {
        // The CPU calls this at instruction boundaries. Commit the cycles accumulated since the last commit to the
        // master clock and fire every scheduled peripheral event now due, until the next event is still in the
        // future. The committed delta also drives the (non-event-scheduled) APU. DMA fires from the flags events set.
        while (m_scheduler.RelativeCycles >= m_scheduler.NextEvent) {
            var cycles = m_scheduler.RelativeCycles;

            m_scheduler.RelativeCycles = 0;
            m_apu.Step(cycles: cycles);
            m_scheduler.Tick(cycles: cycles);
            FireTimedDma();
        }
    }

    /// <inheritdoc/>
    public byte Read8(uint address, BusAccessType access) {
        var value = (byte)ReadRegion(address: address, width: 1);

        ChargeData(address: address, cost: AccessCycles(address: address, width: 1, access: access));

        return value;
    }

    /// <inheritdoc/>
    public ushort Read16(uint address, BusAccessType access) {
        var value = (ushort)ReadRegion(address: address & ~1u, width: 2);

        ChargeData(address: address, cost: AccessCycles(address: address, width: 2, access: access));

        return value;
    }

    /// <inheritdoc/>
    public uint Read32(uint address, BusAccessType access) {
        var value = ReadRegion(address: address & ~3u, width: 4);

        ChargeData(address: address, cost: AccessCycles(address: address, width: 4, access: access));

        return value;
    }

    /// <inheritdoc/>
    public ushort ReadCode16(uint address, BusAccessType access) {
        var cost = CodeFetchCycles(address: address, width: 2, access: access);

        m_executingInBios = address < 0x4000u;
        m_inCodeFetch = true;
        var value = (ushort)ReadRegion(address: address & ~1u, width: 2);
        m_inCodeFetch = false;

        m_scheduler.RelativeCycles += cost;

        return value;
    }

    /// <inheritdoc/>
    public uint ReadCode32(uint address, BusAccessType access) {
        var cost = CodeFetchCycles(address: address, width: 4, access: access);

        m_executingInBios = address < 0x4000u;
        m_inCodeFetch = true;
        var value = ReadRegion(address: address & ~3u, width: 4);
        m_inCodeFetch = false;

        m_scheduler.RelativeCycles += cost;

        return value;
    }

    /// <inheritdoc/>
    public void Write8(uint address, byte value, BusAccessType access) {
        WriteRegion(address: address, width: 1, value: value);
        ChargeData(address: address, cost: AccessCycles(address: address, width: 1, access: access));
    }

    /// <inheritdoc/>
    public void Write16(uint address, ushort value, BusAccessType access) {
        // Pass the raw (unaligned) address: most regions force alignment internally, but the 8-bit save bus must
        // see the low bit to pick which byte of the value it stores and where.
        WriteRegion(address: address, width: 2, value: value);
        ChargeData(address: address, cost: AccessCycles(address: address, width: 2, access: access));
    }

    /// <inheritdoc/>
    public void Write32(uint address, uint value, BusAccessType access) {
        WriteRegion(address: address, width: 4, value: value);
        ChargeData(address: address, cost: AccessCycles(address: address, width: 4, access: access));
    }

    /// <inheritdoc/>
    public void Idle(int cycles) {
        // Internal cycles just advance the CPU's running offset; the prefetcher is modelled on memory accesses.
        m_scheduler.RelativeCycles += cycles;
    }

    /// <inheritdoc/>
    public void Halt(bool stop) {
        // HALTCNT requests a low-power stop until the next interrupt. The BIOS's IntrWait/VBlankIntrWait — how
        // games actually wait — already busy-polls its interrupt-flag word around the SWI, so the CPU naturally
        // resumes the instant the IRQ is serviced; modelling a true CPU halt here only de-synchronises the
        // instruction cadence from the reference. So Halt is a no-op: the surrounding poll does the waiting.
    }

    // The PPU/timer events set the blank/FIFO flags; the bus polls them after each commit and fires the timed
    // DMAs. DMA transfers are marked active so EEPROM (which is DMA-driven on hardware) routes only for them.
    private void FireTimedDma() {
        var prevDma = m_dmaActive;

        m_dmaActive = true;

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

        if (m_apu.ConsumeFifoARefill()) {
            m_dma.OnFifo(fifo: 0, bus: this);
        }

        if (m_apu.ConsumeFifoBRefill()) {
            m_dma.OnFifo(fifo: 1, bus: this);
        }

        m_dmaActive = prevDma;
    }

    private uint ReadRegion(uint address, int width) {
        var value = (address >> 24) switch {
            RegionBios => ReadBios(address: address, width: width),
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
        // Aligned address for the wide-bus regions (the CPU forces 16/32-bit accesses to their natural alignment);
        // the save region below deliberately uses the raw address for its 8-bit byte select.
        var aligned = address & ~(uint)(width - 1);

        switch (address >> 24) {
            case RegionEwram:
                WriteArray(array: m_ewram, index: aligned & 0x3FFFFu, width: width, value: value);

                break;
            case RegionIwram:
                WriteArray(array: m_iwram, index: aligned & 0x7FFFu, width: width, value: value);

                break;
            case RegionIo:
                WriteIo(address: aligned, width: width, value: value);

                break;
            case RegionPalette:
            case RegionVram:
            case RegionOam:
                m_ppu.WriteVideo(address: aligned, width: width, value: value);

                break;
            case 0xE:
            case 0xF:
                // The save region is an 8-bit bus; wider stores write the byte selected by the low address bits.
                m_cartridge.WriteSave(address: address & 0xFFFFu, value: (byte)(value >> (int)((address & (uint)(width - 1)) * 8u)));

                break;
            case 0x8:
            case 0x9:
            case 0xA:
            case 0xB:
            case 0xC:
            case 0xD: {
                // A serial EEPROM is written one bit at a time (bit 0) over the upper ROM region (0x0D…), by DMA.
                if (m_dmaActive && m_cartridge.IsEeprom && ((address >> 24) == 0x0Du)) {
                    m_cartridge.WriteEeprom(value: (ushort)value);

                    break;
                }

                // ROM is otherwise read-only, except the GPIO/RTC registers overlaid at 0x0C4–0x0C8.
                var offset = address & 0x01FFFFFFu;

                if (m_cartridge.HasRtc && (offset >= 0xC4u) && (offset <= 0xC8u)) {
                    m_cartridge.WriteGpio(register: offset & ~1u, value: (ushort)value);
                }

                break;
            }
            default:
                // BIOS writes are dropped.
                break;
        }
    }

    // The BIOS ROM is readable only by code executing within it: a code fetch returns the real bytes and latches
    // the fetched opcode word; a data read returns real bytes only while executing in the BIOS, else that opcode.
    private uint ReadBios(uint address, int width) {
        if (address >= 0x4000u) {
            return m_openBus;
        }

        if (m_inCodeFetch) {
            m_lastBiosOpcode = ReadArray(array: m_bios, index: address & 0x3FFCu, width: 4);

            return ReadArray(array: m_bios, index: address & 0x3FFFu, width: width);
        }

        return m_executingInBios
            ? ReadArray(array: m_bios, index: address & 0x3FFFu, width: width)
            : m_lastBiosOpcode;
    }

    private uint ReadRom(uint address, int width) {
        // A serial EEPROM responds in the upper ROM region (0x0D…) to DMA accesses only (it is DMA-driven on
        // hardware). A CPU read there hits the ROM mirror/open-bus — so a cart that merely embeds the EEPROM_V
        // string (e.g. the AGS aging cartridge) never has its 0x0D region hijacked.
        if (m_dmaActive && m_cartridge.IsEeprom && ((address >> 24) == 0x0Du)) {
            return m_cartridge.ReadEeprom();
        }

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

        // POSTFLG (0x300) / HALTCNT (0x301): a HALT is requested by writing HALTCNT=0x00, which a halfword
        // read-modify-write cannot distinguish from "no change" (HALTCNT reads back 0), so apply bytes directly.
        if ((offset <= 0x301u) && ((offset + (uint)width) > 0x300u)) {
            if ((offset <= 0x300u) && ((offset + (uint)width) > 0x300u)) {
                m_postFlag = (byte)(value >> (int)((0x300u - offset) * 8u));
            }

            if ((offset <= 0x301u) && ((offset + (uint)width) > 0x301u)) {
                Halt(stop: ((value >> (int)((0x301u - offset) * 8u)) & 0x80u) != 0u);
            }

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

        switch (offset) {
            case 0x120u: return m_sioMulti0;
            case 0x122u: return m_sioMulti1;
            case 0x124u: return m_sioMulti2;
            case 0x126u: return m_sioMulti3;
            case 0x128u: return m_sioCnt;
            case 0x12Au: return m_sioSend;
            case 0x132u: return m_keyControl;
            case 0x134u: return m_rcnt;
            case 0x300u: return m_postFlag; // POSTFLG (bit 0); HALTCNT (high byte) is write-only, reads 0
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
            // A control-register write can start an immediate DMA, whose transfers must count as DMA accesses.
            var prevDma = m_dmaActive;
            m_dmaActive = true;
            m_dma.WriteRegister(offset: offset, value: value, bus: this);
            m_dmaActive = prevDma;

            return;
        }

        if ((offset == 0x200u) || (offset == 0x202u) || (offset == 0x208u)) {
            m_interrupts.WriteRegister(offset: offset, value: value);

            return;
        }

        switch (offset) {
            case 0x120u: m_sioMulti0 = value; return;
            case 0x122u: m_sioMulti1 = value; return;
            case 0x124u: m_sioMulti2 = value; return;
            case 0x126u: m_sioMulti3 = value; return;
            case 0x128u: WriteSioControl(value: value); return;
            case 0x12Au: m_sioSend = value; return;
            case 0x132u: m_keyControl = value; EvaluateKeypadIrq(); return;
            case 0x134u: m_rcnt = value; return;
        }

        m_io[offset] = (byte)value;
        m_io[offset + 1u] = (byte)(value >> 8);

        // WAITCNT (0x04000204) reconfigures the game-pak wait-states.
        if (offset == 0x204u) {
            UpdateWaitControl(value: value);
        }
    }

    // SIOCNT (0x128): Normal/Multiplayer/UART mode SIO control.
    // Bit-field meaning depends on the mode selected by bits [13:12]:
    //   00/01 = Normal 8-/32-bit: bit 0 = clock select (0=external, 1=internal).
    //           External clock: transfer waits for a partner to supply SC — never completes without one.
    //           Internal clock: GBA provides SC itself; completes immediately, received data = 0xFFFF.
    //   10 = Multiplayer: bit 0 is baud rate, not clock select. The Parent GBA starts the transfer and it
    //        always completes (all child slots return 0xFFFF when no GBA is connected). Fire immediately.
    //   11 = UART: not commonly used; fire immediately so the game sees a clean completion.
    private void WriteSioControl(ushort value) {
        if ((value & 0x0080u) != 0u) {
            m_sioCnt = (ushort)(value & ~0x0080u);

            if ((value & 0x4000u) != 0u) {
                var mode = (value >> 12) & 3u;
                var normalWithInternalClock = (mode != 2u) && (mode != 3u) && ((value & 0x0001u) != 0u);
                var multiplayerOrUart = (mode == 2u) || (mode == 3u);

                if (normalWithInternalClock || multiplayerOrUart) {
                    m_interrupts.Request(source: InterruptSource.Serial);
                }
            }
        }
        else {
            m_sioCnt = value;
        }
    }

    // KEYCNT (0x132): check the keypad IRQ condition against the current KEYINPUT state and request an IRQ if met.
    // OR mode (bit 15 = 0): any selected key is pressed (KEYINPUT bit = 0).
    // AND mode (bit 15 = 1): all selected keys are pressed — vacuously true when 0 keys are selected.
    private void EvaluateKeypadIrq() {
        if ((m_keyControl & 0x4000u) == 0u) {
            return;
        }

        var selected = (uint)(m_keyControl & 0x03FFu);
        var keyInput = (uint)((m_io[0x131] << 8) | m_io[0x130]) & 0x03FFu; // 0 = pressed, 1 = released
        var pressed = selected & ~keyInput;

        bool condition;

        if ((m_keyControl & 0x8000u) == 0u) {
            condition = pressed != 0u;                 // OR: any selected key pressed
        }
        else {
            condition = (selected & keyInput) == 0u;   // AND: all selected keys pressed (vacuously true if selected=0)
        }

        if (condition) {
            m_interrupts.Request(source: InterruptSource.Keypad);
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

    // Charges a data access. While code runs from ROM with prefetch on, a non-game-pak access lets the prefetcher
    // run ahead and discounts the upcoming code fetches it covers (mGBA's GBAMemoryStall, which may net negative).
    private void ChargeData(uint address, int cost) {
        if (!s_disablePrefetch && m_prefetchEnabled && m_executingInRom && ((address >> 24) < 0x08u)) {
            cost = ApplyPrefetchStall(wait: cost);
        }

        m_scheduler.RelativeCycles += cost;
    }

    // Temporary diagnostic switch to isolate the prefetch credit from the event model.
    private static readonly bool s_disablePrefetch = Environment.GetEnvironmentVariable("PUCK_NO_PREFETCH") == "1";

    // Cycle cost of an instruction fetch: always the raw wait-state. The prefetch benefit is credited from the
    // following data access (ApplyPrefetchStall), not the fetch. Records the execution context that needs.
    private int CodeFetchCycles(uint address, int width, BusAccessType access) {
        var region = address >> 24;

        m_codeFetchPc = address;
        m_executingInRom = (region >= 0x08u) && (region <= 0x0Du);

        if (m_executingInRom) {
            m_romSeqWait = RomSeqCycles(region: region);
            m_romNonSeqWait = RomNonSeqCycles(region: region);
        }

        return AccessCycles(address: address, width: width, access: access);
    }

    // Game-pak prefetch, ported from mGBA's GBAMemoryStall: while code runs from ROM with prefetch on, a data
    // access to a non-game-pak region lets the prefetcher stream sequential ROM halfwords during the access, so
    // the wait-states of the code fetches it covers vanish (and the pending fetch's N becomes an S).
    private int ApplyPrefetchStall(int wait) {
        var s = m_romSeqWait;
        var pc = m_codeFetchPc;

        var previousLoads = 0;
        var dist = m_lastPrefetchedPc - pc;
        var maxLoads = 8;

        if (dist < 16u) {
            previousLoads = (int)(dist >> 1);
            maxLoads -= previousLoads;
        }

        var stall = s + 1;
        var loads = 1;

        while ((stall < wait) && (loads < maxLoads)) {
            stall += s;
            ++loads;
        }

        m_lastPrefetchedPc = pc + (uint)(2 * (loads + previousLoads - 1));

        if (stall > wait) {
            wait = stall;
        }

        wait -= m_romNonSeqWait - s; // the pending fetch used to be an N; prefetch makes it an S
        wait -= stall;               // the prefetched code fetches disappear entirely

        return wait;
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
            ? (seq + 1)
            : (nonSeq + 1);
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
