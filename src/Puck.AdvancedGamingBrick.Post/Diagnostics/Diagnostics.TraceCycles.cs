namespace Puck.AdvancedGamingBrick.Post;

// --trace-cycles <rom> <steps>: per-instruction cycle trace, to diff against the cosim oracle.
internal static partial class Diagnostics {
    public static void TraceCycles(string romPath, long steps) {
        if (!TryLoad(romPath: romPath, name: Path.GetFileName(path: romPath), out var instance)) {
            return;
        }

        using (instance) {
            var machine = instance.Machine;
            var bus = (AgbBus)machine.Bus;
            var prev = bus.Cycles;
            var lastPc = 0xFFFFFFFFu;
            var stable = 0;

            for (long i = 0; (i < steps); ++i) {
                var pc = machine.Cpu.GetRegister(index: 15);

                machine.Step();

                var now = bus.Cycles;

                Console.WriteLine(value: $"{i,5}  pc={pc:X8}  cyc={now}  d={(now - prev)}");

                prev = now;

                if (pc == lastPc) {
                    if (++stable > 4) {
                        break;
                    }
                } else {
                    stable = 0;
                }

                lastPc = pc;
            }

            Console.WriteLine(value: $"RESULT[0x02000000] = 0x{machine.Bus.Read32(address: 0x02000000u, access: BusAccessType.NonSequential):X8} (timer value the test reads)");
        }
    }
}
