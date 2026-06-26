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
    public void DumpSpriteVram() {
        Assert.SkipUnless(RomCatalog.IsAvailable, "no corpus");

        var rom = Path.Combine(RomCatalog.Root!, "mealybug-tearoom-tests", "ppu", "m3_lcdc_obj_en_change.gb");
        var bootPath = Path.Combine(RomCatalog.Root!, "dmg_boot.bin");
        var boot = File.Exists(bootPath) ? File.ReadAllBytes(bootPath) : null;
        var machine = new AresMachine(File.ReadAllBytes(rom), color: false, bootRom: boot);
        m_output.WriteLine($"boot rom: {(boot is null ? "NONE" : boot.Length + " bytes")}");

        for (var f = 0; f < 24; f += 1) {
            var guard = 0;

            while (!machine.Ppu.ConsumeFrameReady() && (guard < 200_000)) {
                machine.Step();
                guard += 1;
            }
        }

        // $8000..$803F = first 4 OBJ tiles (unsigned). $9000.. = signed BG tiles.
        var obj = new System.Text.StringBuilder();
        var nonZero8000 = 0;

        for (var a = 0x8000; a < 0x8100; a += 1) {
            var b = machine.Peek((ushort)a);

            if (b != 0) {
                nonZero8000 += 1;
            }
        }

        for (var a = 0x8000; a < 0x8020; a += 1) {
            obj.Append(machine.Peek((ushort)a).ToString("X2")).Append(' ');
        }

        var nonZero9000 = 0;

        for (var a = 0x9000; a < 0x9100; a += 1) {
            if (machine.Peek((ushort)a) != 0) {
                nonZero9000 += 1;
            }
        }

        var oam = new System.Text.StringBuilder();

        for (var a = 0xFE00; a < 0xFE20; a += 1) {
            oam.Append(machine.Peek((ushort)a).ToString("X2")).Append(' ');
        }

        m_output.WriteLine($"$8000-$80FF nonzero bytes: {nonZero8000}/256");
        m_output.WriteLine($"$9000-$90FF nonzero bytes: {nonZero9000}/256");
        m_output.WriteLine("$8000: " + obj);
        m_output.WriteLine("OAM[0..32]: " + oam);

        var totalNonZero = 0;
        var firstNonZero = -1;

        for (var a = 0x8000; a <= 0x9FFF; a += 1) {
            if (machine.Peek((ushort)a) != 0) {
                totalNonZero += 1;

                if (firstNonZero < 0) {
                    firstNonZero = a;
                }
            }
        }

        var tile19 = new System.Text.StringBuilder();

        for (var a = 0x8190; a < 0x81A0; a += 1) {
            tile19.Append(machine.Peek((ushort)a).ToString("X2")).Append(' ');
        }

        m_output.WriteLine($"$8000-$9FFF total nonzero: {totalNonZero}/8192; first nonzero @ {(firstNonZero < 0 ? "NONE" : "$" + firstNonZero.ToString("X4"))}");
        m_output.WriteLine("tile 0x19 ($8190): " + tile19);
        m_output.WriteLine($"VRAM writes during run: {machine.Ppu.VramWrites}");
    }

    [Fact]
    public void MealybugPpuScorecard() {
        Assert.SkipUnless(RomCatalog.IsAvailable, "no corpus");

        var ppuDir = Path.Combine(RomCatalog.Root!, "mealybug-tearoom-tests", "ppu");

        Assert.SkipUnless(Directory.Exists(ppuDir), "no mealybug ppu dir");

        var total = 0;
        var exact = 0;

        var bootPath = Path.Combine(RomCatalog.Root!, "dmg_boot.bin");
        var boot = File.Exists(bootPath) ? File.ReadAllBytes(bootPath) : null;

        foreach (var rom in Directory.GetFiles(ppuDir, "*.gb").OrderBy(static p => p)) {
            var reference = Path.ChangeExtension(rom, null) + "_dmg_blob.png";

            if (!File.Exists(reference)) {
                continue;
            }

            var mismatches = RunAndCompare(rom: rom, referencePath: reference, bootRom: boot);

            total += 1;

            if (mismatches == 0) {
                exact += 1;
            }

            m_output.WriteLine($"{Path.GetFileNameWithoutExtension(rom),-32} {mismatches,6} mismatches{(mismatches == 0 ? "  EXACT" : "")}");
        }

        m_output.WriteLine($"=== ares-core mealybug PPU: {exact}/{total} pixel-exact ===");
    }

    private static int RunAndCompare(string rom, string referencePath, byte[]? bootRom) {
        var reference = Imaging.PngDecoder.Decode(referencePath);
        var machine = new AresMachine(File.ReadAllBytes(rom), color: false, bootRom: bootRom);

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
