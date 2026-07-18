using Microsoft.Extensions.DependencyInjection;

namespace Puck.AdvancedGamingBrick.Post;

/// <summary>
/// The self-authored cycle-oracle probe battery (survey #1). Each probe is a tiny hand-assembled ARM ROM (or a
/// direct component script) that isolates ONE documented Direct-Memory-Access / timer / interrupt / halt behaviour and
/// reports our core's measured value beside the documented hardware measurement. Two probes are self-checking gates
/// whose expected values we derive from the hardware model itself (the Direct Sound FIFO ring/playing split, and the
/// per-channel DMA read-latch isolation); the rest are our-harness measurements against the documented targets from
/// the public hardware-test corpus.
/// <para>
/// Honesty note (the anti-constant-chasing doctrine): a cycle-count divergence here is EVIDENCE, not a fail signal to
/// tune to. Our probe harness is not the external corpus's harness, so a documented number and ours can legitimately
/// differ by the surrounding instruction shape without our core being wrong. Divergences are recorded, never chased;
/// the two SELF-CHECK gates are the only hard pass/fail rows.
/// </para>
/// </summary>
internal static class OracleProbes {
    private const uint IoBase = 0x04000000u;
    private const uint ResultBase = 0x02000000u;

    /// <summary>Runs every probe against our core and prints the measured-vs-documented table. Returns 0 (the two
    /// self-checking gates gate; the measurement rows never fail the process — they are evidence).</summary>
    public static int RunOracle(string[] args) {
        var bios = Diagnostics.BiosImage;
        var identity = AgbBiosProfile.Identify(image: bios.Span);
        var hasRetailBios = identity.IsCycleParityTrustworthy;

        Console.WriteLine(value: "== AGB cycle-oracle probes (measured vs documented) ==");
        Console.WriteLine(value: $"   BIOS: {identity.Description}{(hasRetailBios ? string.Empty : "  (interrupt/halt-region probes need the retail BIOS — they will skip)")}");
        Console.WriteLine(value: "   rows marked [gate] are self-checking; the rest are our-harness measurements vs the documented corpus targets.");
        Console.WriteLine();

        var gateFailures = 0;

        // --- Self-checking gate: the hardware-measured Direct Sound FIFO model (survey #4/#5). ------------------------
        var (fifoOk, fifoLines) = FifoModelProbe();

        Console.WriteLine(value: $"[gate] apu/fifo-model .......... {(fifoOk ? "PASS" : "FAIL")}");

        foreach (var line in fifoLines) {
            Console.WriteLine(value: $"          {line}");
        }

        gateFailures += (fifoOk ? 0 : 1);

        // --- Self-checking gate: per-channel DMA read-latch isolation (survey #7). ----------------------------------
        var latch = RunRom(rom: BuildDmaLatchProbe(), resultCount: 2, hasRetailBios: hasRetailBios, needsBios: false);
        var latchOk = (latch is [0x00000000u, 0xAABBCCDDu]);

        Console.WriteLine(value: $"[gate] dma/latch-per-channel ... {(latchOk ? "PASS" : "FAIL")}");
        Console.WriteLine(value: $"          ch0 undrivable-source read = 0x{((latch is null) ? 0 : latch[0]):X8} (expect 0x00000000: ch0's own latch, NOT ch1's 0xAABBCCDD — proves per-channel)");
        Console.WriteLine(value: $"          ch1 drivable transfer      = 0x{((latch is null) ? 0 : latch[1]):X8} (expect 0xAABBCCDD: confirms ch1 actually ran)");
        gateFailures += (latchOk ? 0 : 1);

        // --- Measurement rows: our value beside the documented corpus target. ---------------------------------------
        Console.WriteLine();
        Console.WriteLine(value: "   -- measurement rows (evidence; divergence recorded, not chased) --");

        Measure(name: "dma/start-delay", documented: "20", rom: BuildDmaStartDelayProbe(), resultIndex: 0, hasRetailBios: hasRetailBios, needsBios: false);
        Measure(name: "dma/force-nseq", documented: "88", rom: BuildDmaForceNseqProbe(), resultIndex: 0, hasRetailBios: hasRetailBios, needsBios: false);
        Measure(name: "timer/start-stop", documented: "3 then frozen 8", rom: BuildTimerStartStopProbe(), resultIndex: -1, hasRetailBios: hasRetailBios, needsBios: false);
        Measure(name: "timer/reload-race", documented: "0xDEAE / 0xFFF9 boundary", rom: BuildTimerReloadRaceProbe(), resultIndex: -1, hasRetailBios: hasRetailBios, needsBios: false);
        Measure(name: "irq/dispatch (ROM handler)", documented: "120 (region-dependent 92/112/120)", rom: BuildIrqLatencyProbe(), resultIndex: 0, hasRetailBios: hasRetailBios, needsBios: true);
        Measure(name: "haltcnt/exit (direct)", documented: "12", rom: BuildHaltExitProbe(), resultIndex: 0, hasRetailBios: hasRetailBios, needsBios: true);

        Console.WriteLine();
        Console.WriteLine(value: $"== oracle: {((gateFailures == 0) ? "gates PASS" : $"{gateFailures} GATE FAILURE(S)")} — measurement rows are evidence only ==");

        return ((gateFailures == 0) ? 0 : 1);
    }

    // Runs a measurement probe and prints "measured vs documented". resultIndex >= 0 reads one masked-16 timer value;
    // resultIndex == -1 prints both 32-bit result words (for probes that store two values).
    private static void Measure(string name, string documented, byte[] rom, int resultIndex, bool hasRetailBios, bool needsBios) {
        if (needsBios && !hasRetailBios) {
            Console.WriteLine(value: $"   {name,-30} measured=SKIP (needs retail BIOS)   documented={documented}");

            return;
        }

        var results = RunRom(rom: rom, resultCount: ((resultIndex < 0) ? 2 : 1), hasRetailBios: hasRetailBios, needsBios: needsBios);

        if (results is null) {
            Console.WriteLine(value: $"   {name,-30} measured=SKIP   documented={documented}");

            return;
        }

        var measured = ((resultIndex < 0)
            ? $"0x{results[0] & 0xFFFFu:X4}, 0x{results[1] & 0xFFFFu:X4}"
            : $"{results[resultIndex] & 0xFFFFu}");

        Console.WriteLine(value: $"   {name,-30} measured={measured,-18} documented={documented}");
    }

    // Builds a direct-booted machine over the probe ROM, runs it to its spin loop (or a step cap), and reads the
    // result words the ROM stored to EWRAM (0x02000000+). Returns null when the ROM could not run.
    private static uint[]? RunRom(byte[] rom, int resultCount, bool hasRetailBios, bool needsBios) {
        _ = needsBios;

        var cartridge = new AgbCartridge(rom: rom);
        var services = new ServiceCollection();

        _ = services.AddAdvancedGamingBrick();
        _ = services.AddReplacementBios(image: Diagnostics.BiosImage);
        services.AddScoped<AgbCartridge>(implementationFactory: _ => cartridge);

        using var provider = services.BuildServiceProvider();
        var machine = provider.CreateScope().ServiceProvider.GetRequiredService<AdvancedGamingBrickMachine>();
        var bus = (AgbBus)machine.Bus;

        machine.DirectBoot();

        // Step to the ROM's spin loop (a stable PC), capped so a mis-assembled ROM cannot hang the battery.
        var lastPc = 0xFFFFFFFFu;
        var stable = 0;

        for (var i = 0; (i < 2_000_000); ++i) {
            var pc = machine.Cpu.GetRegister(index: 15);

            machine.Step();

            if (pc == lastPc) {
                if (++stable > 8) {
                    break;
                }
            } else {
                stable = 0;
            }

            lastPc = pc;
        }

        var results = new uint[resultCount];

        for (var i = 0; (i < resultCount); ++i) {
            results[i] = bus.DebugRead32(address: (ResultBase + (uint)(i * 4)));
        }

        return results;
    }

    // -------------------------------------------------------------------------------------------------------------
    // Component-level probe: the hardware-measured Direct Sound FIFO (7-word ring + 32-bit playing buffer). We drive
    // AgbApu directly and assert the model's documented properties. Expected values are DERIVED FROM THE MODEL SPEC, so
    // this is a true self-checking gate.
    // -------------------------------------------------------------------------------------------------------------
    private static (bool ok, List<string> lines) FifoModelProbe() {
        var lines = new List<string>();
        var ok = true;

        var apu = new AgbApu();

        apu.ConfigureOutput(sampleRate: 0);       // no host sampling — isolate the FIFO logic
        apu.WriteRegister(offset: 0x84u, value: 0x80); // master enable (gate open)
        apu.WriteRegister(offset: 0x82u, value: 0x0000); // SOUNDCNT_H: timer 0 selects both Direct Sound channels

        void Check(string what, bool condition) {
            ok &= condition;
            lines.Add(item: $"[{(condition ? "ok" : "XX")}] {what}");
        }

        void Reset() => apu.WriteRegister(offset: 0x82u, value: 0x8800); // bits 11 + 15 reset FIFO A + B

        void WriteWord(uint word) {
            for (var i = 0; (i < 4); ++i) {
                apu.WriteFifoByte(fifo: 0, value: (byte)(word >> (8 * i)));
            }
        }

        // (1) Ring capacity + narrow (partial-word) fill.
        Reset();
        Check(what: "reset clears ring + playing", ((apu.DebugFifoWordCount(fifo: 0) == 0) && (apu.DebugFifoPlayingBytes(fifo: 0) == 0)));
        apu.WriteFifoByte(fifo: 0, value: 0x11);
        apu.WriteFifoByte(fifo: 0, value: 0x22);
        apu.WriteFifoByte(fifo: 0, value: 0x33);
        Check(what: "3 bytes = partial word, ring still empty", (apu.DebugFifoWordCount(fifo: 0) == 0));
        apu.WriteFifoByte(fifo: 0, value: 0x44);
        Check(what: "4th byte completes one ring word", (apu.DebugFifoWordCount(fifo: 0) == 1));

        // (2) The DAC drains the completed word LSB-first out of the playing buffer, one byte per timer overflow.
        apu.OnTimerOverflow(timer: 0);
        Check(what: "overflow refills playing buffer + outputs low byte 0x11", (apu.DebugDirectSound(fifo: 0) == 0x11));
        Check(what: "playing buffer holds the remaining 3 bytes", (apu.DebugFifoPlayingBytes(fifo: 0) == 3));
        apu.OnTimerOverflow(timer: 0);
        Check(what: "next overflow outputs 0x22", (apu.DebugDirectSound(fifo: 0) == 0x22));
        apu.OnTimerOverflow(timer: 0);
        apu.OnTimerOverflow(timer: 0);
        Check(what: "fourth overflow outputs 0x44 (word drained)", (apu.DebugDirectSound(fifo: 0) == 0x44));
        _ = apu.ConsumeFifoARefill();

        // (3) DMA-request threshold: the ring must have >= 4 EMPTY words (i.e. <= 3 filled) at overflow.
        Reset();
        WriteWord(word: 0);
        WriteWord(word: 0);
        WriteWord(word: 0); // 3 filled → 4 empty → request expected
        apu.OnTimerOverflow(timer: 0);
        Check(what: "3 filled words (>=4 empty) requests a DMA top-up", apu.ConsumeFifoARefill());
        Reset();
        WriteWord(word: 0);
        WriteWord(word: 0);
        WriteWord(word: 0);
        WriteWord(word: 0); // 4 filled → 3 empty → no request
        apu.OnTimerOverflow(timer: 0);
        Check(what: "4 filled words (<4 empty) requests NO DMA top-up", !apu.ConsumeFifoARefill());

        // (4) The load-bearing invariant: two DMA requests cannot occur without an intervening timer overflow.
        Reset();
        apu.OnTimerOverflow(timer: 0);
        var first = apu.ConsumeFifoARefill();
        var second = apu.ConsumeFifoARefill(); // no overflow between the two consumes

        Check(what: "invariant: no second DMA request without an intervening overflow", (first && !second));

        // (5) Write overrun auto-resets the FIFO to empty (drops buffered samples, does not wrap).
        Reset();

        for (var i = 0; (i < 7); ++i) {
            WriteWord(word: 0x01020304u);
        }

        Check(what: "ring fills to its 7-word capacity", (apu.DebugFifoWordCount(fifo: 0) == 7));
        WriteWord(word: 0x0A0B0C0Du); // the 8th word overruns
        Check(what: "overrun auto-resets the FIFO to empty", (apu.DebugFifoWordCount(fifo: 0) == 0));

        return (ok, lines);
    }

    // -------------------------------------------------------------------------------------------------------------
    // ROM probe builders. Each is a direct-boot ARM ROM: it stores its result word(s) to EWRAM 0x02000000+ and spins.
    // -------------------------------------------------------------------------------------------------------------

    // Per-channel DMA read-latch isolation. Channel 1 runs a DRIVABLE transfer (loads a value into channel 1's latch),
    // then channel 0 runs an UNDRIVABLE (BIOS-region source) transfer whose destination therefore receives CHANNEL 0's
    // own open-bus latch. With per-channel latches that value is 0 (ch0 never drove its latch); with a single shared
    // latch it would be channel 1's 0xAABBCCDD. Result[0] = ch0 dest, Result[1] = ch1 dest.
    private static byte[] BuildDmaLatchProbe() {
        var a = new Asm();

        a.LdrConst(rd: 0, value: IoBase);
        // Seed the channel-1 source word in EWRAM.
        a.LdrConst(rd: 1, value: 0xAABBCCDDu);
        a.LdrConst(rd: 2, value: 0x02000100u);
        a.Str(rd: 1, rn: 2, imm12: 0);
        // Channel 1 (regs at 0xBC/0xC0/0xC4/0xC6): SAD=0x02000100, DAD=0x02000004, count 1, 32-bit immediate enable.
        a.LdrConst(rd: 1, value: 0x02000100u);
        a.Str(rd: 1, rn: 0, imm12: 0xBC);
        a.LdrConst(rd: 1, value: 0x02000004u);
        a.Str(rd: 1, rn: 0, imm12: 0xC0);
        a.LdrConst(rd: 1, value: (0x8400u << 16) | 1u); // CNT_L=1, CNT_H=0x8400 (enable + 32-bit + immediate)
        a.Str(rd: 1, rn: 0, imm12: 0xC4);
        // Channel 0 (regs at 0xB0/0xB4/0xB8/0xBA): SAD=0x00000000 (BIOS, undrivable), DAD=0x02000000, count 1, 32-bit.
        a.LdrConst(rd: 1, value: 0x00000000u);
        a.Str(rd: 1, rn: 0, imm12: 0xB0);
        a.LdrConst(rd: 1, value: 0x02000000u);
        a.Str(rd: 1, rn: 0, imm12: 0xB4);
        a.LdrConst(rd: 1, value: (0x8400u << 16) | 1u);
        a.Str(rd: 1, rn: 0, imm12: 0xB8);
        // Give the pending channel-0 DMA a bus access to run on, then spin.
        a.Nop();
        a.Spin();

        return a.Finish();
    }

    // Timer0 (÷1) enabled, then an immediate 4-word DMA is triggered; the timer is read right after. The measured
    // value reflects the DMA start delay + burst cycles. Documented corpus target: 20.
    private static byte[] BuildDmaStartDelayProbe() {
        var a = new Asm();

        a.LdrConst(rd: 0, value: IoBase);
        a.LdrConst(rd: 1, value: 0x00800000u);          // TM0: reload 0, control 0x80 (enable, ÷1)
        a.Str(rd: 1, rn: 0, imm12: 0x100);
        // Immediate DMA3 (regs 0xD4/0xD8/0xDC/0xDE): EWRAM→EWRAM, 4 words, 32-bit.
        a.LdrConst(rd: 1, value: 0x02000100u);
        a.Str(rd: 1, rn: 0, imm12: 0xD4);
        a.LdrConst(rd: 1, value: 0x02000200u);
        a.Str(rd: 1, rn: 0, imm12: 0xD8);
        a.LdrConst(rd: 1, value: (0x8400u << 16) | 4u);
        a.Str(rd: 1, rn: 0, imm12: 0xDC);
        a.Ldr(rd: 1, rn: 0, imm12: 0x100);              // read TM0COUNT
        a.LdrConst(rd: 2, value: ResultBase);
        a.Str(rd: 1, rn: 2, imm12: 0);
        a.Spin();

        return a.Finish();
    }

    // Timer0 (÷1) enabled, an immediate 1-word DMA runs, then a plain memory access follows: hardware forces the
    // instruction fetch after a DMA non-sequential. The timer read afterward captures the combined cost. Target: 88.
    private static byte[] BuildDmaForceNseqProbe() {
        var a = new Asm();

        a.LdrConst(rd: 0, value: IoBase);
        a.LdrConst(rd: 1, value: 0x00800000u);
        a.Str(rd: 1, rn: 0, imm12: 0x100);
        a.LdrConst(rd: 1, value: 0x02000100u);
        a.Str(rd: 1, rn: 0, imm12: 0xD4);
        a.LdrConst(rd: 1, value: 0x02000200u);
        a.Str(rd: 1, rn: 0, imm12: 0xD8);
        a.LdrConst(rd: 1, value: (0x8400u << 16) | 1u);
        a.Str(rd: 1, rn: 0, imm12: 0xDC);
        a.Nop();
        a.Nop();
        a.Nop();
        a.Ldr(rd: 1, rn: 0, imm12: 0x100);
        a.LdrConst(rd: 2, value: ResultBase);
        a.Str(rd: 1, rn: 2, imm12: 0);
        a.Spin();

        return a.Finish();
    }

    // Enable timer0 (÷1), read it a few cycles later (result 0), then STOP it and read again (result 1, frozen).
    // Documented: reads ~3 while running, then a frozen value (corpus target 8).
    private static byte[] BuildTimerStartStopProbe() {
        var a = new Asm();

        a.LdrConst(rd: 0, value: IoBase);
        a.LdrConst(rd: 1, value: 0x00800000u);          // enable ÷1
        a.Str(rd: 1, rn: 0, imm12: 0x100);
        a.Ldr(rd: 3, rn: 0, imm12: 0x100);              // read running counter → result 0
        a.LdrConst(rd: 2, value: ResultBase);
        a.Str(rd: 3, rn: 2, imm12: 0);
        a.Mov(rd: 1, imm8: 0);
        a.Str(rd: 1, rn: 0, imm12: 0x100);              // reload 0, control 0 → stop (freezes the counter)
        a.Ldr(rd: 3, rn: 0, imm12: 0x100);              // read frozen counter → result 1
        a.Str(rd: 3, rn: 2, imm12: 4);
        a.Spin();

        return a.Finish();
    }

    // Timer0 near overflow (reload 0xFFF0), let it run, then write a new reload while live and read the counter: probes
    // whether the live counter or the freshly written reload wins at the boundary. Documented boundary: 0xDEAE/0xFFF9.
    private static byte[] BuildTimerReloadRaceProbe() {
        var a = new Asm();

        a.LdrConst(rd: 0, value: IoBase);
        a.LdrConst(rd: 1, value: (0x0080u << 16) | 0xFFF0u); // reload 0xFFF0, enable ÷1
        a.Str(rd: 1, rn: 0, imm12: 0x100);
        a.Nop();
        a.Nop();
        a.Ldr(rd: 3, rn: 0, imm12: 0x100);              // read live counter → result 0
        a.LdrConst(rd: 2, value: ResultBase);
        a.Str(rd: 3, rn: 2, imm12: 0);
        a.LdrConst(rd: 1, value: 0xDEAEu);              // write a new reload low half while live
        a.Str(rd: 1, rn: 0, imm12: 0x100);
        a.Ldr(rd: 3, rn: 0, imm12: 0x100);              // read again → result 1
        a.Str(rd: 3, rn: 2, imm12: 4);
        a.Spin();

        return a.Finish();
    }

    // Timer0 raises an IRQ; a ROM handler (installed at the BIOS user-vector 0x03007FFC) reads timer1 on entry to
    // capture the elapsed cycles through the BIOS dispatch path. timer1 is (re)started to 0 immediately before arming
    // timer0 so the reading isolates the dispatch window as closely as this coarse harness allows. This is a coarse
    // MEASUREMENT (our harness shape, not the corpus's) against the documented region-dependent latency 92/112/120;
    // it needs the retail BIOS IRQ vector to dispatch at all.
    private static byte[] BuildIrqLatencyProbe() {
        var a = new Asm();

        a.LdrConst(rd: 0, value: IoBase);
        a.LdrLabel(rd: 1, label: "handler");
        a.LdrConst(rd: 2, value: 0x03007FFCu);
        a.Str(rd: 1, rn: 2, imm12: 0);
        a.Mov(rd: 1, imm8: 1);
        a.Str(rd: 1, rn: 0, imm12: 0x208);              // IME = 1
        a.Mov(rd: 1, imm8: 8);
        a.Str(rd: 1, rn: 0, imm12: 0x200);              // IE = timer0
        a.LdrConst(rd: 1, value: 0x00800000u);          // timer1 ÷1 free-running (the latency clock), started at 0
        a.Str(rd: 1, rn: 0, imm12: 0x104);
        a.LdrConst(rd: 1, value: (0x00C0u << 16) | 0xFFFFu); // timer0 reload 0xFFFF, enable + IRQ, ÷1 (overflows next)
        a.Str(rd: 1, rn: 0, imm12: 0x100);
        a.Spin();

        a.Label(name: "handler");
        a.Ldr(rd: 1, rn: 0, imm12: 0x104);              // read TM1COUNT (elapsed through the dispatch path)
        a.LdrConst(rd: 2, value: ResultBase);
        a.Str(rd: 1, rn: 2, imm12: 0);
        a.Mov(rd: 1, imm8: 0);
        a.Str(rd: 1, rn: 0, imm12: 0x100);              // disable timer0 so it fires ONCE (no IRQ storm)
        a.LdrConst(rd: 1, value: 0x00080008u);
        a.Str(rd: 1, rn: 0, imm12: 0x200);              // ack timer0
        a.Bx(rn: 14);

        return a.Finish();
    }

    // Enable timer0 (÷1), HALT (write HALTCNT=0), and immediately raise a pending timer IRQ so the CPU wakes; the
    // timer is read after wake, capturing the halt-exit latency. Documented direct halt-exit: 12. Needs the retail BIOS.
    private static byte[] BuildHaltExitProbe() {
        var a = new Asm();

        a.LdrConst(rd: 0, value: IoBase);
        a.LdrLabel(rd: 1, label: "handler");
        a.LdrConst(rd: 2, value: 0x03007FFCu);
        a.Str(rd: 1, rn: 2, imm12: 0);
        a.Mov(rd: 1, imm8: 1);
        a.Str(rd: 1, rn: 0, imm12: 0x208);              // IME = 1
        a.Mov(rd: 1, imm8: 8);
        a.Str(rd: 1, rn: 0, imm12: 0x200);              // IE = timer0
        a.LdrConst(rd: 1, value: (0x00C0u << 16) | 0xFFFFu); // timer0 overflow next cycle, enable + IRQ
        a.Str(rd: 1, rn: 0, imm12: 0x100);
        a.Mov(rd: 1, imm8: 0);
        a.Str(rd: 1, rn: 0, imm12: 0x301);              // HALTCNT = 0 → halt (wakes on the timer IRQ)
        a.Ldr(rd: 1, rn: 0, imm12: 0x100);              // read timer0 after wake → result 0
        a.LdrConst(rd: 2, value: ResultBase);
        a.Str(rd: 1, rn: 2, imm12: 0);
        a.Spin();

        a.Label(name: "handler");
        a.LdrConst(rd: 3, value: 0x04000000u);
        a.LdrConst(rd: 1, value: 0x00080008u);
        a.Str(rd: 1, rn: 3, imm12: 0x200);              // ack timer0
        a.Bx(rn: 14);

        return a.Finish();
    }

    // -------------------------------------------------------------------------------------------------------------
    // A minimal ARM assembler (little-endian words, a dedup literal pool after the code, label + backward-branch
    // resolution). Extended from the MicroRoms assembler with LDR-from-register and an infinite-spin terminator.
    // -------------------------------------------------------------------------------------------------------------
    private sealed class Asm {
        private const uint RomBase = 0x08000000u;
        private const int RomSizeBytes = 0x8000;

        private readonly List<uint> m_code = new();
        private readonly List<(int instr, int rd, uint value, string? label)> m_loads = new();
        private readonly Dictionary<string, int> m_labels = new();

        public void Label(string name) => m_labels[name] = m_code.Count;
        public void Mov(int rd, uint imm8) => m_code.Add(item: 0xE3A00000u | ((uint)rd << 12) | (imm8 & 0xFFu));
        public void Nop() => m_code.Add(item: 0xE1A00000u); // mov r0,r0
        public void Str(int rd, int rn, uint imm12) =>
            m_code.Add(item: 0xE5800000u | ((uint)rn << 16) | ((uint)rd << 12) | (imm12 & 0xFFFu));
        public void Ldr(int rd, int rn, uint imm12) =>
            m_code.Add(item: 0xE5900000u | ((uint)rn << 16) | ((uint)rd << 12) | (imm12 & 0xFFFu));
        public void Bx(int rn) => m_code.Add(item: 0xE12FFF10u | (uint)rn);
        public void Spin() => m_code.Add(item: 0xEAFFFFFEu); // b . (branch to self)
        public void LdrConst(int rd, uint value) {
            m_loads.Add(item: (m_code.Count, rd, value, null));
            m_code.Add(item: 0);
        }
        public void LdrLabel(int rd, string label) {
            m_loads.Add(item: (m_code.Count, rd, 0, label));
            m_code.Add(item: 0);
        }
        public byte[] Finish() {
            var poolBase = m_code.Count;
            var pool = new List<uint>();

            foreach (var (instr, rd, value, label) in m_loads) {
                var resolved = ((label is null) ? value : (RomBase + ((uint)m_labels[label] * 4u)));
                var poolIndex = pool.IndexOf(item: resolved);

                if (poolIndex < 0) {
                    poolIndex = pool.Count;
                    pool.Add(item: resolved);
                }

                var literalWord = (poolBase + poolIndex);
                var offsetBytes = ((literalWord - (instr + 2)) * 4);

                m_code[instr] = 0xE59F0000u | ((uint)rd << 12) | ((uint)offsetBytes & 0xFFFu);
            }

            var words = new List<uint>(collection: m_code);

            words.AddRange(collection: pool);

            var bytes = new byte[RomSizeBytes];

            for (var i = 0; (i < words.Count); ++i) {
                BitConverter.TryWriteBytes(destination: bytes.AsSpan(start: (i * 4)), value: words[i]);
            }

            return bytes;
        }
    }
}
