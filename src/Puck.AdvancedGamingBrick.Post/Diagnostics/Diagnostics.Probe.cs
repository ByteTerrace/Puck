using Microsoft.Extensions.DependencyInjection;

namespace Puck.AdvancedGamingBrick.Post;

// --probe <rom> <steps> | --link-init-trace <rom> <loHex> <hiHex> <count> [skip]: blank-screen boot diagnostics.
internal static partial class Diagnostics {
    /// <summary>Runs a ROM and dumps key machine state, to diagnose a game that boots to a blank screen.</summary>
    /// <summary>Dispatches the blank-screen boot diagnostics — <c>--probe &lt;rom&gt; &lt;steps&gt;</c> and
    /// <c>--link-init-trace &lt;rom&gt; &lt;loHex&gt; &lt;hiHex&gt; &lt;count&gt; [skipAfter]</c>; returns whether it
    /// handled the args (kept out of Program.cs to bound Main's cyclomatic complexity).</summary>
    public static bool TryDiagnostic(string[] args) {
        for (var index = 0; (index < (args.Length - 2)); ++index) {
            if (args[index] == "--probe") {
                Probe(romPath: args[(index + 1)], steps: long.Parse(s: args[(index + 2)]));

                return true;
            }
        }

        for (var index = 0; (index < (args.Length - 4)); ++index) {
            if (args[index] == "--link-init-trace") {
                LinkInitTrace(
                    romPath: args[(index + 1)],
                    triggerLo: Convert.ToUInt32(value: args[(index + 2)], fromBase: 16),
                    triggerHi: Convert.ToUInt32(value: args[(index + 3)], fromBase: 16),
                    count: long.Parse(s: args[(index + 4)]),
                    skipAfter: ((args.Length > (index + 5)) ? long.Parse(s: args[(index + 5)]) : 0));

                return true;
            }
        }

        return false;
    }

    /// <summary>Full-boots a ROM, runs until the PC first enters [triggerLo, triggerHi), then dumps the next
    /// <paramref name="count"/> instructions with PC + r0..r6 + the SIO/timer/IRQ registers the link probe reads —
    /// to see exactly why a cart's link-init loops, with no external oracle.</summary>
    public static void LinkInitTrace(string romPath, uint triggerLo, uint triggerHi, long count, long skipAfter = 0) {
        using var instance = AgbMachineFactory.Create(configuration: new AgbMachineConfiguration(bios: BiosImage, rom: File.ReadAllBytes(path: romPath)));
        var machine = instance.Machine;

        machine.DirectBoot();
        machine.Cpu.Reset(); // full BIOS boot

        var bus = (AgbBus)machine.Bus;
        var cpu = machine.Cpu;

        var i = 0L;
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
            Console.WriteLine(value: $"  LinkInitTrace: trigger 0x{triggerLo:X}-0x{triggerHi:X} not hit within {cap} instrs");
            return;
        }

        Console.WriteLine(value: $"  LinkInitTrace: armed at instr {i}; dumping {count} instructions:");

        for (long k = 0; (k < count); ++k) {
            var pc = cpu.GetRegister(index: 15);
            var cpsr = cpu.Cpsr;

            Console.WriteLine(
                (((($"{k,5} pc={pc:X8} cpsr={cpsr:X8} r0={cpu.GetRegister(0):X8} r1={cpu.GetRegister(1):X8} "
                + $"r2={cpu.GetRegister(2):X8} r3={cpu.GetRegister(3):X8} r4={cpu.GetRegister(4):X8} ")
                + $"r6={cpu.GetRegister(6):X8} | SIOCNT={bus.DebugReadIo(offset: 0x128):X4} SIOML0={bus.DebugReadIo(offset: 0x120):X4} ")
                + $"IE={bus.DebugReadIo(offset: 0x200):X4} IF={bus.DebugReadIo(offset: 0x202):X4} IME={bus.DebugReadIo(offset: 0x208):X4} ")
                + $"TM3={bus.DebugReadIo(offset: 0x10C):X4} KEY={bus.DebugReadIo(offset: 0x130):X4} DISPCNT={bus.DebugReadIo(offset: 0x000):X4}"));
            machine.Step();
        }
    }
    public static void Probe(string romPath, long steps) {
        if (!File.Exists(path: romPath)) {
            Console.WriteLine(value: $"  [SKIP] {Path.GetFileName(path: romPath)}: not found");
            return;
        }

        var sioWrites = new List<(long step, ushort value)>();
        var dispcntWrites = new List<(long step, uint pc, ushort value)>();

        var stepCounter = 0L;
        AdvancedGamingBrickMachine? machineProbeRef = null;

        using var instance = AgbMachineFactory.Create(
            configuration: new AgbMachineConfiguration(bios: BiosImage, rom: File.ReadAllBytes(path: romPath)),
            compose: services => {
                services.AddScoped<AgbBus>();
                services.AddScoped<IAgbBus>(implementationFactory: sp => {
                    var inner = new TracingAgbBus(
                        inner: sp.GetRequiredService<AgbBus>(),
                        watchAddress: 0x04000000u,
                        onStore: value => {
                            if (dispcntWrites.Count < 200) {
                                var pc = (machineProbeRef?.Cpu.GetRegister(index: 15) ?? 0u);

                                dispcntWrites.Add(item: (stepCounter, pc, (ushort)value));
                            }
                        });

                    return new TracingAgbBus(
                        inner: inner,
                        watchAddress: 0x04000128u,
                        onStore: value => {
                            if (sioWrites.Count < 64) {
                                sioWrites.Add(item: (stepCounter, (ushort)value));
                            }
                        });
                });
            });
        var machine = instance.Machine;
        var cartridge = instance.GetRequiredService<AgbCartridge>();

        Console.WriteLine(value: $"  backup={cartridge.Backup}  hasRtc={cartridge.HasRtc}");

        machineProbeRef = machine;
        machine.DirectBoot();

        // PUCK_AGB_FULLBOOT=1: undo the HLE direct-boot state and run the real BIOS intro from the reset vector
        // (cpu.Reset()), the same path a hardware power-on takes. The default stays direct boot for quick game-state probes.
        if (Environment.GetEnvironmentVariable(variable: "PUCK_AGB_FULLBOOT") == "1") {
            machine.Cpu.Reset();
        }

        // Count BX-to-reset-vector events (soft resets). In ARM7TDMI after DirectBoot, the PC register
        // (which shows executing_addr + 8 in ARM mode) = 8 only when a branch to 0x00000000 fires the pipeline.
        // SWI goes to vector 0x08 → PC=0x10; IRQ goes to 0x18 → PC=0x20; only BX 0 gives PC < 0x10.
        var biosResets = 0L;

        for (stepCounter = 0; (stepCounter < steps); ++stepCounter) {
            machine.Step();

            var pc = machine.Cpu.GetRegister(index: 15);

            if (pc < 0x10u) {
                ++biosResets;
            }
        }

        var bus = machine.Bus;

        uint Reg(uint a) => bus.Read16(address: a, access: BusAccessType.NonSequential);
        uint Reg32(uint a) => bus.Read32(address: a, access: BusAccessType.NonSequential);

        for (var r = 0; (r < 16); r += 4) {
            Console.WriteLine(value: $"  r{r,-2}=0x{machine.Cpu.GetRegister(r):X8}  r{(r + 1),-2}=0x{machine.Cpu.GetRegister((r + 1)):X8}  r{(r + 2),-2}=0x{machine.Cpu.GetRegister((r + 2)):X8}  r{(r + 3),-2}=0x{machine.Cpu.GetRegister((r + 3)):X8}");
        }

        Console.WriteLine(value: $"  PC=0x{machine.Cpu.GetRegister(15):X8}  CPSR=0x{machine.Cpu.Cpsr:X8}");
        Console.WriteLine(value: $"  DISPCNT=0x{Reg(a: 0x04000000u):X4}  DISPSTAT=0x{Reg(a: 0x04000004u):X4}  VCOUNT=0x{Reg(a: 0x04000006u):X4}");
        Console.WriteLine(value: $"  IE=0x{Reg(a: 0x04000200u):X4}  IF=0x{Reg(a: 0x04000202u):X4}  IME=0x{Reg(a: 0x04000208u):X4}  WAITCNT=0x{Reg(a: 0x04000204u):X4}");
        Console.WriteLine(value: $"  SIOCNT=0x{Reg(a: 0x04000128u):X4}  RCNT=0x{Reg(a: 0x04000134u):X4}");
        Console.WriteLine(value: $"  IRQ_handler=[0x03FFFFFC]=0x{Reg32(a: 0x03007FFCu):X8}  bios_flags=[0x03FFFFF8]=0x{Reg32(a: 0x03007FF8u):X8}");
        Console.WriteLine(value: $"  SIO state struct @0x030078A0:");

        for (uint o = 0; (o < 16u); o += 4u) {
            Console.WriteLine(value: $"    +{o,2}: 0x{Reg32(a: (0x030078A0u + o)):X8}");
        }

        // Dump the stack (top 32 words) to see return addresses / call chain.
        var sp = machine.Cpu.GetRegister(index: 13);

        Console.WriteLine(value: $"  Stack @0x{sp:X8} (top 32 words):");
        for (uint o = 0; (o < 128u); o += 4u) {
            Console.WriteLine(value: $"    [SP+{o,3}] @0x{(sp + o):X8} = 0x{Reg32((sp + o)):X8}");
        }

        // Dump IWRAM around the pushed R6 (callback/continuation address) seen at [SP+12].
        var savedR6 = Reg32((sp + 12u));

        Console.WriteLine(value: $"  IWRAM around pushed-R6 continuation @0x{savedR6:X8}:");
        for (uint o = 0; (o < 64u); o += 4u) {
            Console.WriteLine(value: $"    [+{o,3}] @0x{(savedR6 + o):X8} = 0x{Reg32(a: (savedR6 + o)):X8}");
        }

        // Dump IWRAM IRQ dispatcher (copied from ROM by game init).
        const uint irqHandler = 0x03002750u;

        Console.WriteLine(value: $"  IWRAM IRQ dispatcher @0x{irqHandler:X8}:");
        for (uint o = 0; (o < 256u); o += 4u) {
            Console.WriteLine(value: $"    [+{o,3}] @0x{(irqHandler + o):X8} = 0x{Reg32(a: (irqHandler + o)):X8}");
        }

        // Count of BX-to-reset-vector events (PC==8 in ARM mode, only reachable via BX to address 0).
        Console.WriteLine(value: $"  BIOS soft-reset entries (BX 0x00000000 events) = {biosResets}");

        Console.WriteLine(value: $"  DISPCNT writes ({dispcntWrites.Count} captured):");
        foreach (var (step, pc, value) in dispcntWrites) {
            Console.WriteLine(value: $"    step={step,12}  pc=0x{pc:X8}  DISPCNT=0x{value:X4}");
        }

        Console.WriteLine(value: $"  SIOCNT writes ({sioWrites.Count} captured):");

        foreach (var (step, value) in sioWrites) {
            Console.WriteLine(value: $"    step={step,12}  SIOCNT=0x{value:X4}  start={((value & 0x80) != 0)}  irq={((value & 0x4000) != 0)}  clk_int={((value & 0x02) != 0)}");
        }

        // Dump ROM around the key SIO-caller addresses to decode the "SIO failed" path.
        void DumpRom(string label, uint addr, int words = 32) {
            Console.WriteLine(value: $"  ROM @0x{addr:X8} [{label}]:");
            for (uint i = 0; (i < (uint)words); ++i) {
                Console.WriteLine(value: $"    [+{(i * 4),3}] 0x{(addr + (i * 4)):X8} = 0x{Reg32(a: (addr + (i * 4))):X8}");
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

        for (var a = 0x06000000u; (a < 0x06018000u); a += 2u) {
            if (Reg(a: a) != 0u) {
                ++vramNonZero;
            }
        }

        for (var a = 0x05000000u; (a < 0x05000400u); a += 2u) {
            if (Reg(a: a) != 0u) {
                ++palNonZero;
            }
        }

        var fb = machine.Framebuffer;
        var distinct = new HashSet<uint>();

        for (var i = 0; ((i < fb.Length) && (distinct.Count < 16)); ++i) {
            distinct.Add(fb[i]);
        }

        Console.WriteLine(value: $"  VRAM non-zero halfwords={vramNonZero}  palette non-zero={palNonZero}  framebuffer distinct colors≈{distinct.Count}");
    }
}
