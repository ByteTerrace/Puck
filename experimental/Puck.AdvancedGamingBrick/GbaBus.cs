namespace Puck.AdvancedGamingBrick;

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
    private readonly IGbaSerialController m_serial;
    private readonly IGbaPpu m_ppu;
    private readonly IGbaApu m_apu;

    private uint m_openBus;
    private uint m_lastBiosOpcode;
    private bool m_inCodeFetch;
    private bool m_executingInBios;
    private bool m_dmaActive;
    private bool m_dmaStalling;
    private bool m_halted;
    private long m_apuClock;
    private byte m_postFlag;
    private ushort m_keyControl;
    private int m_ws0N;
    private int m_ws0S;
    private int m_ws1N;
    private int m_ws1S;
    private int m_ws2N;
    private int m_ws2S;
    private int m_sram;

    // Game-pak prefetch buffer (WAITCNT bit 14): an 8-halfword FIFO that loads sequential ROM data in the
    // background during non-ROM accesses, letting future code fetches hit the buffer for 1 cycle instead of the
    // full ROM wait-state. Ported from Ares's prefetch model (prefetch.cpp / bus.cpp).
    private bool m_prefetchEnabled;
    private readonly ushort[] m_prefetchSlots = new ushort[8];
    private uint m_prefetchAddr;
    private uint m_prefetchLoad;
    private int m_prefetchWait;
    private bool m_prefetchStopped = true;
    private bool m_prefetchAhead;

    /// <summary>Creates the bus over a BIOS image, a cartridge, and the I/O peripherals it routes to.</summary>
    /// <param name="bios">The system BIOS provider.</param>
    /// <param name="cartridge">The inserted cartridge.</param>
    /// <param name="interrupts">The interrupt controller (IE/IF/IME).</param>
    /// <param name="timers">The timer block.</param>
    /// <param name="dma">The DMA block.</param>
    /// <param name="ppu">The picture-processing unit (owns palette/VRAM/OAM and the display registers).</param>
    /// <param name="apu">The audio-processing unit.</param>
    /// <exception cref="ArgumentNullException">Any argument is <see langword="null"/>.</exception>
    public GbaBus(GbaScheduler scheduler, IBios bios, GbaCartridge cartridge, IGbaInterruptController interrupts, IGbaTimerController timers, IGbaDmaController dma, IGbaSerialController serial, IGbaPpu ppu, IGbaApu apu) {
        ArgumentNullException.ThrowIfNull(scheduler);
        ArgumentNullException.ThrowIfNull(bios);
        ArgumentNullException.ThrowIfNull(cartridge);
        ArgumentNullException.ThrowIfNull(interrupts);
        ArgumentNullException.ThrowIfNull(timers);
        ArgumentNullException.ThrowIfNull(dma);
        ArgumentNullException.ThrowIfNull(serial);
        ArgumentNullException.ThrowIfNull(ppu);
        ArgumentNullException.ThrowIfNull(apu);

        m_scheduler = scheduler;
        m_bios = bios.Image.ToArray();
        m_cartridge = cartridge;
        m_interrupts = interrupts;
        m_timers = timers;
        m_dma = dma;
        m_serial = serial;
        m_ppu = ppu;
        m_apu = apu;

        cartridge.SetCycleProvider(provider: () => m_scheduler.Now);

        UpdateWaitControl(value: 0);

        // KEYINPUT (0x04000130) reads 0x03FF at rest — every button is active-low and released.
        m_io[0x130] = 0xFF;
        m_io[0x131] = 0x03;
    }

    /// <summary>Gets the current master-clock time (committed clock plus the CPU's running offset).</summary>
    public long Cycles => m_scheduler.Now;

    /// <summary>Reads an I/O register halfword without advancing the clock — for the I/O-read differential dump.</summary>
    public ushort DebugReadIo(uint offset) => ReadIoHalf(offset: offset);

    /// <summary>Sets the KEYINPUT register (0x04000130). The value is active-low: bit 0 = A, bit 1 = B,
    /// bit 2 = Select, bit 3 = Start, bits 4–7 = D-pad (R/L/U/D), bits 8–9 = shoulder (R/L).
    /// A set bit means the button is <b>released</b>; a clear bit means <b>pressed</b>.</summary>
    public void SetKeyInput(ushort keys) {
        m_io[0x130] = (byte)(keys & 0xFF);
        m_io[0x131] = (byte)((keys >> 8) & 0x03);
        EvaluateKeypadIrq();
    }

    /// <inheritdoc/>
    public bool IrqPending => m_interrupts.Synchronizer;

    /// <inheritdoc/>
    public bool Synchronizer => m_interrupts.Synchronizer;

    /// <inheritdoc/>
    public bool BeginDmaStall() {
        var previous = m_dmaStalling;

        m_dmaStalling = true;

        return previous;
    }

    /// <inheritdoc/>
    public void EndDmaStall(bool previous) => m_dmaStalling = previous;

    // The unified per-cycle clock (ARES's cycle-stepped model, gba/cpu/cpu.cpp:72-111 + Thread::synchronize).
    // Advances the master clock <paramref name="n"/> cycles; on the way it steps the IRQ synchronizer and the four
    // timers each cycle, and fires every scheduled peripheral event (PPU, SIO) at its EXACT cycle — never batched at
    // an instruction boundary. The bus calls this from every cycle-charge site, so every component advances in
    // lockstep with the CPU's clock, and a peripheral register read mid-instruction sees up-to-the-cycle state.
    // Fast-path: across a span with no running timer and a settled IRQ pipeline nothing per-cycle changes (only a
    // timer Step can set IF, and only an event can change peripheral state), so the span up to the next event
    // collapses to a single clock advance — provably identical, and what keeps the engine fast.
    private void StepClocks(int n) {
        if (n <= 0) {
            m_scheduler.Now += n; // defensive; charge sites never pass a negative count

            return;
        }

        var remaining = n;

        while (remaining > 0) {
            var untilEvent = m_scheduler.NextWhen - m_scheduler.Now;

            if (untilEvent <= 0L) {
                FireEvents(); // an event is due at the current cycle — fire it before advancing further

                continue;
            }

            // Never step past the next event: clamp the span so the event fires on its exact cycle.
            var chunk = (untilEvent < remaining) ? (int)untilEvent : remaining;

            if (!m_timers.HasRunningTimer && !m_timers.HasPendingLatch && m_interrupts.PipelineQuiescent) {
                m_scheduler.Now += chunk;
            }
            else {
                for (var i = 0; i < chunk; ++i) {
                    m_interrupts.StepSync(stallingCpu: m_dmaStalling);
                    m_timers.RunCycle(clock: m_scheduler.Now);
                    m_scheduler.Now += 1;
                }
            }

            remaining -= chunk;

            if (m_scheduler.NextWhen <= m_scheduler.Now) {
                FireEvents();
            }
        }
    }

    // Fires the scheduler events now due (PPU/SIO callbacks), then activates any DMA their flags requested. Called
    // from the per-cycle loop at the exact event cycle, so PPU register state and timed-DMA triggers land on the
    // ARES cycle. The activated DMA itself runs via RunPending at the next bus access. No bus access happens here,
    // so this never re-enters StepClocks.
    private void FireEvents() {
        m_scheduler.FireDue();
        ActivateTimedDmas();
    }

    // Brings the APU up to the master clock. The APU is sample-generating, not event-scheduled, so it is advanced in
    // coarse spans (each instruction boundary) by however many cycles elapsed — the cycle-critical FIFO/Direct-Sound
    // path is driven separately by the per-cycle timer overflow, not by this.
    private void SyncApu() {
        var delta = m_scheduler.Now - m_apuClock;

        if (delta > 0) {
            m_apu.Step(cycles: (int)delta);
            m_apuClock = m_scheduler.Now;
        }
    }

    /// <inheritdoc/>
    public void ProcessEvents() {
        // Events fire at their exact cycle inside StepClocks now; at the instruction boundary the CPU only needs the
        // APU brought current, plus a safety sweep of anything due exactly now.
        SyncApu();

        if (m_scheduler.NextWhen <= m_scheduler.Now) {
            FireEvents();
        }
    }

    // ARES calls dmac.runPending() at the start of every CPU bus access (getBus/setBus/sleep): a DMA queued by a
    // trigger runs its whole burst HERE, just before the CPU touches the bus, with the CPU stalled — so the burst's
    // cycles and its completion IRQ are charged to the consuming instruction. m_dmaActive guards re-entry from the
    // DMA's own accesses and marks those accesses as DMA (for EEPROM routing).
    private void RunPendingDma() {
        if (m_dmaActive) {
            return;
        }

        m_dmaActive = true;

        // A drained Direct-Sound FIFO (flagged by the per-cycle timer overflow that clocked the APU) refills via a
        // DMA transfer; run it here on the consuming access, like any other pending DMA. OnFifo does bus accesses,
        // so it must run on this access path (not inside the per-cycle event loop) to avoid re-entering StepClocks.
        if (m_apu.ConsumeFifoARefill()) {
            m_dma.OnFifo(fifo: 0, bus: this);
        }

        if (m_apu.ConsumeFifoBRefill()) {
            m_dma.OnFifo(fifo: 1, bus: this);
        }

        m_dma.RunPending(bus: this);
        m_dmaActive = false;
    }

    /// <inheritdoc/>
    public byte Read8(uint address, BusAccessType access) {
        RunPendingDma();
        BusTrace(op: 'R', address: address, width: 1, access: access);
        var value = (byte)ReadRegion(address: address, width: 1, access: access);

        ChargeData(address: address, cost: AccessCycles(address: address, width: 1, access: access));

        return value;
    }

    /// <inheritdoc/>
    public ushort Read16(uint address, BusAccessType access) {
        RunPendingDma();
        BusTrace(op: 'R', address: address, width: 2, access: access);
        var value = (ushort)ReadRegion(address: address & ~1u, width: 2, access: access);

        ChargeData(address: address, cost: AccessCycles(address: address, width: 2, access: access));

        return value;
    }

    /// <inheritdoc/>
    public uint Read32(uint address, BusAccessType access) {
        RunPendingDma();
        BusTrace(op: 'R', address: address, width: 4, access: access);
        var value = ReadRegion(address: address & ~3u, width: 4, access: access);

        ChargeData(address: address, cost: AccessCycles(address: address, width: 4, access: access));

        return value;
    }

    /// <inheritdoc/>
    public ushort ReadCode16(uint address, BusAccessType access) {
        RunPendingDma();
        BusTrace(op: 'F', address: address, width: 2, access: access);
        m_executingInBios = address < 0x4000u;
        var region = address >> 24;

        if (!s_disablePrefetch && m_prefetchEnabled && region >= 0x08u && region <= 0x0Du) {
            if ((address & 0x1FFFEu) != 0
                && address == m_prefetchAddr
                && (!PrefetchEmpty || m_prefetchAhead)) {
                PrefetchStep(1);
                var (hit, extra) = PrefetchRead();
                StepClocks(1 + extra);
                return hit;
            }

            var syncExtra = PrefetchSync(address, 2);
            m_inCodeFetch = true;
            var miss = (ushort)ReadRegion(address: address & ~1u, width: 2, access: access);
            m_inCodeFetch = false;
            var missCost = AccessCycles(address: address, width: 2, access: access);

            // The prefetch unit cannot run ahead while the CPU is using the ROM bus for this fetch, so the miss
            // only advances the clock (it does NOT fill the buffer). ARES uses step() here, not prefetchStep — and
            // only HITS (above) and non-ROM/idle cycles fill the buffer. Filling here caused tight ROM loops to
            // see a false prefetch speed-up (micro3: 7 vs ARES's 11 cyc/iter).
            StepClocks(missCost + syncExtra);
            return miss;
        }

        m_inCodeFetch = true;
        var value = (ushort)ReadRegion(address: address & ~1u, width: 2, access: access);
        m_inCodeFetch = false;
        var cost = AccessCycles(address: address, width: 2, access: access);
        if (!s_disablePrefetch) {
            PrefetchStep(cost);
        }

        StepClocks(cost);
        return value;
    }

    /// <inheritdoc/>
    public uint ReadCode32(uint address, BusAccessType access) {
        RunPendingDma();
        BusTrace(op: 'F', address: address, width: 4, access: access);
        m_executingInBios = address < 0x4000u;
        var region = address >> 24;

        if (!s_disablePrefetch && m_prefetchEnabled && region >= 0x08u && region <= 0x0Du) {
            if ((address & 0x1FFFEu) != 0
                && address == m_prefetchAddr
                && (!PrefetchEmpty || m_prefetchAhead)) {
                PrefetchStep(1);
                var (lo, extra1) = PrefetchRead();
                var (hi, extra2) = PrefetchRead();
                StepClocks(1 + extra1 + extra2);
                return (uint)(lo | (hi << 16));
            }

            var syncExtra = PrefetchSync(address & ~3u, 4);
            m_inCodeFetch = true;
            var miss = ReadRegion(address: address & ~3u, width: 4, access: access);
            m_inCodeFetch = false;
            var missCost = AccessCycles(address: address, width: 4, access: access);

            // See ReadCode16: a ROM-bus miss advances the clock but does NOT fill the prefetch buffer (ARES step()).
            StepClocks(missCost + syncExtra);
            return miss;
        }

        m_inCodeFetch = true;
        var value = ReadRegion(address: address & ~3u, width: 4, access: access);
        m_inCodeFetch = false;
        var cost = AccessCycles(address: address, width: 4, access: access);
        if (!s_disablePrefetch) {
            PrefetchStep(cost);
        }

        StepClocks(cost);
        return value;
    }

    /// <inheritdoc/>
    public void Write8(uint address, byte value, BusAccessType access) {
        RunPendingDma();
        BusTrace(op: 'W', address: address, width: 1, access: access);
        WriteRegion(address: address, width: 1, value: value);
        ChargeData(address: address, cost: AccessCycles(address: address, width: 1, access: access));
    }

    /// <inheritdoc/>
    public void Write16(uint address, ushort value, BusAccessType access) {
        RunPendingDma();
        BusTrace(op: 'W', address: address, width: 2, access: access);
        // Pass the raw (unaligned) address: most regions force alignment internally, but the 8-bit save bus must
        // see the low bit to pick which byte of the value it stores and where.
        WriteRegion(address: address, width: 2, value: value);
        ChargeData(address: address, cost: AccessCycles(address: address, width: 2, access: access));
    }

    /// <inheritdoc/>
    public void Write32(uint address, uint value, BusAccessType access) {
        RunPendingDma();
        BusTrace(op: 'W', address: address, width: 4, access: access);
        WriteRegion(address: address, width: 4, value: value);
        ChargeData(address: address, cost: AccessCycles(address: address, width: 4, access: access));
    }

    /// <inheritdoc/>
    public void Idle(int cycles) {
        RunPendingDma();

        if (s_busTrace) {
            Console.Error.WriteLine($"  c={m_scheduler.Now} I x{cycles}");
        }

        if (!s_disablePrefetch) {
            PrefetchStep(cycles);
        }

        StepClocks(cycles);
    }

    /// <inheritdoc/>
    public bool Halted => m_halted;

    /// <inheritdoc/>
    public void Halt(bool stop) {
        // STOP mode powers down the LCD/sound and only wakes on keypad/cartridge IRQ.
        // We treat it as HALT — the CPU sleeps until any enabled interrupt fires.
        m_halted = true;
    }

    /// <inheritdoc/>
    public void RunUntilInterrupt() {
        // ARES steps the halted CPU one cycle at a time, waking on enable[0] & flag[0] (cpu.cpp:46-53). We do the
        // same per-cycle stepping whenever something can change the IRQ state on the next cycle (a running timer, a
        // pending latch, or an un-propagated pipeline shift), but jump straight to the next scheduled event over
        // genuinely idle spans — the common VBlank-wait case, where only a PPU/DMA/SIO event can wake us.
        while (true) {
            if (m_interrupts.HasPendingInterrupt) {
                break;
            }

            if (m_timers.HasRunningTimer || m_timers.HasPendingLatch || !m_interrupts.PipelineQuiescent) {
                StepClocks(1);
            }
            else {
                // Nothing per-cycle can change the IRQ state; jump to the next scheduled event (the wake source for a
                // V-blank-style wait). Cap to a frame so a pathological no-event halt still re-checks periodically.
                var next = m_scheduler.NextWhen - m_scheduler.Now;

                StepClocks((next <= 0L) ? 1 : (int)Math.Min(next, 280_896L));
            }

            ProcessEvents();

            // ARES runs pending DMA during halt too (cpu.cpp:47 dmac.runPending). A timed DMA queued by a PPU event
            // while the CPU is halted (e.g. a VBlank copy during a VBlank-wait) must still run, since there are no
            // CPU bus accesses to drive RunPendingDma here.
            RunPendingDma();
        }

        // ARES wakes a halted CPU with step(2) before resuming (cpu.cpp:51) — two cycles charged after the wake
        // condition is met and before the first post-halt instruction. Without this every IntrWait leaves the clock
        // two cycles ahead of ARES, which accumulates and breaks timing-paced boot loops (Pokémon Emerald).
        StepClocks(2);

        m_halted = false;
    }

    // A PPU event (V-blank/H-blank/video-capture start/end) fired at its exact cycle marks the matching timed-DMA
    // channels PENDING. This only flips activation flags — no bus access — so it is safe to call from the per-cycle
    // event loop (FireEvents) without re-entering StepClocks. The activated burst then runs via RunPending at the
    // CPU's next bus access (ARES's model). The Direct-Sound FIFO refill, which does do bus accesses, is handled
    // separately on the access path in RunPendingDma.
    private void ActivateTimedDmas() {
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
    }

    private uint ReadRegion(uint address, int width, BusAccessType access) {
        // Callers pre-align wide reads to their natural boundary — INCLUDING the save region: a wide read of the
        // 8-bit save bus fetches the byte at the ALIGNED address and repeats it (proven by the mGBA Memory suite;
        // raw-address fetch regresses it by 12. The store path differs — WriteRegion passes the raw address there).
        var value = (address >> 24) switch {
            RegionBios => ReadBios(address: address, width: width),
            RegionEwram => ReadArray(array: m_ewram, index: address & 0x3FFFFu, width: width),
            RegionIwram => ReadArray(array: m_iwram, index: address & 0x7FFFu, width: width),
            RegionIo => ReadIo(address: address, width: width),
            RegionPalette or RegionVram or RegionOam => m_ppu.ReadVideo(address: address, width: width),
            0x8 or 0x9 or 0xA or 0xB or 0xC or 0xD => ReadRom(address: address, width: width, access: access),
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

    private uint ReadRom(uint address, int width, BusAccessType access) {
        // A serial EEPROM responds in the upper ROM region (0x0D…) to DMA accesses only (it is DMA-driven on
        // hardware). A CPU read there hits the ROM mirror/open-bus — so a cart that merely embeds the EEPROM_V
        // string (e.g. the AGS aging cartridge) never has its 0x0D region hijacked.
        if (m_dmaActive && m_cartridge.IsEeprom && ((address >> 24) == 0x0Du)) {
            return m_cartridge.ReadEeprom();
        }

        var offset = address & 0x01FFFFFFu;

        // DMA reads go through the cartridge's burst page counter, so a fixed/decrement source still reads the
        // auto-incrementing data (mGBA "ROM load DMA" tests). CPU code/data reads stay raw (their values already
        // resolve to the requested address, and routing them through the shared counter would corrupt a code fetch
        // that interleaves with a data read). A 32-bit access is two half-word burst steps (first per access, second
        // sequential within the word).
        if (m_dmaActive) {
            var sequential = access == BusAccessType.Sequential;

            switch (width) {
                case 1: {
                    var half = m_cartridge.ReadRomBurst(address: offset & ~1u, sequential: sequential);

                    return ((offset & 1u) == 0u) ? (uint)(half & 0xFFu) : (uint)(half >> 8);
                }
                case 2:
                    return m_cartridge.ReadRomBurst(address: offset, sequential: sequential);
                default: {
                    var low = m_cartridge.ReadRomBurst(address: offset, sequential: sequential);
                    var high = m_cartridge.ReadRomBurst(address: offset + 2u, sequential: true);

                    return (uint)(low | ((uint)high << 16));
                }
            }
        }

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

    // The open bus seen when reading a write-only or unmapped I/O register: the most recent value on the CPU bus
    // (the last ReadRegion result, i.e. the prefetched opcode), selected to the accessed halfword — matching ARES's
    // `mdr >> (8*(addr&3))`.
    private ushort OpenBusHalf(uint offset) => (ushort)(m_openBus >> (int)((offset & 2u) * 8u));

    // The I/O page is a 16-bit space; reads and writes of other widths decompose into halfword accesses, which
    // are routed to the owning peripheral or fall through to the open bus (NOT the last written value).
    private ushort ReadIoHalf(uint offset) {
        if (offset >= 0x400u) {
            return 0;
        }

        if (offset < 0x58u) {
            // Readable PPU registers: DISPCNT/GRSWP/DISPSTAT/VCOUNT/BGxCNT (≤0x0E), WININ/WINOUT (0x48/0x4A),
            // BLDCNT/BLDALPHA (0x50/0x52). All others (BG scroll/affine refs, window dims, MOSAIC, BLDY) are
            // write-only and read back as open bus on hardware (verified against ARES gba/ppu/io.cpp).
            var readable = (offset <= 0x0Eu) || (offset == 0x48u) || (offset == 0x4Au) || (offset == 0x50u) || (offset == 0x52u);

            return readable ? m_ppu.ReadRegister(offset: offset) : OpenBusHalf(offset: offset);
        }

        if ((offset >= 0x60u) && (offset < 0xA8u)) {
            // The SOUNDCNT_X gap (0x8C-0x8F) and the Direct Sound FIFOs (0xA0-0xA7) are write-only/unmapped → open
            // bus (ARES gba/apu/io.cpp has no read case for them → mdr default). Wave RAM (0x90-0x9F) stays readable.
            if (((offset >= 0x8Cu) && (offset < 0x90u)) || (offset >= 0xA0u)) {
                return OpenBusHalf(offset: offset);
            }

            return m_apu.ReadRegister(offset: offset);
        }

        if ((offset >= 0x100u) && (offset < 0x110u)) {
            return m_timers.ReadRegister(offset: offset);
        }

        if ((offset >= 0xB0u) && (offset < 0xE0u)) {
            // Only each DMA channel's control halfword (DMAxCNT_H) is readable; SAD/DAD/CNT_L are write-only and
            // read back as open bus (ARES gba/cpu/io.cpp: DMA SAD/DAD have no readIO case → mdr; CNT_L → 0... but
            // hardware/ARES return open bus for the unlatched address regs). CNT_H is offset 0xA within the 12-byte channel.
            return (((offset - 0xB0u) % 12u) == 10u) ? m_dma.ReadRegister(offset: offset) : OpenBusHalf(offset: offset);
        }

        if ((offset == 0x200u) || (offset == 0x202u) || (offset == 0x208u)) {
            return m_interrupts.ReadRegister(offset: offset);
        }

        switch (offset) {
            // Serial subsystem (SIO 0x120-0x12A, RCNT 0x134, JOY 0x140/0x150-0x158).
            case 0x120u:
            case 0x122u:
            case 0x124u:
            case 0x126u:
            case 0x128u:
            case 0x12Au:
            case 0x134u:
            case 0x140u:
            case 0x150u:
            case 0x152u:
            case 0x154u:
            case 0x156u:
            case 0x158u:
                return m_serial.ReadRegister(offset: offset);
            case 0x132u: return m_keyControl; // KEYCNT (keypad IRQ control)
            case 0x130u: return (ushort)((m_io[0x131] << 8) | m_io[0x130]); // KEYINPUT
            case 0x204u: return (ushort)((m_io[0x205] << 8) | m_io[0x204]);  // WAITCNT (fully readable)
            case 0x300u: return m_postFlag; // POSTFLG (bit 0); HALTCNT (high byte) is write-only, reads 0
        }

        // Unmapped and write-only I/O registers are NOT backed by a readable latch: on hardware they return the
        // open bus (the most recent value on the CPU bus, i.e. the prefetched opcode), NOT the last written value.
        return OpenBusHalf(offset: offset);
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
            // Serial subsystem (SIO 0x120-0x12A, RCNT 0x134, JOY 0x140/0x150-0x158).
            case 0x120u:
            case 0x122u:
            case 0x124u:
            case 0x126u:
            case 0x128u:
            case 0x12Au:
            case 0x134u:
            case 0x140u:
            case 0x150u:
            case 0x152u:
            case 0x154u:
            case 0x156u:
            case 0x158u:
                m_serial.WriteRegister(offset: offset, value: value);
                return;
            case 0x132u: m_keyControl = value; EvaluateKeypadIrq(); return; // KEYCNT (keypad IRQ control)
            case 0x130u: return; // KEYINPUT is read-only: writes (e.g. a boot-time I/O clear) must NOT overwrite the
                                 // live key state — otherwise it reads back 0x0000 (all buttons "pressed"), and games
                                 // like Pokémon Emerald see the A+B+Start+Select soft-reset combo and reboot forever.
        }

        m_io[offset] = (byte)value;
        m_io[offset + 1u] = (byte)(value >> 8);

        // WAITCNT (0x04000204) reconfigures the game-pak wait-states.
        if (offset == 0x204u) {
            UpdateWaitControl(value: value);
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

    private void ChargeData(uint address, int cost) {
        var region = address >> 24;

        if (region == RegionPalette) {
            // ARES readPRAM/writePRAM (memory.cpp): each halfword access stalls one cycle at a time while the PPU is
            // contending for the palette bus during rendering — do { prefetchStep(1); } while(pramContention()).
            // `cost` is the halfword count (1 for byte/half, 2 for a word on the 16-bit palette bus). This is the one
            // access cost ARES has that Puck lacked, and it is exactly the DMA-into-palette case Emerald's boot hits.
            for (var half = 0; half < cost; ++half) {
                do {
                    if (!s_disablePrefetch) {
                        PrefetchStep(1);
                    }

                    StepClocks(1);
                }
                while (m_ppu.PramContention);
            }

            return;
        }

        if (!s_disablePrefetch) {
            if (m_prefetchEnabled && region >= 0x08u && region <= 0x0Fu) {
                cost += PrefetchReset();
            }
            PrefetchStep(cost);
        }
        StepClocks(cost);
    }

    private static readonly bool s_disablePrefetch = Environment.GetEnvironmentVariable("PUCK_NO_PREFETCH") == "1";

    // Per-access bus trace, mirroring the ARES oracle's ARES_BUSTRACE format, so the two access streams diff
    // directly to localise cycle divergences. Logs the running clock (committed + uncommitted) BEFORE the access.
    private static readonly bool s_busTrace = Environment.GetEnvironmentVariable("PUCK_BUSTRACE") == "1";

    private void BusTrace(char op, uint address, int width, BusAccessType access) {
        if (s_busTrace) {
            Console.Error.WriteLine($"  c={m_scheduler.Now} {op} a={address:X8} w={width} {(access == BusAccessType.Sequential ? "S" : "N")}");
        }
    }

    private bool PrefetchEmpty => m_prefetchLoad == m_prefetchAddr;
    private bool PrefetchFull => (m_prefetchLoad - m_prefetchAddr) >= 16u;

    private int PrefetchSeqWait(uint address) => ((address & 0x0E000000u) switch {
        0x08000000u => m_ws0S,
        0x0A000000u => m_ws1S,
        0x0C000000u => m_ws2S,
        _ => m_ws0S,
    }) + 1;

    private int PrefetchNonSeqWait(uint address) => ((address & 0x0E000000u) switch {
        0x08000000u => m_ws0N,
        0x0A000000u => m_ws1N,
        0x0C000000u => m_ws2N,
        _ => m_ws0N,
    }) + 1;

    private void PrefetchStep(int clocks) {
        if (!m_prefetchEnabled || m_prefetchStopped) {
            return;
        }

        while (clocks-- > 0) {
            if (PrefetchFull) {
                m_prefetchStopped = true;
                m_prefetchAhead = false;
                break;
            }

            m_prefetchAhead = true;

            if (--m_prefetchWait > 0) {
                continue;
            }

            if ((m_prefetchLoad & 0x1FFFEu) != 0) {
                var offset = m_prefetchLoad & 0x01FFFFFFu;
                m_prefetchSlots[(m_prefetchLoad >> 1) & 7] = (ushort)(m_cartridge.ReadRom(offset) | (m_cartridge.ReadRom(offset + 1u) << 8));
                m_prefetchLoad += 2;
            }

            m_prefetchWait = PrefetchSeqWait(m_prefetchLoad);
        }
    }

    private int PrefetchSync(uint address, int width) {
        var extra = 0;

        if (m_prefetchWait == 1) {
            PrefetchStep(1);
            extra = 1;
        }

        var size = (width == 4) ? 4u : 2u;
        m_prefetchStopped = false;
        m_prefetchAhead = false;
        m_prefetchAddr = address + size;
        m_prefetchLoad = address + size;
        m_prefetchWait = PrefetchSeqWait(m_prefetchLoad);
        return extra;
    }

    private (ushort value, int extraCycles) PrefetchRead() {
        var extra = 0;

        if (m_prefetchStopped && PrefetchEmpty) {
            m_prefetchStopped = false;
            m_prefetchWait = PrefetchNonSeqWait(m_prefetchLoad);
        }

        if (PrefetchEmpty) {
            // Capture the wait BEFORE stepping: PrefetchStep loads a slot and resets m_prefetchWait to the NEXT
            // slot's seq-wait, so reading it after would charge the post-reload count (e.g. 3 instead of 1). ARES's
            // prefetchStep(prefetch.wait) advances exactly `wait` clocks and never re-reads the field.
            extra = m_prefetchWait;
            PrefetchStep(extra);
        }

        var word = m_prefetchSlots[(m_prefetchAddr >> 1) & 7];
        m_prefetchAddr += 2;
        return (word, extra);
    }

    private int PrefetchReset() {
        var extra = 0;

        if (m_prefetchWait == 1) {
            PrefetchStep(1);
            extra = 1;
        }

        m_prefetchStopped = true;
        m_prefetchAhead = false;
        m_prefetchWait = 0;
        m_prefetchAddr = 0;
        m_prefetchLoad = 0;
        return extra;
    }

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
                return RomCycles(nonSeq: m_ws0N, seq: m_ws0S, width: width, access: RomBurstAccess(address: address, access: access));
            case 0xA:
            case 0xB:
                return RomCycles(nonSeq: m_ws1N, seq: m_ws1S, width: width, access: RomBurstAccess(address: address, access: access));
            case 0xC:
            case 0xD:
                return RomCycles(nonSeq: m_ws2N, seq: m_ws2S, width: width, access: RomBurstAccess(address: address, access: access));
            case 0xE:
            case 0xF:
                // The 8-bit save bus pays the same first-access overhead as a ROM word access.
                return m_sram + 1;
            default:
                // BIOS, on-chip WRAM, I/O, OAM: single-cycle 32-bit bus.
                return 1;
        }
    }

    // A sequential game-pak access that lands on a 128 KiB page boundary restarts the cartridge's burst (the previous
    // burst ended after the page's last half-word, 0x1FFFE), so it pays a fresh non-sequential first-access cost.
    private static BusAccessType RomBurstAccess(uint address, BusAccessType access) {
        return ((access == BusAccessType.Sequential) && ((address & 0x1FFFEu) == 0u))
            ? BusAccessType.NonSequential
            : access;
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
