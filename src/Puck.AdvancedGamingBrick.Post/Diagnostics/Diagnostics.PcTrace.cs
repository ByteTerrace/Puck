namespace Puck.AdvancedGamingBrick.Post;

// --pctrace <rom> <steps>: print executing 0x08… instruction addresses, to diff against the cosim oracle.
internal static partial class Diagnostics {
    /// <summary>
    /// Traces per-instruction cycle accounting for a ROM, to diff against the cycle-exact cosim oracle. Prints
    /// (step, PC, cumulative cycles, delta) for each instruction, plus the final value the micro-ROM stored to
    /// EWRAM (0x02000000) — the same number the AGS wait-state/prescaler tests compare against hardware.
    /// </summary>
    /// <summary>Prints the executing instruction address for each game-ROM (0x08…) instruction, matching the
    /// cosim oracle's --pctrace format, so the two streams can be diffed to find the first execution divergence.</summary>
    public static void PcTrace(string romPath, long steps) {
        if (!TryLoad(romPath: romPath, name: Path.GetFileName(path: romPath), out var instance)) {
            return;
        }

        using (instance) {
            var machine = instance.Machine;
            var cpu = machine.Cpu;
            var bus = (AgbBus)machine.Bus;
            var output = Console.Out;

            // Boot through the BIOS reset routine (undo TryLoad's direct boot) so the trace aligns with the oracle's
            // full-BIOS boot, letting the first true execution divergence be found.
            cpu.Reset();

            for (long i = 0; (i < steps); ++i) {
                var thumb = ((cpu.Cpsr & 0x20u) != 0u);

                output.WriteLine(value: $"{cpu.GetRegister(index: 15):X8} {(thumb ? 'T' : 'A')} {bus.Cycles}");

                machine.Step();
            }
        }
    }
}
