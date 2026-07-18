namespace Puck.AdvancedGamingBrick.Post;

// --trace-crash <rom>: report the first branch into unmapped memory.
internal static partial class Diagnostics {
    public static void TraceCrash(string romPath) {
        if (!TryLoad(romPath: romPath, name: Path.GetFileName(path: romPath), out var instance)) {
            return;
        }

        using (instance) {
            var machine = instance.Machine;
            var cpu = machine.Cpu;

            for (long i = 0; (i < 30_000_000); ++i) {
                var pcBefore = cpu.GetRegister(index: 15);
                var thumb = ((cpu.Cpsr & 0x20u) != 0u);

                machine.Step();

                var pc = cpu.GetRegister(index: 15);
                var region = (pc >> 24);
                var valid = ((pc < 0x4000u)
                    || (region == 0x02u)   // EWRAM
                    || (region == 0x03u)   // IWRAM
                    || (region == 0x05u)   // palette
                    || (region == 0x06u)   // VRAM
                    || ((region >= 0x08u) && (region <= 0x0Du))); // ROM + mirrors

                if (valid) {
                    continue;
                }

                var culprit = (pcBefore - (thumb ? 4u : 8u));

                Console.WriteLine(value: $"  CRASH at step {i}: branched to 0x{pc:X8}");
                Console.WriteLine(value: $"  culprit instruction @0x{culprit:X8} = 0x{machine.Bus.Read32(address: culprit, access: BusAccessType.NonSequential):X8} (thumb={thumb})");

                for (var r = 0; (r < 16); r += 4) {
                    Console.WriteLine(value: $"    r{r,-2}=0x{cpu.GetRegister(r):X8}  r{(r + 1),-2}=0x{cpu.GetRegister((r + 1)):X8}  r{(r + 2),-2}=0x{cpu.GetRegister((r + 2)):X8}  r{(r + 3),-2}=0x{cpu.GetRegister((r + 3)):X8}");
                }

                Console.WriteLine(value: $"    cpsr=0x{cpu.Cpsr:X8}");

                return;
            }

            Console.WriteLine(value: "  no crash within step budget");
        }
    }
}
