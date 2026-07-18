using System.Diagnostics;

namespace Puck.AdvancedGamingBrick.Post;

// --lockstep <rom> <steps> [direct]: step Puck against the cosim oracle in lockstep to the first divergence.
internal static partial class Diagnostics {
    /// <summary>
    /// Lockstep differential against the cosim oracle (the cycle-stepped reference Puck is being realigned to).
    /// Spawns <c>ares-cosim.exe</c> on the same ROM/steps/BIOS, reads its per-instruction trace, and steps Puck
    /// in lockstep — comparing architectural state (cpsr + r0..r14) and per-instruction cycle deltas. Both boot
    /// the real BIOS, so the streams align 1:1 by instruction index. Halts at the first FUNCTIONAL divergence (a
    /// real bug, or the symptom of accumulated timing drift resolving a timing-paced branch differently) and
    /// reports the first TIMING-delta divergence + cumulative drift — the M-CYCLE target. Cosim path from
    /// <c>PUCK_ARES_COSIM</c> (default the gba-cosim dir); BIOS from <c>PUCK_AGB_BIOS</c>.
    /// </summary>
    public static int Lockstep(string romPath, long steps, bool direct = false) {
        if (!File.Exists(path: romPath)) {
            Console.WriteLine(value: $"  [SKIP] lockstep: rom not found at {romPath}");

            return 0;
        }

        var cosimExe = (Environment.GetEnvironmentVariable(variable: "PUCK_ARES_COSIM")
            ?? @"D:\Source\ByteTerrace\Temp\gba-cosim\ares-cosim.exe");
        var biosPath = (Environment.GetEnvironmentVariable(variable: "PUCK_AGB_BIOS")
            ?? @"D:\Source\ByteTerrace\Temp\GBA_bios.rom");

        if (!File.Exists(path: cosimExe)) {
            Console.WriteLine(value: $"  [SKIP] lockstep: ares-cosim not found at {cosimExe} (set PUCK_ARES_COSIM)");

            return 0;
        }

        var psi = new ProcessStartInfo {
            CreateNoWindow = true,
            FileName = cosimExe,
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };

        psi.ArgumentList.Add(item: romPath);
        psi.ArgumentList.Add(item: steps.ToString());
        psi.ArgumentList.Add(item: biosPath);

        if (direct) {
            psi.ArgumentList.Add(item: "direct");
        }

        using var cosim = Process.Start(startInfo: psi)!;
        var cosimOut = cosim.StandardOutput;

        if (!TryLoad(romPath: romPath, name: Path.GetFileName(path: romPath), out var instance)) {
            return 0;
        }

        using (instance) {
            var machine = instance.Machine;
            var cpu = machine.Cpu;
            var bus = (AgbBus)machine.Bus;

            // BIOS-boot mode: undo TryLoad's direct boot and run the BIOS reset to align with the oracle's full-BIOS
            // boot. Direct-boot mode: keep TryLoad's DirectBoot state so both cores start at the cartridge entry
            // (0x08000000), skipping the ~1M-instruction BIOS intro — for diffing ROM/game execution.
            if (!direct) {
                cpu.Reset();
            }

            var history = new Queue<string>(capacity: 16);
            long prevOracle = -1, prevPuck = -1, oracleClk0 = -1, puckCyc0 = -1;
            long firstTimingIdx = -1, firstTimingOracle = 0, firstTimingPuck = 0, timingMismatches = 0;
            long maxDrift = 0, maxDriftIdx = 0;
            var firstTimingPc = 0U;

            void TimingSummary() {
                if (firstTimingIdx < 0) {
                    Console.WriteLine(value: "  timing: per-instruction cycle deltas matched the oracle exactly.");
                } else {
                    Console.WriteLine(value: $"  timing: FIRST cycle-delta divergence at instr {firstTimingIdx} (oraclePC=0x{firstTimingPc:X8}): oracle d={firstTimingOracle} puck d={firstTimingPuck}");
                    Console.WriteLine(value: $"  timing: {timingMismatches} delta mismatches; max cumulative drift (puck-oracle) = {maxDrift} at instr {maxDriftIdx}");
                }
            }

            try {
                for (long i = 0; (i < steps); ++i) {
                    var line = cosimOut.ReadLine();

                    if (line is null) {
                        Console.WriteLine(value: $"  oracle stream ended at instr {i}");

                        break;
                    }

                    var f = line.Split(separator: ' ', options: StringSplitOptions.RemoveEmptyEntries);

                    if (f.Length < 19) {
                        continue;
                    }

                    // oracle columns: f0=execAddr f1=cpsr f2..f17=r0..r15 f18=clock
                    var oExec = Convert.ToUInt32(value: f[0], fromBase: 16);
                    var oCpsr = Convert.ToUInt32(value: f[1], fromBase: 16);
                    var oClk = long.Parse(s: f[18]);

                    var pCpsr = cpu.Cpsr;
                    var pCyc = bus.Cycles;

                    if (oracleClk0 < 0) {
                        oracleClk0 = oClk;
                        puckCyc0 = pCyc;
                    }

                    // functional compare: cpsr + r0..r14 (architectural state, immune to PC-representation offset)
                    var funcCpsr = (oCpsr != pCpsr);
                    var funcReg = -1;

                    for (var r = 0; ((r < 15) && (funcReg < 0)); ++r) {
                        if (Convert.ToUInt32(value: f[(2 + r)], fromBase: 16) != cpu.GetRegister(index: r)) {
                            funcReg = r;
                        }
                    }

                    if (prevOracle >= 0) {
                        var da = (oClk - prevOracle);
                        var dp = (pCyc - prevPuck);

                        if (da != dp) {
                            ++timingMismatches;

                            if (firstTimingIdx < 0) {
                                firstTimingIdx = i;
                                firstTimingOracle = da;
                                firstTimingPuck = dp;
                                firstTimingPc = oExec;
                            }
                        }

                        var drift = ((pCyc - puckCyc0) - (oClk - oracleClk0));

                        if (Math.Abs(value: drift) > Math.Abs(value: maxDrift)) {
                            maxDrift = drift;
                            maxDriftIdx = i;
                        }
                    }

                    if (history.Count >= 16) {
                        _ = history.Dequeue();
                    }

                    history.Enqueue(item: $"#{i,8} oraclePC={oExec:X8} cpsr o={oCpsr:X8}/p={pCpsr:X8} cyc o={oClk}/p={pCyc} do={((prevOracle < 0) ? 0 : (oClk - prevOracle))}/dp={((prevPuck < 0) ? 0 : (pCyc - prevPuck))}");

                    if (funcCpsr || (funcReg >= 0)) {
                        Console.WriteLine(value: $"  == FUNCTIONAL DIVERGENCE at instr {i} ==");
                        Console.WriteLine(value: $"     oraclePC=0x{oExec:X8}  puckR15=0x{cpu.GetRegister(index: 15):X8}  thumb={((pCpsr & 0x20u) != 0u)}");

                        if (funcCpsr) {
                            Console.WriteLine(value: $"     cpsr  oracle=0x{oCpsr:X8}  puck=0x{pCpsr:X8}");
                        }

                        for (var r = 0; (r < 15); ++r) {
                            var ov = Convert.ToUInt32(value: f[(2 + r)], fromBase: 16);
                            var pv = cpu.GetRegister(index: r);

                            if (ov != pv) {
                                Console.WriteLine(value: $"     r{r,-2}  oracle=0x{ov:X8}  puck=0x{pv:X8}");
                            }
                        }

                        Console.WriteLine(value: "     -- last instructions (oldest first) --");

                        foreach (var h in history) {
                            Console.WriteLine(value: $"       {h}");
                        }

                        TimingSummary();

                        return 1;
                    }

                    prevOracle = oClk;
                    prevPuck = pCyc;
                    machine.Step();
                }

                Console.WriteLine(value: $"  == lockstep: NO functional divergence in {steps} instructions ==");
                TimingSummary();

                return 0;
            } finally {
                try {
                    if (!cosim.HasExited) {
                        cosim.Kill(entireProcessTree: true);
                    }
                } catch {
                    // best-effort cleanup
                }
            }
        }
    }
}
