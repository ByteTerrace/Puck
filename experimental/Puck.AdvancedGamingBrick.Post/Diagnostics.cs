using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Extensions.DependencyInjection;
using Puck.Capture;

namespace Puck.AdvancedGamingBrick.Post;

/// <summary>
/// The diagnostic surface of the GBA POST: the mGBA / ARES co-simulator tooling and single-ROM inspectors that drive the
/// accuracy frontier (the Pokémon Emerald boot investigation — see the README). These are investigative tools, not
/// self-checking stages; <see cref="TryRun"/> dispatches them from CLI flags before the POST battery runs, so the battery
/// stays the default. The menu-driven mGBA test suite (<see cref="RunMgbaSuite"/>) and the AGS aging cartridge
/// (<see cref="RunAgs"/>) are conformance runs shared with their Tier-B measurement stages
/// (<see cref="MgbaSuiteStage"/> / <see cref="AgsStage"/>).
/// </summary>
internal static class Diagnostics {
    /// <summary>The BIOS image every machine is built with. Defaults to a zeroed stub; the entry point loads the
    /// open-source replacement BIOS into it when one is available.</summary>
    public static ReadOnlyMemory<byte> BiosImage { get; set; } = new byte[ReplacementBios.ImageSize];

    /// <summary>The number of suites the menu-driven mGBA test suite steps through.</summary>
    public const int MgbaSuiteCount = 14;

    /// <summary>Dispatches the diagnostic CLI flags — each runs a single investigative mode and returns; when none
    /// matches, the caller proceeds to the POST battery.</summary>
    /// <param name="args">The command-line arguments.</param>
    /// <param name="exitCode">The exit code the handled mode produced (0 when it does not gate).</param>
    /// <returns><see langword="true"/> when a diagnostic flag was handled (return <paramref name="exitCode"/>, skip the
    /// battery); otherwise <see langword="false"/>.</returns>
    public static bool TryRun(string[] args, out int exitCode) {
        exitCode = 0;

        // --save-test: verify the cartridge save-persistence (.sav) round-trip, standalone.
        if (Array.IndexOf(array: args, value: "--save-test") >= 0) {
            var (pass, detail) = SaveRoundTripProbe.Run();

            Console.WriteLine(value: $"== save persistence round-trip: {(pass ? "PASS" : "FAIL")} — {detail} ==");
            exitCode = (pass ? 0 : 1);

            return true;
        }

        // --trace-crash <rom>: report the first branch into unmapped memory.
        for (var index = 0; index < (args.Length - 1); ++index) {
            if (args[index] == "--trace-crash") {
                TraceCrash(romPath: args[index + 1]);

                return true;
            }
        }

        // --render <rom> <out.png> [steps]: boot a ROM and dump its framebuffer, to eyeball the PPU output.
        for (var index = 0; index < (args.Length - 2); ++index) {
            if (args[index] == "--render") {
                var steps = (((index + 3) < args.Length) && long.TryParse(args[index + 3], out var parsed))
                    ? parsed
                    : 6_000_000L;

                Render(romPath: args[index + 1], outputPath: args[index + 2], steps: steps);

                return true;
            }
        }

        // --render-hash <rom> <steps>: print the framebuffer hash after N steps, for capturing a render floor.
        for (var index = 0; index < (args.Length - 2); ++index) {
            if (args[index] == "--render-hash") {
                var (_, _, detail) = RenderHashProbe.Run(romPath: args[index + 1], steps: long.Parse(args[index + 2]), expected: 0ul, bios: BiosImage);

                Console.WriteLine(value: $"  [HASH] {Path.GetFileName(path: args[index + 1])}: {detail}");

                return true;
            }
        }

        // --pctrace <rom> <steps>: print executing 0x08… instruction addresses, to diff against the mGBA cosim.
        for (var index = 0; index < (args.Length - 2); ++index) {
            if (args[index] == "--pctrace") {
                PcTrace(romPath: args[index + 1], steps: long.Parse(args[index + 2]));

                return true;
            }
        }

        // --statetrace <rom> <steps>: full per-instruction CPU state, to diff against the mGBA cosim's --statetrace.
        for (var index = 0; index < (args.Length - 2); ++index) {
            if (args[index] == "--statetrace") {
                StateTrace(romPath: args[index + 1], steps: long.Parse(args[index + 2]));

                return true;
            }
        }

        // --gen-rom <kind> <out.gba>: hand-assemble a timer/IRQ micro-ROM to disk for lockstep against the ARES oracle.
        for (var index = 0; index < (args.Length - 2); ++index) {
            if (args[index] == "--gen-rom") {
                MicroRoms.Generate(kind: args[index + 1], outPath: args[index + 2]);

                return true;
            }
        }

        // --lockstep <rom> <steps> [direct]: step Puck against the ARES oracle in lockstep to the first divergence.
        for (var index = 0; index < (args.Length - 2); ++index) {
            if (args[index] == "--lockstep") {
                exitCode = Lockstep(romPath: args[index + 1], steps: long.Parse(args[index + 2]), direct: Array.IndexOf(array: args, value: "direct") >= 0);

                return true;
            }
        }

        // --iodump <rom> <steps>: dump every I/O register halfword, to diff against ares-cosim's iodump.
        for (var index = 0; index < (args.Length - 2); ++index) {
            if (args[index] == "--iodump") {
                IoDump(romPath: args[index + 1], steps: long.Parse(args[index + 2]));

                return true;
            }
        }

        // --probe <rom> <steps> | --emerald-trace <rom> <loHex> <hiHex> <count> [skip]: blank-screen boot diagnostics.
        if (TryDiagnostic(args: args)) {
            return true;
        }

        // --trace-cycles <rom> <steps>: per-instruction cycle trace, to diff against the mGBA cosim oracle.
        for (var index = 0; index < (args.Length - 2); ++index) {
            if (args[index] == "--trace-cycles") {
                TraceCycles(romPath: args[index + 1], steps: long.Parse(args[index + 2]));

                return true;
            }
        }

        // --mgba-suite <rom>: run the menu-driven mGBA test suite (mgba-emu/suite) headlessly via the debug-log register.
        for (var index = 0; index < (args.Length - 1); ++index) {
            if (args[index] == "--mgba-suite") {
                exitCode = RunMgbaSuite(romPath: args[index + 1], name: "mGBA suite");

                return true;
            }
        }

        // --ags <rom>: run the AGS aging cartridge (TCHK10 dump) headlessly and print the per-test result stream.
        for (var index = 0; index < (args.Length - 1); ++index) {
            if (args[index] == "--ags") {
                _ = RunAgs(romPath: args[index + 1], name: Path.GetFileName(path: args[index + 1]));

                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Runs the menu-driven mGBA test suite (mgba-emu/suite) head-lessly: a <see cref="MgbaDebugBus"/> emulates the
    /// mGBA debug-log register (so the suite prints each category's "BEGIN:"/"END: passes/total") and injects the
    /// controller input that drives the menu — press A to run each suite, read its result, press B then Down to
    /// advance. Returns the number of suites with at least one failing subtest.
    /// </summary>
    public static int RunMgbaSuite(string romPath, string name) {
        if (!File.Exists(romPath)) {
            Console.WriteLine($"  [SKIP] {name}: not found at {romPath}");

            return 0;
        }

        const long frameCycles = 280_896; // one GBA frame
        var logs = new List<string>();
        var cartridge = new GbaCartridge(rom: File.ReadAllBytes(path: romPath));
        var services = new ServiceCollection();

        services.AddScoped<GbaBus>();
        services.AddScoped<IGbaBus>(implementationFactory: sp => new MgbaDebugBus(
            inner: sp.GetRequiredService<GbaBus>(),
            onLog: (level, text) => logs.Add(item: text)));
        _ = services.AddGameBoyAdvance();
        _ = services.AddReplacementBios(image: BiosImage);
        services.AddScoped<GbaCartridge>(implementationFactory: _ => cartridge);

        using var provider = services.BuildServiceProvider();
        var scope = provider.CreateScope().ServiceProvider;
        var machine = scope.GetRequiredService<GameBoyAdvanceMachine>();
        var bus = scope.GetRequiredService<GbaBus>();
        var debug = (MgbaDebugBus)scope.GetRequiredService<IGbaBus>();

        machine.DirectBoot();

        void StepCycles(long cycles) {
            var target = bus.Cycles + cycles;

            while (bus.Cycles < target) {
                machine.Step();
            }
        }

        void Press(ushort keyMask) {
            debug.Keys = (ushort)(0x3FFu & ~keyMask);
            StepCycles(frameCycles * 3);
            debug.Keys = 0x3FF;
            StepCycles(frameCycles * 5);
        }

        const ushort keyA = 0x1, keyB = 0x2, keyDown = 0x80;

        // Let the suite clear SRAM, set up, and reach its menu.
        StepCycles(frameCycles * 40);

        const int suiteCount = MgbaSuiteCount;
        var passedSuites = 0;
        var failedSuites = 0;

        Console.WriteLine($"  == {name} ==");

        for (var i = 0; i < suiteCount; ++i) {
            var before = logs.Count;

            Press(keyMask: keyA); // run the selected suite

            // Wait for the "END: passes/total" line (slow suites take many frames).
            var endLine = (string?)null;

            for (var frame = 0; (frame < 1200) && (endLine is null); ++frame) {
                StepCycles(frameCycles);

                for (var l = before; l < logs.Count; ++l) {
                    if (logs[l].StartsWith(value: "END:", comparisonType: StringComparison.Ordinal)) {
                        endLine = logs[l];

                        break;
                    }
                }
            }

            var beginLine = logs.Skip(count: before).FirstOrDefault(predicate: s => s.StartsWith(value: "BEGIN:", comparisonType: StringComparison.Ordinal));
            var suiteName = beginLine?.Substring(startIndex: 6).Trim() ?? $"suite #{i}";

            if (endLine is null) {
                Console.WriteLine($"    [????] {suiteName,-20} no result (timed out)");
                ++failedSuites;
            }
            else {
                // "END: passes/total"
                var slash = endLine.IndexOf(value: '/');
                var passes = int.Parse(endLine.AsSpan(start: 4, length: slash - 4).Trim());
                var total = int.Parse(endLine.AsSpan(start: slash + 1).Trim());
                var ok = (passes == total) && (total > 0);

                Console.WriteLine($"    [{(ok ? "PASS" : "FAIL")}] {suiteName,-20} {passes}/{total}");

                if (ok) {
                    ++passedSuites;
                }
                else {
                    ++failedSuites;

                    // Per-subtest detail: the suite logs each FAILING subtest between BEGIN: and END:. Dump them
                    // (gated/focusable via PUCK_MGBA_FOCUS=<substring>) so we can fix real mechanism failures rather
                    // than guess from the aggregate score. Capped to keep the output readable.
                    var focus = Environment.GetEnvironmentVariable(variable: "PUCK_MGBA_FOCUS");
                    var wantDetail = (focus is null) || suiteName.Contains(value: focus, comparisonType: StringComparison.OrdinalIgnoreCase);

                    if (wantDetail) {
                        var shown = 0;

                        for (var l = before; (l < logs.Count) && (shown < 80); ++l) {
                            var text = logs[l];

                            // Show only failures: a suite's early sub-tests often pass, and the PASS debug lines would
                            // otherwise consume the cap before any FAIL detail (the savprintf "Got X vs Y" offset that
                            // tells us the actual cycle/value error) is reached.
                            if (text.StartsWith(value: "BEGIN:", comparisonType: StringComparison.Ordinal)
                                || text.StartsWith(value: "END:", comparisonType: StringComparison.Ordinal)
                                || (text.Length == 0)
                                || !text.Contains(value: "FAIL", comparisonType: StringComparison.Ordinal)) {
                                continue;
                            }

                            Console.WriteLine($"        · {text}");
                            ++shown;
                        }
                    }
                }
            }

            Press(keyMask: keyB);    // back to the menu
            Press(keyMask: keyDown); // next suite
        }

        Console.WriteLine($"  == {name}: {passedSuites}/{suiteCount} suites fully passed ==");

        return failedSuites;
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
                var region = pc >> 24;
                var valid = (pc < 0x4000u)
                    || (region == 0x02u)   // EWRAM
                    || (region == 0x03u)   // IWRAM
                    || (region == 0x05u)   // palette
                    || (region == 0x06u)   // VRAM
                    || ((region >= 0x08u) && (region <= 0x0Du)); // ROM + mirrors

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
            // PUCK_GBA_FULLBOOT=1: run the real BIOS intro then jump to the cartridge (cpu.Reset), instead of the
            // direct-boot post-BIOS state. Some games (e.g. Pokémon Emerald) depend on full-BIOS-boot side effects.
            if (Environment.GetEnvironmentVariable(variable: "PUCK_GBA_FULLBOOT") == "1") {
                machine.Cpu.Reset();
            }

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
    /// <summary>Prints the executing instruction address for each game-ROM (0x08…) instruction, matching the
    /// mGBA cosim's --pctrace format, so the two streams can be diffed to find the first execution divergence.</summary>
    public static void PcTrace(string romPath, long steps) {
        if (!TryLoad(romPath: romPath, name: Path.GetFileName(romPath), out var provider, out var machine)) {
            return;
        }

        using (provider) {
            var cpu = machine.Cpu;
            var bus = (GbaBus)machine.Bus;
            var output = Console.Out;

            // Boot through the BIOS reset routine (undo TryLoad's direct boot) so the trace aligns with mGBA's
            // full-BIOS boot, letting the first true execution divergence be found.
            cpu.Reset();

            for (long i = 0; i < steps; ++i) {
                var thumb = (cpu.Cpsr & 0x20u) != 0u;

                output.WriteLine($"{cpu.GetRegister(index: 15):X8} {(thumb ? 'T' : 'A')} {bus.Cycles}");

                machine.Step();
            }
        }
    }

    /// <summary>Full-state co-simulation trace: per instruction prints PC, CPSR, r0..r14, and cumulative cycles
    /// (state BEFORE the instruction executes), matching the mGBA cosim's <c>--statetrace</c> format. Diffing the
    /// architectural registers (not just PC) finds the first true divergence unambiguously — immune to the
    /// pipeline PC offset and to single-instruction count slips that defeat a naive line-by-line PC diff.</summary>
    public static void StateTrace(string romPath, long steps) {
        if (!TryLoad(romPath: romPath, name: Path.GetFileName(romPath), out var provider, out var machine)) {
            return;
        }

        using (provider) {
            var cpu = machine.Cpu;
            var bus = (GbaBus)machine.Bus;
            using var output = new StreamWriter(stream: Console.OpenStandardOutput(), bufferSize: 1 << 20);
            var sb = new System.Text.StringBuilder(capacity: 160);

            // Boot through the BIOS reset routine (undo TryLoad's direct boot) so the trace aligns with mGBA.
            cpu.Reset();

            for (long i = 0; i < steps; ++i) {
                sb.Clear();
                sb.Append(value: cpu.GetRegister(index: 15).ToString(format: "X8"));
                sb.Append(value: ' ').Append(value: cpu.Cpsr.ToString(format: "X8"));

                for (var r = 0; r < 15; ++r) {
                    sb.Append(value: ' ').Append(value: cpu.GetRegister(index: r).ToString(format: "X8"));
                }

                sb.Append(value: ' ').Append(value: bus.Cycles);
                output.WriteLine(value: sb);

                machine.Step();
            }
        }
    }

    /// <summary>
    /// Lockstep differential against the ARES oracle (the cycle-stepped reference Puck is being realigned to).
    /// Spawns <c>ares-cosim.exe</c> on the same ROM/steps/BIOS, reads its per-instruction trace, and steps Puck
    /// in lockstep — comparing architectural state (cpsr + r0..r14) and per-instruction cycle deltas. Both boot
    /// the real BIOS, so the streams align 1:1 by instruction index. Halts at the first FUNCTIONAL divergence (a
    /// real bug, or the symptom of accumulated timing drift resolving a timing-paced branch differently) and
    /// reports the first TIMING-delta divergence + cumulative drift — the M-CYCLE target. ares-cosim path from
    /// <c>PUCK_ARES_COSIM</c> (default the gba-cosim dir); BIOS from <c>PUCK_GBA_BIOS</c>.
    /// </summary>
    public static int Lockstep(string romPath, long steps, bool direct = false) {
        if (!File.Exists(romPath)) {
            Console.WriteLine($"  [SKIP] lockstep: rom not found at {romPath}");

            return 0;
        }

        var aresExe = Environment.GetEnvironmentVariable(variable: "PUCK_ARES_COSIM")
            ?? @"D:\Source\ByteTerrace\Temp\gba-cosim\ares-cosim.exe";
        var biosPath = Environment.GetEnvironmentVariable(variable: "PUCK_GBA_BIOS")
            ?? @"D:\Source\ByteTerrace\Temp\GBA_bios.rom";

        if (!File.Exists(aresExe)) {
            Console.WriteLine($"  [SKIP] lockstep: ares-cosim not found at {aresExe} (set PUCK_ARES_COSIM)");

            return 0;
        }

        var psi = new ProcessStartInfo {
            FileName = aresExe,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        psi.ArgumentList.Add(item: romPath);
        psi.ArgumentList.Add(item: steps.ToString());
        psi.ArgumentList.Add(item: biosPath);

        if (direct) {
            psi.ArgumentList.Add(item: "direct");
        }

        using var ares = Process.Start(startInfo: psi)!;
        var aresOut = ares.StandardOutput;

        if (!TryLoad(romPath: romPath, name: Path.GetFileName(romPath), out var provider, out var machine)) {
            return 0;
        }

        using (provider) {
            var cpu = machine.Cpu;
            var bus = (GbaBus)machine.Bus;

            // BIOS-boot mode: undo TryLoad's direct boot and run the BIOS reset to align with ares's full-BIOS boot.
            // Direct-boot mode: keep TryLoad's DirectBoot state so both cores start at the cartridge entry (0x08000000),
            // skipping the ~1M-instruction BIOS intro — for diffing ROM/game execution.
            if (!direct) {
                cpu.Reset();
            }

            var history = new Queue<string>(capacity: 16);
            long prevAres = -1, prevPuck = -1, aresClk0 = -1, puckCyc0 = -1;
            long firstTimingIdx = -1, firstTimingAres = 0, firstTimingPuck = 0, timingMismatches = 0;
            long maxDrift = 0, maxDriftIdx = 0;
            uint firstTimingPc = 0;

            void TimingSummary() {
                if (firstTimingIdx < 0) {
                    Console.WriteLine("  timing: per-instruction cycle deltas matched ARES exactly.");
                }
                else {
                    Console.WriteLine($"  timing: FIRST cycle-delta divergence at instr {firstTimingIdx} (aresPC=0x{firstTimingPc:X8}): ares d={firstTimingAres} puck d={firstTimingPuck}");
                    Console.WriteLine($"  timing: {timingMismatches} delta mismatches; max cumulative drift (puck-ares) = {maxDrift} at instr {maxDriftIdx}");
                }
            }

            try {
                for (long i = 0; i < steps; ++i) {
                    var line = aresOut.ReadLine();

                    if (line is null) {
                        Console.WriteLine($"  ares stream ended at instr {i}");

                        break;
                    }

                    var f = line.Split(separator: ' ', options: StringSplitOptions.RemoveEmptyEntries);

                    if (f.Length < 19) {
                        continue;
                    }

                    // ares columns: f0=execAddr f1=cpsr f2..f17=r0..r15 f18=clock
                    var aExec = Convert.ToUInt32(value: f[0], fromBase: 16);
                    var aCpsr = Convert.ToUInt32(value: f[1], fromBase: 16);
                    var aClk = long.Parse(s: f[18]);

                    var pCpsr = cpu.Cpsr;
                    var pCyc = bus.Cycles;

                    if (aresClk0 < 0) {
                        aresClk0 = aClk;
                        puckCyc0 = pCyc;
                    }

                    // functional compare: cpsr + r0..r14 (architectural state, immune to PC-representation offset)
                    var funcCpsr = aCpsr != pCpsr;
                    var funcReg = -1;

                    for (var r = 0; (r < 15) && (funcReg < 0); ++r) {
                        if (Convert.ToUInt32(value: f[2 + r], fromBase: 16) != cpu.GetRegister(index: r)) {
                            funcReg = r;
                        }
                    }

                    if (prevAres >= 0) {
                        var da = aClk - prevAres;
                        var dp = pCyc - prevPuck;

                        if (da != dp) {
                            ++timingMismatches;

                            if (firstTimingIdx < 0) {
                                firstTimingIdx = i;
                                firstTimingAres = da;
                                firstTimingPuck = dp;
                                firstTimingPc = aExec;
                            }
                        }

                        var drift = (pCyc - puckCyc0) - (aClk - aresClk0);

                        if (Math.Abs(value: drift) > Math.Abs(value: maxDrift)) {
                            maxDrift = drift;
                            maxDriftIdx = i;
                        }
                    }

                    if (history.Count >= 16) {
                        _ = history.Dequeue();
                    }

                    history.Enqueue(item: $"#{i,8} aresPC={aExec:X8} cpsr a={aCpsr:X8}/p={pCpsr:X8} cyc a={aClk}/p={pCyc} da={(prevAres < 0 ? 0 : aClk - prevAres)}/dp={(prevPuck < 0 ? 0 : pCyc - prevPuck)}");

                    if (funcCpsr || (funcReg >= 0)) {
                        Console.WriteLine($"  == FUNCTIONAL DIVERGENCE at instr {i} ==");
                        Console.WriteLine($"     aresPC=0x{aExec:X8}  puckR15=0x{cpu.GetRegister(index: 15):X8}  thumb={(pCpsr & 0x20u) != 0u}");

                        if (funcCpsr) {
                            Console.WriteLine($"     cpsr  ares=0x{aCpsr:X8}  puck=0x{pCpsr:X8}");
                        }

                        for (var r = 0; r < 15; ++r) {
                            var av = Convert.ToUInt32(value: f[2 + r], fromBase: 16);
                            var pv = cpu.GetRegister(index: r);

                            if (av != pv) {
                                Console.WriteLine($"     r{r,-2}  ares=0x{av:X8}  puck=0x{pv:X8}");
                            }
                        }

                        Console.WriteLine("     -- last instructions (oldest first) --");

                        foreach (var h in history) {
                            Console.WriteLine($"       {h}");
                        }

                        TimingSummary();

                        return 1;
                    }

                    prevAres = aClk;
                    prevPuck = pCyc;
                    machine.Step();
                }

                Console.WriteLine($"  == lockstep: NO functional divergence in {steps} instructions ==");
                TimingSummary();

                return 0;
            }
            finally {
                try {
                    if (!ares.HasExited) {
                        ares.Kill(entireProcessTree: true);
                    }
                }
                catch {
                    // best-effort cleanup
                }
            }
        }
    }

    /// <summary>Dumps every I/O register halfword after running a ROM, in the ares-cosim <c>iodump</c> format
    /// (<c>IO &lt;offset&gt; &lt;value&gt;</c>), so the two streams diff to find I/O read-mask divergences.</summary>
    public static void IoDump(string romPath, long steps) {
        if (!TryLoad(romPath: romPath, name: Path.GetFileName(romPath), out var provider, out var machine)) {
            return;
        }

        using (provider) {
            var bus = (GbaBus)machine.Bus;

            machine.Cpu.Reset();

            for (long i = 0; i < steps; ++i) {
                machine.Step();
            }

            for (uint off = 0; off < 0x400u; off += 2u) {
                Console.WriteLine($"IO {off:X3} {bus.DebugReadIo(offset: off):X4}");
            }
        }
    }

    /// <summary>Runs a ROM and dumps key machine state, to diagnose a game that boots to a blank screen.</summary>
    /// <summary>Dispatches the blank-screen boot diagnostics — <c>--probe &lt;rom&gt; &lt;steps&gt;</c> and
    /// <c>--emerald-trace &lt;rom&gt; &lt;loHex&gt; &lt;hiHex&gt; &lt;count&gt; [skipAfter]</c>; returns whether it
    /// handled the args (kept out of Program.cs to bound Main's cyclomatic complexity).</summary>
    public static bool TryDiagnostic(string[] args) {
        for (var index = 0; index < (args.Length - 2); ++index) {
            if (args[index] == "--probe") {
                Probe(romPath: args[index + 1], steps: long.Parse(args[index + 2]));

                return true;
            }
        }

        for (var index = 0; index < (args.Length - 4); ++index) {
            if (args[index] == "--emerald-trace") {
                EmeraldTrace(
                    romPath: args[index + 1],
                    triggerLo: Convert.ToUInt32(value: args[index + 2], fromBase: 16),
                    triggerHi: Convert.ToUInt32(value: args[index + 3], fromBase: 16),
                    count: long.Parse(args[index + 4]),
                    skipAfter: (args.Length > (index + 5)) ? long.Parse(args[index + 5]) : 0);

                return true;
            }
        }

        return false;
    }

    /// <summary>Full-boots a ROM, runs until the PC first enters [triggerLo, triggerHi), then dumps the next
    /// <paramref name="count"/> instructions with PC + r0..r6 + the SIO/timer/IRQ registers the link probe reads —
    /// to see exactly why Pokémon Emerald's link-init loops, with no external oracle.</summary>
    public static void EmeraldTrace(string romPath, uint triggerLo, uint triggerHi, long count, long skipAfter = 0) {
        var cartridge = new GbaCartridge(rom: File.ReadAllBytes(path: romPath));
        var services = new ServiceCollection();

        services.AddScoped<GbaBus>();
        services.AddScoped<IGbaBus>(implementationFactory: sp => sp.GetRequiredService<GbaBus>());
        _ = services.AddGameBoyAdvance();
        _ = services.AddReplacementBios(image: BiosImage);
        services.AddScoped<GbaCartridge>(implementationFactory: _ => cartridge);

        using var provider = services.BuildServiceProvider();
        var machine = provider.CreateScope().ServiceProvider.GetRequiredService<GameBoyAdvanceMachine>();

        machine.DirectBoot();
        machine.Cpu.Reset(); // full BIOS boot

        var bus = (GbaBus)machine.Bus;
        var cpu = machine.Cpu;

        long i = 0;
        const long cap = 250_000_000;
        var armed = false;

        while (i < cap) {
            var pc = cpu.GetRegister(index: 15);

            if ((pc >= triggerLo) && (pc < triggerHi) && (i >= skipAfter)) {
                armed = true;
                break;
            }

            machine.Step();
            ++i;
        }

        if (!armed) {
            Console.WriteLine($"  EmeraldTrace: trigger 0x{triggerLo:X}-0x{triggerHi:X} not hit within {cap} instrs");
            return;
        }

        Console.WriteLine($"  EmeraldTrace: armed at instr {i}; dumping {count} instructions:");

        for (long k = 0; k < count; ++k) {
            var pc = cpu.GetRegister(index: 15);
            var cpsr = cpu.Cpsr;
            Console.WriteLine(
                $"{k,5} pc={pc:X8} cpsr={cpsr:X8} r0={cpu.GetRegister(0):X8} r1={cpu.GetRegister(1):X8} "
                + $"r2={cpu.GetRegister(2):X8} r3={cpu.GetRegister(3):X8} r4={cpu.GetRegister(4):X8} "
                + $"r6={cpu.GetRegister(6):X8} | SIOCNT={bus.DebugReadIo(0x128):X4} SIOML0={bus.DebugReadIo(0x120):X4} "
                + $"IE={bus.DebugReadIo(0x200):X4} IF={bus.DebugReadIo(0x202):X4} IME={bus.DebugReadIo(0x208):X4} "
                + $"TM3={bus.DebugReadIo(0x10C):X4} KEY={bus.DebugReadIo(0x130):X4} DISPCNT={bus.DebugReadIo(0x000):X4}");
            machine.Step();
        }
    }

    public static void Probe(string romPath, long steps) {
        if (!File.Exists(romPath)) {
            Console.WriteLine($"  [SKIP] {Path.GetFileName(romPath)}: not found");
            return;
        }

        var sioWrites = new List<(long step, ushort value)>();
        var dispcntWrites = new List<(long step, uint pc, ushort value)>();
        var cartridge = new GbaCartridge(rom: File.ReadAllBytes(path: romPath));
        var services = new ServiceCollection();

        Console.WriteLine($"  backup={cartridge.Backup}  hasRtc={cartridge.HasRtc}");

        long stepCounter = 0;
        GameBoyAdvanceMachine? machineProbeRef = null;
        services.AddScoped<GbaBus>();
        services.AddScoped<IGbaBus>(implementationFactory: sp => {
            var inner = new TracingGbaBus(
                inner: sp.GetRequiredService<GbaBus>(),
                watchAddress: 0x04000000u,
                onStore: value => {
                    if (dispcntWrites.Count < 200) {
                        var pc = machineProbeRef?.Cpu.GetRegister(index: 15) ?? 0u;
                        dispcntWrites.Add((stepCounter, pc, (ushort)value));
                    }
                });
            return new TracingGbaBus(
                inner: inner,
                watchAddress: 0x04000128u,
                onStore: value => {
                    if (sioWrites.Count < 64) {
                        sioWrites.Add((stepCounter, (ushort)value));
                    }
                });
        });
        _ = services.AddGameBoyAdvance();
        _ = services.AddReplacementBios(image: BiosImage);
        services.AddScoped<GbaCartridge>(implementationFactory: _ => cartridge);

        using var provider = services.BuildServiceProvider();
        var machine = provider.CreateScope().ServiceProvider.GetRequiredService<GameBoyAdvanceMachine>();

        machineProbeRef = machine;
        machine.DirectBoot();

        // PUCK_GBA_FULLBOOT=1: undo the HLE direct-boot state and run the real BIOS intro from the reset vector,
        // exactly as ARES boots (cpu.Reset()). The default stays direct boot for quick game-state probes.
        if (Environment.GetEnvironmentVariable(variable: "PUCK_GBA_FULLBOOT") == "1") {
            machine.Cpu.Reset();
        }

        // Count BX-to-reset-vector events (soft resets). In ARM7TDMI after DirectBoot, the PC register
        // (which shows executing_addr + 8 in ARM mode) = 8 only when a branch to 0x00000000 fires the pipeline.
        // SWI goes to vector 0x08 → PC=0x10; IRQ goes to 0x18 → PC=0x20; only BX 0 gives PC < 0x10.
        var biosResets = 0L;

        for (stepCounter = 0; stepCounter < steps; ++stepCounter) {
            machine.Step();

            var pc = machine.Cpu.GetRegister(index: 15);

            if (pc < 0x10u) {
                ++biosResets;
            }
        }

        var bus = machine.Bus;
        uint Reg(uint a) => bus.Read16(address: a, access: BusAccessType.NonSequential);
        uint Reg32(uint a) => bus.Read32(address: a, access: BusAccessType.NonSequential);

        for (var r = 0; r < 16; r += 4) {
            Console.WriteLine($"  r{r,-2}=0x{machine.Cpu.GetRegister(r):X8}  r{r + 1,-2}=0x{machine.Cpu.GetRegister(r + 1):X8}  r{r + 2,-2}=0x{machine.Cpu.GetRegister(r + 2):X8}  r{r + 3,-2}=0x{machine.Cpu.GetRegister(r + 3):X8}");
        }

        Console.WriteLine($"  PC=0x{machine.Cpu.GetRegister(15):X8}  CPSR=0x{machine.Cpu.Cpsr:X8}");
        Console.WriteLine($"  DISPCNT=0x{Reg(0x04000000u):X4}  DISPSTAT=0x{Reg(0x04000004u):X4}  VCOUNT=0x{Reg(0x04000006u):X4}");
        Console.WriteLine($"  IE=0x{Reg(0x04000200u):X4}  IF=0x{Reg(0x04000202u):X4}  IME=0x{Reg(0x04000208u):X4}  WAITCNT=0x{Reg(0x04000204u):X4}");
        Console.WriteLine($"  SIOCNT=0x{Reg(0x04000128u):X4}  RCNT=0x{Reg(0x04000134u):X4}");
        Console.WriteLine($"  IRQ_handler=[0x03FFFFFC]=0x{Reg32(0x03007FFCu):X8}  bios_flags=[0x03FFFFF8]=0x{Reg32(0x03007FF8u):X8}");
        Console.WriteLine($"  SIO state struct @0x030078A0:");

        for (uint o = 0; o < 16u; o += 4u) {
            Console.WriteLine($"    +{o,2}: 0x{Reg32(0x030078A0u + o):X8}");
        }

        // Dump the stack (top 32 words) to see return addresses / call chain.
        var sp = machine.Cpu.GetRegister(index: 13);
        Console.WriteLine($"  Stack @0x{sp:X8} (top 32 words):");
        for (uint o = 0; o < 128u; o += 4u) {
            Console.WriteLine($"    [SP+{o,3}] @0x{sp + o:X8} = 0x{Reg32(sp + o):X8}");
        }

        // Dump IWRAM around the pushed R6 (callback/continuation address) seen at [SP+12].
        var savedR6 = Reg32(sp + 12u);
        Console.WriteLine($"  IWRAM around pushed-R6 continuation @0x{savedR6:X8}:");
        for (uint o = 0; o < 64u; o += 4u) {
            Console.WriteLine($"    [+{o,3}] @0x{savedR6 + o:X8} = 0x{Reg32(savedR6 + o):X8}");
        }

        // Dump IWRAM IRQ dispatcher (copied from ROM by game init).
        const uint irqHandler = 0x03002750u;
        Console.WriteLine($"  IWRAM IRQ dispatcher @0x{irqHandler:X8}:");
        for (uint o = 0; o < 256u; o += 4u) {
            Console.WriteLine($"    [+{o,3}] @0x{irqHandler + o:X8} = 0x{Reg32(irqHandler + o):X8}");
        }

        // Count of BX-to-reset-vector events (PC==8 in ARM mode, only reachable via BX to address 0).
        Console.WriteLine($"  BIOS soft-reset entries (BX 0x00000000 events) = {biosResets}");

        Console.WriteLine($"  DISPCNT writes ({dispcntWrites.Count} captured):");
        foreach (var (step, pc, value) in dispcntWrites) {
            Console.WriteLine($"    step={step,12}  pc=0x{pc:X8}  DISPCNT=0x{value:X4}");
        }

        Console.WriteLine($"  SIOCNT writes ({sioWrites.Count} captured):");

        foreach (var (step, value) in sioWrites) {
            Console.WriteLine($"    step={step,12}  SIOCNT=0x{value:X4}  start={(value & 0x80) != 0}  irq={(value & 0x4000) != 0}  clk_int={(value & 0x02) != 0}");
        }

        // Dump ROM around the key SIO-caller addresses to decode the "SIO failed" path.
        void DumpRom(string label, uint addr, int words = 32) {
            Console.WriteLine($"  ROM @0x{addr:X8} [{label}]:");
            for (uint i = 0; i < (uint)words; ++i) {
                Console.WriteLine($"    [+{i * 4,3}] 0x{addr + i * 4:X8} = 0x{Reg32(addr + i * 4):X8}");
            }
        }

        // ROM entry — first word is B 0x08000204; game init at 0x08000204 calls Thumb init at 0x080003A4.
        DumpRom(label: "game init 0x08000204", addr: 0x08000204u, words: 16);
        DumpRom(label: "thumb init 0x080003A4", addr: 0x080003A4u, words: 64);
        // What DISPCNT is written to 0 in game init (around 0x08001078).
        DumpRom(label: "DISPCNT-clear at 0x08001068", addr: 0x08001068u, words: 16);
        // CMP R0,#0x8001 comparison block and the BNE target.
        DumpRom(label: "sio-caller cmp+bne", addr: 0x082E42F0u, words: 32);
        // BNE target — "SIO failed / no link" path.
        DumpRom(label: "sio-failed path 0x082E4350", addr: 0x082E4350u, words: 32);
        // Timeout-exit block inside outer SIO function (word-aligned to 0x082E6DE8).
        DumpRom(label: "outer-sio timeout exit 0x082E6DE8", addr: 0x082E6DE8u, words: 32);

        var vramNonZero = 0;
        var palNonZero = 0;

        for (var a = 0x06000000u; a < 0x06018000u; a += 2u) {
            if (Reg(a) != 0u) {
                ++vramNonZero;
            }
        }

        for (var a = 0x05000000u; a < 0x05000400u; a += 2u) {
            if (Reg(a) != 0u) {
                ++palNonZero;
            }
        }

        var fb = machine.Framebuffer;
        var distinct = new HashSet<uint>();

        for (var i = 0; (i < fb.Length) && (distinct.Count < 16); ++i) {
            distinct.Add(fb[i]);
        }

        Console.WriteLine($"  VRAM non-zero halfwords={vramNonZero}  palette non-zero={palNonZero}  framebuffer distinct colors≈{distinct.Count}");
    }

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
        // Also capture all I/O timer register reads (0x04000100–0x04000110) and IF (0x04000202) for the
        // timer_connect diagnostic (#17). Key: address, value, result-count at time of read.
        var timer1Reads = new List<(int afterResults, uint value)>();
        var connectIoReads = new List<(int afterResults, uint address, uint value, long cycles)>();
        var connectIoWrites = new List<(int afterResults, uint address, uint value, long cycles)>();
        var cartridge = new GbaCartridge(rom: rom);
        var services = new ServiceCollection();

        var trace = Environment.GetEnvironmentVariable(variable: "PUCK_AGS_TRACE") == "1";
        GameBoyAdvanceMachine? machineRef = null;
        GbaBus? busRef = null;

        // Wire the tracing decorator in front of the real bus: register the concrete bus, then map IGbaBus to a
        // TracingGbaBus that wraps it. Registering IGbaBus first makes AddGameBoyAdvance's TryAdd defer to ours.
        services.AddScoped<GbaBus>();
        services.AddScoped<IGbaBus>(implementationFactory: sp => {
            busRef = sp.GetRequiredService<GbaBus>();

            return new TracingGbaBus(
                inner: busRef,
                watchAddress: 0x04u,
                onStore: value => {
                    if (trace && (results.Count < 8)) {
                        Console.WriteLine($"    [store] result#{results.Count} value=0x{value:X8} pc=0x{(machineRef?.Cpu.GetRegister(index: 15) ?? 0):X8}");
                    }

                    results.Add(item: value);
                },
                readWatchAddress: 0x04000100u,
                onRead: value => timerReads.Add((results.Count, value)),
                readWatchAddress2: 0x04000104u,
                onRead2: (_, value) => timer1Reads.Add((results.Count, value)),
                readRangeBase: 0x040000B0u,
                readRangeEnd: 0x04000210u,
                onReadRange: (addr, value) => connectIoReads.Add((results.Count, addr, value, busRef?.Cycles ?? 0)),
                writeRangeBase: 0x04000100u,
                writeRangeEnd: 0x04000110u,
                onWriteRange: (addr, value) => connectIoWrites.Add((results.Count, addr, value, busRef?.Cycles ?? 0)));
        });
        _ = services.AddGameBoyAdvance();
        _ = services.AddReplacementBios(image: BiosImage);
        services.AddScoped<GbaCartridge>(implementationFactory: _ => cartridge);

        using var provider = services.BuildServiceProvider();
        var machine = provider.CreateScope().ServiceProvider.GetRequiredService<GameBoyAdvanceMachine>();

        machineRef = machine;

        machine.DirectBoot();

        // Step until the result stream goes quiet: once results have started arriving, a long gap with no new
        // result means the suite has stalled (the SIO link-cable test) or finished.
        const long budget = 400_000_000;
        const long quietWindow = 12_000_000;
        var lastCount = 0;
        var lastChangeStep = 0L;

        for (long i = 1; i <= budget; ++i) {
            machine.Step();

            if (trace && ((i % 2_000_000L) == 0L)) {
                Console.WriteLine($"    [trace] step={i,12} pc=0x{machine.Cpu.GetRegister(index: 15):X8} dispcnt=0x{machine.Ppu.ReadRegister(offset: 0x00u):X4} vcount={machine.Ppu.ReadRegister(offset: 0x06u),3} results={results.Count}");
            }

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
                Console.WriteLine($"     [{i,2}] ours=0x{ours:X} expected=0x{expectedWait[i]:X} {(ours == expectedWait[i] ? "ok" : $"d={(long)ours - expectedWait[i]}")}");
            }
        }

        // prefetch test (#6) reads the timer twice (prefetch on → expect 0x18, off → 0x33).
        var prefetchReads = timerReads.Where(predicate: r => r.afterResults == 6).Select(selector: r => r.value).ToArray();
        Console.WriteLine($"  -- prefetch timer reads (afterResults==6): {string.Join(", ", prefetchReads.Select(v => $"0x{v:X}"))} (expect 0x18 on, 0x33 off) --");

        // cart-RAM (SRAM) wait test (#8) reads the timer 4 times (expect 0x1C,0x18,0x14,0x2C).
        uint[] expectedCart = [0x1C, 0x18, 0x14, 0x2C];
        var cartReads = timerReads.Where(predicate: r => r.afterResults == 8).Select(selector: r => r.value).ToArray();
        Console.WriteLine($"  -- cart-RAM timer reads (afterResults==8): {string.Join(", ", cartReads.Select(v => $"0x{v:X}"))} (expect 0x1C,0x18,0x14,0x2C) --");

        // prescaler test (#16) reads the timer once per prescaler mode (4 reads: /1, /64, /256, /1024).
        var prescalerReads = timerReads.Where(predicate: r => r.afterResults == 16).Select(selector: r => r.value).ToArray();
        Console.WriteLine($"  -- prescaler timer reads (afterResults==16): {string.Join(", ", prescalerReads.Select(v => $"0x{v:X}"))} ({prescalerReads.Length} reads, expect 4 values) --");

        // For any failed test: dump I/O reads and writes observed during it.
        for (var testIdx = 0; testIdx < results.Count; ++testIdx) {
            if (results[testIdx] == 0u) {
                continue;
            }

            var testLabel = (testIdx < s_agsTestNames.Length) ? s_agsTestNames[testIdx] : $"test #{testIdx}";

            var ioWritesForTest = connectIoWrites.Where(predicate: r => r.afterResults == testIdx).ToArray();

            if (ioWritesForTest.Length > 0) {
                Console.WriteLine($"  -- [{testIdx}] {testLabel} I/O writes: {ioWritesForTest.Length} total --");

                foreach (var (_, addr, val, cyc) in ioWritesForTest) {
                    Console.WriteLine($"     W [0x{addr:X8}] <- 0x{val:X6}  @cyc={cyc}");
                }
            }

            var ioForTest = connectIoReads.Where(predicate: r => r.afterResults == testIdx).ToArray();

            if (ioForTest.Length > 0) {
                Console.WriteLine($"  -- [{testIdx}] {testLabel} I/O reads: {ioForTest.Length} total --");

                foreach (var (_, addr, val, cyc) in ioForTest) {
                    Console.WriteLine($"     R [0x{addr:X8}] = 0x{val:X4}  @cyc={cyc}");
                }
            }
        }

        return failed;
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
}
