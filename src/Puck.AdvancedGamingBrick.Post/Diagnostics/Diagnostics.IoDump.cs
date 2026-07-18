namespace Puck.AdvancedGamingBrick.Post;

// --iodump <rom> <steps>: dump every I/O register halfword, to diff against the cosim oracle's iodump.
internal static partial class Diagnostics {
    /// <summary>Dumps every I/O register halfword after running a ROM, in the cosim oracle's <c>iodump</c> format
    /// (<c>IO &lt;offset&gt; &lt;value&gt;</c>), so the two streams diff to find I/O read-mask divergences.</summary>
    public static void IoDump(string romPath, long steps) {
        if (!TryLoad(romPath: romPath, name: Path.GetFileName(path: romPath), out var instance)) {
            return;
        }

        using (instance) {
            var machine = instance.Machine;
            var bus = (AgbBus)machine.Bus;

            machine.Cpu.Reset();

            for (long i = 0; (i < steps); ++i) {
                machine.Step();
            }

            for (uint off = 0; (off < 0x400u); off += 2u) {
                Console.WriteLine(value: $"IO {off:X3} {bus.DebugReadIo(offset: off):X4}");
            }
        }
    }
}
