using System.Runtime.InteropServices;
using Microsoft.Extensions.DependencyInjection;
using Puck.Capture;

namespace Puck.GameBoyAdvance.Conformance;

// Runs prebuilt GBA test ROMs on a full machine and reads their verdict from memory — no PPU needed.
//   * jsmolka gba-tests accumulate the first failing test number in r12 (0 = all passed) before a vsync/idle
//     loop; with no PPU the ROM hangs in the vsync wait, but r12 already holds the result.
//   * FuzzARM dumps a failure marker ('AAAA'/'TTTT') to the start of EWRAM and otherwise leaves it zero.
// Either way the runner steps until execution settles into a tight loop (or a cap) and then inspects memory.
internal static class RomRunner {
    /// <summary>The BIOS image every machine is built with. Defaults to a zeroed stub; the entry point loads the
    /// open-source replacement BIOS into it when one is available.</summary>
    public static ReadOnlyMemory<byte> BiosImage { get; set; } = new byte[ReplacementBios.ImageSize];

    private const long StepCap = 64_000_000;
    private const long CheckInterval = 0x40000;
    private const uint EwramStart = 0x02000000;
    private const uint FuzzArmMarkerArm = 0x41414141u;  // 'AAAA'
    private const uint FuzzArmMarkerThumb = 0x54545454u; // 'TTTT'

    public static int RunJsmolka(string romPath, string name) {
        if (!TryLoad(romPath: romPath, name: name, out var provider, out var machine)) {
            return 0;
        }

        using (provider) {
            RunUntilSettled(machine: machine);

            // Guard against a crash masquerading as a pass: a settled PC outside BIOS/ROM means the core ran off
            // into unmapped memory rather than reaching the ROM's result loop.
            var pc = machine.Cpu.GetRegister(index: 15);

            if ((pc >= 0x4000u) && ((pc < 0x08000000u) || (pc >= 0x0A000000u))) {
                Console.WriteLine($"  [FAIL] {name}: ran off to unmapped PC 0x{pc:X8}");

                return 1;
            }

            var verdict = machine.Cpu.GetRegister(index: 12);

            if (verdict == 0u) {
                Console.WriteLine($"  [PASS] {name}: all tests passed");

                return 0;
            }

            Console.WriteLine($"  [FAIL] {name}: first failing test = {verdict}");

            return 1;
        }
    }

    public static int RunFuzzArm(string romPath, string name) {
        if (!TryLoad(romPath: romPath, name: name, out var provider, out var machine)) {
            return 0;
        }

        using (provider) {
            RunUntilSettled(machine: machine);

            var marker = machine.Bus.Read32(address: EwramStart, access: BusAccessType.NonSequential);

            if ((marker != FuzzArmMarkerArm) && (marker != FuzzArmMarkerThumb)) {
                Console.WriteLine($"  [PASS] {name}: all tests passed");

                return 0;
            }

            Console.WriteLine($"  [FAIL] {name}: failure dumped to EWRAM (state '{(char)(marker & 0xFF)}')");

            for (uint offset = 0; offset < 0x80u; offset += 4u) {
                var word = machine.Bus.Read32(address: EwramStart + offset, access: BusAccessType.NonSequential);

                Console.WriteLine($"      EWRAM+{offset:X2}: 0x{word:X8}  '{Ascii(word)}'");
            }

            return 1;
        }
    }

    public static void TraceCrash(string romPath) {
        if (!TryLoad(romPath: romPath, name: Path.GetFileName(romPath), out var provider, out var machine)) {
            return;
        }

        using (provider) {
            var cpu = machine.Cpu;

            for (long i = 0; i < 30_000_000; ++i) {
                var pcBefore = cpu.GetRegister(index: 15);
                var thumb = (cpu.Cpsr & 0x20u) != 0u;

                machine.Step();

                var pc = cpu.GetRegister(index: 15);
                var valid = (pc < 0x4000u) || ((pc >= 0x08000000u) && (pc < 0x0A000000u));

                if (valid) {
                    continue;
                }

                var culprit = pcBefore - (thumb ? 4u : 8u);

                Console.WriteLine($"  CRASH at step {i}: branched to 0x{pc:X8}");
                Console.WriteLine($"  culprit instruction @0x{culprit:X8} = 0x{machine.Bus.Read32(address: culprit, access: BusAccessType.NonSequential):X8} (thumb={thumb})");

                for (var r = 0; r < 16; r += 4) {
                    Console.WriteLine($"    r{r,-2}=0x{cpu.GetRegister(r):X8}  r{r + 1,-2}=0x{cpu.GetRegister(r + 1):X8}  r{r + 2,-2}=0x{cpu.GetRegister(r + 2):X8}  r{r + 3,-2}=0x{cpu.GetRegister(r + 3):X8}");
                }

                Console.WriteLine($"    cpsr=0x{cpu.Cpsr:X8}");

                return;
            }

            Console.WriteLine("  no crash within step budget");
        }
    }

    public static void Render(string romPath, string outputPath, long steps = 6_000_000) {
        if (!TryLoad(romPath: romPath, name: Path.GetFileName(romPath), out var provider, out var machine)) {
            return;
        }

        using (provider) {
            // Run long enough for the ROM to finish its vsync wait and draw its result, rather than stopping at
            // the first stable-PC loop (which would catch it mid-vsync, before anything is drawn).
            for (long i = 0; i < steps; ++i) {
                machine.Step();
            }

            PngEncoder.Write(
                height: 160,
                path: outputPath,
                rgba: MemoryMarshal.AsBytes(span: machine.Framebuffer),
                width: 240);

            Console.WriteLine($"  rendered {Path.GetFileName(romPath)} -> {outputPath}");
        }
    }

    /// <summary>
    /// Traces per-instruction cycle accounting for a ROM, to diff against the mGBA cycle-exact oracle. Prints
    /// (step, PC, cumulative cycles, delta) for each instruction, plus the final value the micro-ROM stored to
    /// EWRAM (0x02000000) — the same number the AGS wait-state/prescaler tests compare against hardware.
    /// </summary>
    public static void TraceCycles(string romPath, long steps) {
        if (!TryLoad(romPath: romPath, name: Path.GetFileName(romPath), out var provider, out var machine)) {
            return;
        }

        using (provider) {
            var bus = (GbaBus)machine.Bus;
            var prev = bus.Cycles;
            var lastPc = 0xFFFFFFFFu;
            var stable = 0;

            for (long i = 0; i < steps; ++i) {
                var pc = machine.Cpu.GetRegister(index: 15);

                machine.Step();

                var now = bus.Cycles;

                Console.WriteLine($"{i,5}  pc={pc:X8}  cyc={now}  d={now - prev}");

                prev = now;

                if (pc == lastPc) {
                    if (++stable > 4) {
                        break;
                    }
                }
                else {
                    stable = 0;
                }

                lastPc = pc;
            }

            Console.WriteLine($"RESULT[0x02000000] = 0x{machine.Bus.Read32(address: 0x02000000u, access: BusAccessType.NonSequential):X8} (timer value the test reads)");
        }
    }

    /// <summary>
    /// Boots a ROM, runs it for a fixed number of steps, and hashes the resulting framebuffer. Used as a
    /// deterministic visual-regression floor: the core is fully deterministic, so a known-good render must
    /// reproduce its hash exactly. A mismatch flags an unintended change to the CPU/PPU/timing pipeline.
    /// When <paramref name="expected"/> is 0 the actual hash is reported (for capturing a new floor).
    /// </summary>
    public static int RunRenderHash(string romPath, string name, long steps, ulong expected) {
        if (!TryLoad(romPath: romPath, name: name, out var provider, out var machine)) {
            return 0;
        }

        using (provider) {
            for (long i = 0; i < steps; ++i) {
                machine.Step();
            }

            var bytes = MemoryMarshal.AsBytes(span: machine.Framebuffer);
            var hash = 0xCBF29CE484222325ul; // FNV-1a 64-bit

            foreach (var b in bytes) {
                hash = (hash ^ b) * 0x100000001B3ul;
            }

            if (expected == 0ul) {
                Console.WriteLine($"  [HASH] {name}: 0x{hash:X16} (capture)");

                return 0;
            }

            if (hash == expected) {
                Console.WriteLine($"  [PASS] {name}: frame hash matches floor");

                return 0;
            }

            Console.WriteLine($"  [FAIL] {name}: frame hash 0x{hash:X16} != floor 0x{expected:X16}");

            return 1;
        }
    }

    // The AGS tests run in this order under the default (KEYINPUT-advanced / COM / SIO-extended tests disabled),
    // per the DenSinH/AGSTests decompilation. Names are best-effort annotations for the value stream written to
    // 0x04; the raw index + value is authoritative. The SIO interrupt test spins waiting for a link cable, so a
    // headless run stalls there (after ~32 results).
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
    /// Runs the AGS aging cartridge (the TCHK10 dump, md5 9f74b2ad…) headlessly. The ROM is patched in memory with
    /// the DenSinH output-results patch so each test writes its result flags to 0x04; a <see cref="TracingGbaBus"/>
    /// captures that stream. A flag value of 0 means the test passed. Runs until the result stream goes quiet
    /// (the SIO interrupt test stalls waiting for a link cable, which is expected).
    /// </summary>
    public static int RunAgs(string romPath, string name) {
        if (!File.Exists(romPath)) {
            Console.WriteLine($"  [SKIP] {name}: not found at {romPath}");

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
        var cartridge = new GbaCartridge(rom: rom);
        var services = new ServiceCollection();

        // Wire the tracing decorator in front of the real bus: register the concrete bus, then map IGbaBus to a
        // TracingGbaBus that wraps it. Registering IGbaBus first makes AddGameBoyAdvance's TryAdd defer to ours.
        services.AddScoped<GbaBus>();
        services.AddScoped<IGbaBus>(implementationFactory: sp => new TracingGbaBus(
            inner: sp.GetRequiredService<GbaBus>(),
            watchAddress: 0x04u,
            onStore: results.Add,
            readWatchAddress: 0x04000100u,
            onRead: value => timerReads.Add((results.Count, value))));
        _ = services.AddGameBoyAdvance();
        _ = services.AddReplacementBios(image: BiosImage);
        services.AddScoped<GbaCartridge>(implementationFactory: _ => cartridge);

        using var provider = services.BuildServiceProvider();
        var machine = provider.CreateScope().ServiceProvider.GetRequiredService<GameBoyAdvanceMachine>();

        machine.DirectBoot();

        // Step until the result stream goes quiet: once results have started arriving, a long gap with no new
        // result means the suite has stalled (the SIO link-cable test) or finished.
        const long budget = 400_000_000;
        const long quietWindow = 12_000_000;
        var lastCount = 0;
        var lastChangeStep = 0L;

        for (long i = 1; i <= budget; ++i) {
            machine.Step();

            if (results.Count != lastCount) {
                lastCount = results.Count;
                lastChangeStep = i;
            }
            else if ((results.Count > 0) && ((i - lastChangeStep) > quietWindow)) {
                break;
            }
        }

        var passed = 0;
        var failed = 0;

        Console.WriteLine($"  {name}: {results.Count} test results captured");

        for (var i = 0; i < results.Count; ++i) {
            var value = results[i];
            var label = (i < s_agsTestNames.Length) ? s_agsTestNames[i] : $"test #{i}";
            var ok = value == 0u;

            if (ok) {
                ++passed;
            }
            else {
                ++failed;
            }

            Console.WriteLine($"    [{(ok ? "PASS" : "FAIL")}] #{i,2} {label,-42} flags=0x{value:X}");
        }

        Console.WriteLine($"  == AGS: {passed} passed, {failed} failed ({results.Count} run) ==");

        // Wait-state diagnostic: the 24 timer reads taken during test index 7, against the expected table.
        uint[] expectedWait = [0x28, 0x24, 0x20, 0x38, 0x24, 0x20, 0x1C, 0x34, 0x30, 0x2C, 0x28, 0x40, 0x24, 0x20, 0x1C, 0x34, 0x40, 0x3C, 0x38, 0x50, 0x24, 0x20, 0x1C, 0x34];
        var waitReads = timerReads.Where(predicate: r => r.afterResults == 7).Select(selector: r => r.value).ToArray();

        if (waitReads.Length > 0) {
            Console.WriteLine($"  -- wait-state timer reads (ours vs expected), {waitReads.Length} captured --");

            for (var i = 0; (i < waitReads.Length) && (i < expectedWait.Length); ++i) {
                var ours = waitReads[i];
                Console.WriteLine($"     [{i,2}] ours=0x{ours:X} expected=0x{expectedWait[i]:X} {(ours == expectedWait[i] ? "ok" : $"Δ={(long)ours - expectedWait[i]}")}");
            }
        }

        // prefetch test (#6) reads the timer twice (prefetch on → expect 0x18, off → 0x33).
        var prefetchReads = timerReads.Where(predicate: r => r.afterResults == 6).Select(selector: r => r.value).ToArray();
        Console.WriteLine($"  -- prefetch timer reads (afterResults==6): {string.Join(", ", prefetchReads.Select(v => $"0x{v:X}"))} (expect 0x18 on, 0x33 off) --");

        // cart-RAM (SRAM) wait test (#8) reads the timer 4 times (expect 0x1C,0x18,0x14,0x2C).
        uint[] expectedCart = [0x1C, 0x18, 0x14, 0x2C];
        var cartReads = timerReads.Where(predicate: r => r.afterResults == 8).Select(selector: r => r.value).ToArray();
        Console.WriteLine($"  -- cart-RAM timer reads (afterResults==8): {string.Join(", ", cartReads.Select(v => $"0x{v:X}"))} (expect 0x1C,0x18,0x14,0x2C) --");

        return failed;
    }

    private static string Ascii(uint word) {
        Span<char> chars = stackalloc char[4];

        for (var i = 0; i < 4; ++i) {
            var c = (char)((word >> (i * 8)) & 0xFFu);

            chars[i] = ((c >= ' ') && (c < (char)0x7F))
                ? c
                : '.';
        }

        return new string(value: chars);
    }

    private static bool TryLoad(string romPath, string name, out ServiceProvider provider, out GameBoyAdvanceMachine machine) {
        provider = null!;
        machine = null!;

        if (!File.Exists(romPath)) {
            Console.WriteLine($"  [SKIP] {name}: not found at {romPath}");

            return false;
        }

        var cartridge = new GbaCartridge(rom: File.ReadAllBytes(path: romPath));
        var services = new ServiceCollection();

        _ = services.AddGameBoyAdvance();
        _ = services.AddReplacementBios(image: BiosImage);
        services.AddScoped<GbaCartridge>(implementationFactory: _ => cartridge);

        provider = services.BuildServiceProvider();
        machine = provider.CreateScope().ServiceProvider.GetRequiredService<GameBoyAdvanceMachine>();

        machine.DirectBoot();

        return true;
    }

    private static void RunUntilSettled(GameBoyAdvanceMachine machine) {
        var lastPc = 0xFFFFFFFFu;

        for (long i = 1; i <= StepCap; ++i) {
            machine.Step();

            if ((i % CheckInterval) == 0) {
                var pc = machine.Cpu.GetRegister(index: 15);

                // Two checkpoints landing in the same small window means execution has settled into a loop.
                if ((pc & ~0x3Fu) == (lastPc & ~0x3Fu)) {
                    return;
                }

                lastPc = pc;
            }
        }
    }
}
