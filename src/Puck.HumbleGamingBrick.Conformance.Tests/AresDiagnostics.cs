using Puck.HumbleGamingBrick.Ares;
using Xunit;

namespace Puck.HumbleGamingBrick.Conformance.Tests;

/// <summary>
/// Measures the ares-architecture core against the mealybug PPU screenshot suite (DMG references). This is the
/// working harness for the mode-3 FIFO accuracy push: it reports per-test pixel-mismatch counts so progress toward
/// pixel-exact is visible test-by-test.
/// </summary>
public sealed class AresDiagnostics {
    private readonly ITestOutputHelper m_output;

    public AresDiagnostics(ITestOutputHelper output) =>
        m_output = output;

    [Fact]
    public void MealybugPpuScorecard() {
        Assert.SkipUnless(RomCatalog.IsAvailable, "no corpus");

        var ppuDir = Path.Combine(RomCatalog.Root!, "mealybug-tearoom-tests", "ppu");

        Assert.SkipUnless(Directory.Exists(ppuDir), "no mealybug ppu dir");

        var total = 0;
        var exact = 0;

        foreach (var rom in Directory.GetFiles(ppuDir, "*.gb").OrderBy(static p => p)) {
            var reference = Path.ChangeExtension(rom, null) + "_dmg_blob.png";

            if (!File.Exists(reference)) {
                continue;
            }

            var mismatches = RunAndCompare(rom: rom, referencePath: reference);

            total += 1;

            if (mismatches == 0) {
                exact += 1;
            }

            m_output.WriteLine($"{Path.GetFileNameWithoutExtension(rom),-32} {mismatches,6} mismatches{(mismatches == 0 ? "  EXACT" : "")}");
        }

        m_output.WriteLine($"=== ares-core mealybug PPU: {exact}/{total} pixel-exact ===");
    }

    private static int RunAndCompare(string rom, string referencePath) {
        var reference = Imaging.PngDecoder.Decode(referencePath);
        var machine = new AresMachine(File.ReadAllBytes(rom), color: false);

        // Run enough frames to reach the steady test image; each frame is instruction-capped to avoid hangs.
        for (var frame = 0; frame < 24; frame += 1) {
            var guard = 0;

            while (!machine.Ppu.ConsumeFrameReady() && (guard < 200_000)) {
                machine.Step();
                guard += 1;
            }
        }

        var frameBuffer = machine.Ppu.Framebuffer;
        var mismatches = 0;

        for (var i = 0; i < reference.Pixels.Length; i += 1) {
            if ((frameBuffer[i] & 0xFFFFFFu) != (reference.Pixels[i] & 0xFFFFFFu)) {
                mismatches += 1;
            }
        }

        return mismatches;
    }
}
