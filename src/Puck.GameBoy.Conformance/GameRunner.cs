using System.Runtime.InteropServices;
using Puck.Capture;

namespace Puck.GameBoy.Conformance;

/// <summary>
/// Boots a real cartridge from its post-boot state, runs a number of frames, and writes the PPU framebuffer to a
/// PNG. This is the end-to-end validation: a commercial game driving the CPU, timer, OAM DMA, and the full pixel
/// pipeline to produce a recognizable image is a far stronger correctness signal than any single synthetic test.
/// </summary>
internal static class GameRunner {
    public static int Run(string romPath, int frames, string outputPath, TextWriter output) {
        if (!File.Exists(path: romPath)) {
            output.WriteLine(value: $"run: ROM not found: {romPath}");

            return 2;
        }

        var machine = new GameBoyMachine(
            cartridge: Cartridge.Load(rom: File.ReadAllBytes(path: romPath)),
            model: ConsoleModel.Dmg
        );

        // Step the CPU until the PPU has completed the requested number of frames, with a generous cycle ceiling
        // so a stuck ROM cannot spin forever.
        var rendered = 0;
        var ceiling = (((long)frames * 80_000L) + 20_000_000L);

        for (var step = 0L; (step < ceiling) && (rendered < frames); step += 1) {
            machine.Step();

            if (machine.Ppu.ConsumeFrameReady()) {
                rendered += 1;
            }
        }

        var pixels = MemoryMarshal.AsBytes(span: machine.Ppu.Framebuffer);

        PngEncoder.Write(
            height: Ppu.ScreenHeight,
            path: outputPath,
            rgba: pixels,
            width: Ppu.ScreenWidth
        );

        output.WriteLine(value: $"run: {Path.GetFileName(path: romPath)} -> {rendered} frames -> {outputPath}");

        return 0;
    }
}
