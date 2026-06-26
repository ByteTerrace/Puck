using Xunit;

namespace Puck.HumbleGamingBrick.Conformance.Tests;

/// <summary>Temporary diagnostic.</summary>
public sealed class Diagnostics {
    private readonly ITestOutputHelper m_output;

    public Diagnostics(ITestOutputHelper output) =>
        m_output = output;

    [Fact]
    public void DumpMealybug() {
        Assert.SkipUnless(RomCatalog.IsAvailable, "no corpus");

        var rom = Path.Combine(RomCatalog.Root!, "mealybug-tearoom-tests", "ppu", "m3_bgp_change.gb");
        var reference = Imaging.PngDecoder.Decode(Path.Combine(RomCatalog.Root!, "mealybug-tearoom-tests", "ppu", "m3_bgp_change_dmg_blob.png"));

        using var handle = MachineFactory.Create(File.ReadAllBytes(rom), ConsoleModel.Dmg);
        var machine = handle.Machine;

        machine.Ppu.FrameBlendingEnabled = false;

        var bus = machine.Bus;
        var instructions = 0L;

        _ = machine.Ppu.ConsumeFrameReady();

        while (bus.ElapsedDots < (60UL * 70224UL)) {
            if ((instructions > 16L) && (bus.ReadByte(machine.Cpu.ProgramCounter) == 0x40)) {
                break;
            }

            machine.Step();
            instructions += 1L;
        }

        var frame = machine.Ppu.Framebuffer;
        var mismatches = 0;

        for (var i = 0; i < reference.Pixels.Length; i += 1) {
            if ((frame[i] & 0xFFFFFFu) != (reference.Pixels[i] & 0xFFFFFFu)) {
                mismatches += 1;
            }
        }

        m_output.WriteLine($"mismatches={mismatches}/{reference.Pixels.Length}");
        m_output.WriteLine("row 8 act: " + Trans(frame, 8));
        m_output.WriteLine("row 8 ref: " + Trans(reference.Pixels, 8));
        m_output.WriteLine("row 0 act: " + Trans(frame, 0));
        m_output.WriteLine("row 0 ref: " + Trans(reference.Pixels, 0));
        m_output.WriteLine("row 20 act: " + Trans(frame, 20));
        m_output.WriteLine("row 20 ref: " + Trans(reference.Pixels, 20));
    }

    private static string Trans(ReadOnlySpan<uint> pixels, int y) {
        var parts = new List<string>();
        var last = -1;

        for (var x = 0; x < 160; x += 1) {
            var shade = (pixels[(y * 160) + x] & 0xFFu) switch { 0xFF => 0, 0xAA => 1, 0x55 => 2, _ => 3 };

            if (shade != last) {
                parts.Add($"x{x}={shade}");
                last = shade;
            }
        }

        return string.Join(" ", parts);
    }
}
