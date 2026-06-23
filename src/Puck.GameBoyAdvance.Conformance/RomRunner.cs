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

    public static void Render(string romPath, string outputPath) {
        if (!TryLoad(romPath: romPath, name: Path.GetFileName(romPath), out var provider, out var machine)) {
            return;
        }

        using (provider) {
            // Run long enough for the ROM to finish its vsync wait and draw its result, rather than stopping at
            // the first stable-PC loop (which would catch it mid-vsync, before anything is drawn).
            for (long i = 0; i < 6_000_000; ++i) {
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
