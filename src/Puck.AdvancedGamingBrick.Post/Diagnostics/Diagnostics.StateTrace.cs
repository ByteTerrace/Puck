namespace Puck.AdvancedGamingBrick.Post;

// --statetrace <rom> <steps>: full per-instruction CPU state, to diff against the cosim oracle's --statetrace.
internal static partial class Diagnostics {
    /// <summary>Full-state co-simulation trace: per instruction prints PC, CPSR, r0..r14, and cumulative cycles
    /// (state BEFORE the instruction executes), matching the cosim oracle's <c>--statetrace</c> format. Diffing the
    /// architectural registers (not just PC) finds the first true divergence unambiguously — immune to the
    /// pipeline PC offset and to single-instruction count slips that defeat a naive line-by-line PC diff.</summary>
    public static void StateTrace(string romPath, long steps) {
        if (!TryLoad(romPath: romPath, name: Path.GetFileName(path: romPath), out var instance)) {
            return;
        }

        using (instance) {
            var machine = instance.Machine;
            var cpu = machine.Cpu;
            var bus = (AgbBus)machine.Bus;
            using var output = new StreamWriter(stream: Console.OpenStandardOutput(), bufferSize: (1 << 20));
            var sb = new System.Text.StringBuilder(capacity: 160);

            // Boot through the BIOS reset routine (undo TryLoad's direct boot) so the trace aligns with the oracle.
            cpu.Reset();

            for (long i = 0; (i < steps); ++i) {
                sb.Clear();
                sb.Append(value: cpu.GetRegister(index: 15).ToString(format: "X8"));
                sb.Append(value: ' ').Append(value: cpu.Cpsr.ToString(format: "X8"));

                for (var r = 0; (r < 15); ++r) {
                    sb.Append(value: ' ').Append(value: cpu.GetRegister(index: r).ToString(format: "X8"));
                }

                sb.Append(value: ' ').Append(value: bus.Cycles);
                output.WriteLine(value: sb);

                machine.Step();
            }
        }
    }
}
