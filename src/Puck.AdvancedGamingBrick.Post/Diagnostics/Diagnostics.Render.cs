using System.Runtime.InteropServices;
using Puck.Capture;

namespace Puck.AdvancedGamingBrick.Post;

// --render <rom> <out.png> [steps]: boot a ROM and dump its framebuffer, to eyeball the PPU output.
internal static partial class Diagnostics {
    public static void Render(string romPath, string outputPath, long steps = 6_000_000) {
        if (!TryLoad(romPath: romPath, name: Path.GetFileName(path: romPath), out var instance)) {
            return;
        }

        using (instance) {
            var machine = instance.Machine;

            // PUCK_AGB_FULLBOOT=1: run the real BIOS intro then jump to the cartridge (cpu.Reset), instead of the
            // direct-boot post-BIOS state. Some carts depend on full-BIOS-boot side effects.
            if (Environment.GetEnvironmentVariable(variable: "PUCK_AGB_FULLBOOT") == "1") {
                machine.Cpu.Reset();
            }

            // Run long enough for the ROM to finish its vsync wait and draw its result, rather than stopping at
            // the first stable-PC loop (which would catch it mid-vsync, before anything is drawn).
            for (long i = 0; (i < steps); ++i) {
                machine.Step();
            }

            PngEncoder.Write(
                height: 160,
                path: outputPath,
                rgba: MemoryMarshal.AsBytes(span: machine.Framebuffer),
                width: 240);

            Console.WriteLine(value: $"  rendered {Path.GetFileName(path: romPath)} -> {outputPath}");
        }
    }
}
