using Microsoft.Extensions.DependencyInjection;

namespace Puck.AdvancedGamingBrick.Post;

// --ags <rom>: run the AGS aging cartridge (TCHK10 dump) headlessly and print the per-test result stream.
internal static partial class Diagnostics {
    // The AGS tests run in this order under the default (KEYINPUT-advanced / COM / SIO-extended tests disabled).
    // Names are best-effort annotations for the value stream written to 0x04; the raw index + value is
    // authoritative. The SIO interrupt test spins waiting for a link cable, so a headless run stalls there (after
    // ~32 results).
    private static readonly string[] s_agsTestNames = [
        "mem: cpu_external_work_ram", "mem: cpu_internal_work_ram", "mem: palette_ram", "mem: vram", "mem: oam",
        "mem: cartridge_type_flag", "mem: prefetch_buffer", "mem: waitstate_wait_control", "mem: cartridge_ram_wait_control",
        "lcd: vcounter", "lcd: vcount_intr_flag", "lcd: hblank_intr_flag", "lcd: vblank_intr_flag", "lcd: vcount_status",
        "lcd: hblank_status", "lcd: vblank_status",
        "timer: prescaler", "timer: timer_connect", "timer: timer_intr_flag",
        "dma: DMA0_address_control", "dma: DMA1_address_control", "dma: DMA2_address_control", "dma: DMA3_address_control",
        "dma: DMA_vblank_start", "dma: DMA_hblank_start", "dma: DMA_display_start", "dma: DMA_intr_flag", "dma: DMA_priority",
        "intr: vblank", "intr: hblank", "intr: vcount", "intr: timer", "intr: sio (link-cable; expected stall)",
    ];

    /// <summary>
    /// Runs the TCHK10 AGS aging cartridge headlessly. The ROM is patched in memory with
    /// an output-results patch so each test writes its result flags to 0x04; a <see cref="TracingAgbBus"/>
    /// captures that stream. A flag value of 0 means the test passed. Runs until the result stream goes quiet
    /// (the SIO interrupt test stalls waiting for a link cable, which is expected).
    /// </summary>
    public static int RunAgs(string romPath, string name) {
        if (!File.Exists(path: romPath)) {
            Console.WriteLine(value: $"  [SKIP] {name}: not found at {romPath}");

            return 0;
        }

        var rom = File.ReadAllBytes(path: romPath);

        // Output-results patch: replace the per-test flag accumulator at file offset 0xB20 with
        //   mov r1,#4 ; str r0,[r1] ; str r7,[r7,#8]
        // so the test's return flags (r0) are stored to address 0x04 after every test.
        ReadOnlySpan<byte> patch = [0x04, 0x21, 0x08, 0x60, 0xBF, 0x60];

        patch.CopyTo(destination: rom.AsSpan(start: 0xB20));

        var results = new List<uint>();

        // Diagnostic: capture every TM0CNT_L (0x04000100) read tagged with how many test results had been written
        // at the time. The wait-state test (result index 7) reads the timer 24 times — one per setting — so the
        // reads tagged 7 are exactly the 24 values it compares against wait_control_timer_values[3][8].
        var timerReads = new List<(int afterResults, uint value)>();
        // Also capture all I/O timer register reads (0x04000100–0x04000110) and IF (0x04000202) for the
        // timer_connect diagnostic (#17). Key: address, value, result-count at time of read.
        var timer1Reads = new List<(int afterResults, uint value)>();
        var connectIoReads = new List<(int afterResults, uint address, uint value, long cycles)>();
        var connectIoWrites = new List<(int afterResults, uint address, uint value, long cycles)>();
        var trace = (Environment.GetEnvironmentVariable(variable: "PUCK_AGS_TRACE") == "1");
        AdvancedGamingBrickMachine? machineRef = null;
        AgbBus? busRef = null;

        // Wire the tracing decorator in front of the real bus: register the concrete bus, then map IAgbBus to a
        // TracingAgbBus that wraps it. The compose callback runs before AddAdvancedGamingBrick's TryAdd, so ours wins.
        using var instance = AgbMachineFactory.Create(
            configuration: new AgbMachineConfiguration(bios: BiosImage, rom: rom),
            compose: services => {
                services.AddScoped<AgbBus>();
                services.AddScoped<IAgbBus>(implementationFactory: sp => {
                    busRef = sp.GetRequiredService<AgbBus>();

                    return new TracingAgbBus(
                        inner: busRef,
                        watchAddress: 0x04u,
                        onStore: value => {
                            if (trace && (results.Count < 8)) {
                                Console.WriteLine(value: $"    [store] result#{results.Count} value=0x{value:X8} pc=0x{(machineRef?.Cpu.GetRegister(index: 15) ?? 0):X8}");
                            }

                            results.Add(item: value);
                        },
                        readWatchAddress: 0x04000100u,
                        onRead: value => timerReads.Add(item: (results.Count, value)),
                        readWatchAddress2: 0x04000104u,
                        onRead2: (_, value) => timer1Reads.Add(item: (results.Count, value)),
                        readRangeBase: 0x040000B0u,
                        readRangeEnd: 0x04000210u,
                        onReadRange: (addr, value) => connectIoReads.Add(item: (results.Count, addr, value, (busRef?.Cycles ?? 0))),
                        writeRangeBase: 0x04000100u,
                        writeRangeEnd: 0x04000110u,
                        onWriteRange: (addr, value) => connectIoWrites.Add(item: (results.Count, addr, value, (busRef?.Cycles ?? 0))));
                });
            });
        var machine = instance.Machine;

        machineRef = machine;

        machine.DirectBoot();

        // Step until the result stream goes quiet: once results have started arriving, a long gap with no new
        // result means the suite has stalled (the SIO link-cable test) or finished.
        const long budget = 400_000_000;
        const long quietWindow = 12_000_000;
        var lastCount = 0;
        var lastChangeStep = 0L;

        for (long i = 1; (i <= budget); ++i) {
            machine.Step();

            if (trace && ((i % 2_000_000L) == 0L)) {
                Console.WriteLine(value: $"    [trace] step={i,12} pc=0x{machine.Cpu.GetRegister(index: 15):X8} dispcnt=0x{machine.Ppu.ReadRegister(offset: 0x00u):X4} vcount={machine.Ppu.ReadRegister(offset: 0x06u),3} results={results.Count}");
            }

            if (results.Count != lastCount) {
                lastCount = results.Count;
                lastChangeStep = i;
            } else if ((results.Count > 0) && ((i - lastChangeStep) > quietWindow)) {
                break;
            }
        }

        var passed = 0;
        var failed = 0;

        Console.WriteLine(value: $"  {name}: {results.Count} test results captured");

        for (var i = 0; (i < results.Count); ++i) {
            var value = results[i];
            var label = ((i < s_agsTestNames.Length) ? s_agsTestNames[i] : $"test #{i}");
            var ok = (value == 0u);

            if (ok) {
                ++passed;
            } else {
                ++failed;
            }

            Console.WriteLine(value: $"    [{(ok ? "PASS" : "FAIL")}] #{i,2} {label,-42} flags=0x{value:X}");
        }

        Console.WriteLine(value: $"  == AGS: {passed} passed, {failed} failed ({results.Count} run) ==");

        // Wait-state diagnostic: the 24 timer reads taken during test index 7, against the expected table.
        uint[] expectedWait = [0x28, 0x24, 0x20, 0x38, 0x24, 0x20, 0x1C, 0x34, 0x30, 0x2C, 0x28, 0x40, 0x24, 0x20, 0x1C, 0x34, 0x40, 0x3C, 0x38, 0x50, 0x24, 0x20, 0x1C, 0x34];
        var waitReads = timerReads.Where(predicate: r => (r.afterResults == 7)).Select(selector: r => r.value).ToArray();

        if (waitReads.Length > 0) {
            Console.WriteLine(value: $"  -- wait-state timer reads (ours vs expected), {waitReads.Length} captured --");

            for (var i = 0; ((i < waitReads.Length) && (i < expectedWait.Length)); ++i) {
                var ours = waitReads[i];

                Console.WriteLine(value: $"     [{i,2}] ours=0x{ours:X} expected=0x{expectedWait[i]:X} {((ours == expectedWait[i]) ? "ok" : $"d={((long)ours - expectedWait[i])}")}");
            }
        }

        // prefetch test (#6) reads the timer twice (prefetch on → expect 0x18, off → 0x33).
        var prefetchReads = timerReads.Where(predicate: r => (r.afterResults == 6)).Select(selector: r => r.value).ToArray();

        Console.WriteLine(value: $"  -- prefetch timer reads (afterResults==6): {string.Join(separator: ", ", values: prefetchReads.Select(selector: v => $"0x{v:X}"))} (expect 0x18 on, 0x33 off) --");

        // cart-RAM (SRAM) wait test (#8) reads the timer 4 times (expect 0x1C,0x18,0x14,0x2C).
        uint[] expectedCart = [0x1C, 0x18, 0x14, 0x2C];
        var cartReads = timerReads.Where(predicate: r => (r.afterResults == 8)).Select(selector: r => r.value).ToArray();

        Console.WriteLine(value: $"  -- cart-RAM timer reads (afterResults==8): {string.Join(separator: ", ", values: cartReads.Select(selector: v => $"0x{v:X}"))} (expect 0x1C,0x18,0x14,0x2C) --");

        // prescaler test (#16) reads the timer once per prescaler mode (4 reads: /1, /64, /256, /1024).
        var prescalerReads = timerReads.Where(predicate: r => (r.afterResults == 16)).Select(selector: r => r.value).ToArray();

        Console.WriteLine(value: $"  -- prescaler timer reads (afterResults==16): {string.Join(separator: ", ", values: prescalerReads.Select(selector: v => $"0x{v:X}"))} ({prescalerReads.Length} reads, expect 4 values) --");

        // For any failed test: dump I/O reads and writes observed during it.
        for (var testIdx = 0; (testIdx < results.Count); ++testIdx) {
            if (results[testIdx] == 0u) {
                continue;
            }

            var testLabel = ((testIdx < s_agsTestNames.Length) ? s_agsTestNames[testIdx] : $"test #{testIdx}");

            var ioWritesForTest = connectIoWrites.Where(predicate: r => (r.afterResults == testIdx)).ToArray();

            if (ioWritesForTest.Length > 0) {
                Console.WriteLine(value: $"  -- [{testIdx}] {testLabel} I/O writes: {ioWritesForTest.Length} total --");

                foreach (var (_, addr, val, cyc) in ioWritesForTest) {
                    Console.WriteLine(value: $"     W [0x{addr:X8}] <- 0x{val:X6}  @cyc={cyc}");
                }
            }

            var ioForTest = connectIoReads.Where(predicate: r => (r.afterResults == testIdx)).ToArray();

            if (ioForTest.Length > 0) {
                Console.WriteLine(value: $"  -- [{testIdx}] {testLabel} I/O reads: {ioForTest.Length} total --");

                foreach (var (_, addr, val, cyc) in ioForTest) {
                    Console.WriteLine(value: $"     R [0x{addr:X8}] = 0x{val:X4}  @cyc={cyc}");
                }
            }
        }

        return failed;
    }
}
