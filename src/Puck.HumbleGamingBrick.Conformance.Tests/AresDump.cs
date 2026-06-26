using System.Text;
using Puck.HumbleGamingBrick.Ares;
using Xunit;

namespace Puck.HumbleGamingBrick.Conformance.Tests;

/// <summary>
/// Per-line/per-pixel diagnostic dumper for a single mealybug ROM (set <c>PUCK_DUMP_ROM</c> to the test name, e.g.
/// <c>m3_wx_6_change</c>). Emits a side-by-side ASCII grid of rendered vs reference output and per-row mismatch
/// counts, plus the ability to "shift compare" (does my line N match ref line N-1?) for diagnosing vertical phase.
/// </summary>
public sealed class AresDump {
    private const int W = 160;
    private const int H = 144;

    private readonly ITestOutputHelper m_output;

    public AresDump(ITestOutputHelper output) =>
        m_output = output;

    private static char Shade(uint argb) {
        var v = (argb & 0xFFu);

        return v switch {
            0xFF => '.',  // white
            0xAA => '+',  // light
            0x55 => 'o',  // dark
            _ => '#',     // black
        };
    }

    [Fact]
    public void DumpRom() {
        var name = Environment.GetEnvironmentVariable("PUCK_DUMP_ROM");

        Assert.SkipUnless(!string.IsNullOrEmpty(name), "PUCK_DUMP_ROM not set");
        Assert.SkipUnless(RomCatalog.IsAvailable, "no corpus");

        var ppuDir = Path.Combine(RomCatalog.Root!, "mealybug-tearoom-tests", "ppu");
        var rom = Path.Combine(ppuDir, name + ".gb");
        var reference = Path.Combine(ppuDir, name + "_dmg_blob.png");

        Assert.True(File.Exists(rom), "rom missing: " + rom);
        Assert.True(File.Exists(reference), "ref missing: " + reference);

        var refImg = Imaging.PngDecoder.Decode(reference);
        var bootPath = Path.Combine(RomCatalog.Root!, "dmg_boot.bin");
        var boot = File.Exists(bootPath) ? File.ReadAllBytes(bootPath) : null;
        var machine = new AresMachine(File.ReadAllBytes(rom), color: false, bootRom: boot);

        for (var frame = 0; frame < 24; frame += 1) {
            var guard = 0;

            while (!machine.Ppu.ConsumeFrameReady() && (guard < 200_000)) {
                machine.Step();
                guard += 1;
            }
        }

        var fb = machine.Ppu.Framebuffer.ToArray();

        // Per-row mismatch counts.
        m_output.WriteLine("=== per-row mismatches (row: same-line, vs-prev-line, vs-next-line) ===");

        var totalSame = 0;

        for (var y = 0; y < H; y += 1) {
            var same = 0;
            var prev = 0;
            var next = 0;

            for (var x = 0; x < W; x += 1) {
                var mine = (fb[(y * W) + x] & 0xFFFFFFu);

                if (mine != (refImg.Pixels[(y * W) + x] & 0xFFFFFFu)) {
                    same += 1;
                }

                if ((y > 0) && (mine != (refImg.Pixels[((y - 1) * W) + x] & 0xFFFFFFu))) {
                    prev += 1;
                }

                if ((y < (H - 1)) && (mine != (refImg.Pixels[((y + 1) * W) + x] & 0xFFFFFFu))) {
                    next += 1;
                }
            }

            totalSame += same;

            if ((same != 0) || (prev != 0) || (next != 0)) {
                m_output.WriteLine($"row {y,3}: same={same,3} prev={prev,3} next={next,3}");
            }
        }

        m_output.WriteLine($"TOTAL same-line mismatches = {totalSame}");

        // Compact per-mismatch listing: row, x, mine-shade -> ref-shade (the first ~120 mismatches).
        var listed = 0;

        for (var y = 0; (y < H) && (listed < 120); y += 1) {
            for (var x = 0; (x < W) && (listed < 120); x += 1) {
                var mine = (fb[(y * W) + x] & 0xFFFFFFu);
                var rf = (refImg.Pixels[(y * W) + x] & 0xFFFFFFu);

                if (mine != rf) {
                    m_output.WriteLine($"MIS y={y,3} x={x,3} mine={Shade(mine)} ref={Shade(rf)}");
                    listed += 1;
                }
            }
        }

        // Side-by-side for the first few mismatching rows.
        var shown = 0;

        for (var y = 0; (y < H) && (shown < 12); y += 1) {
            var mismatch = false;

            for (var x = 0; x < W; x += 1) {
                if ((fb[(y * W) + x] & 0xFFFFFFu) != (refImg.Pixels[(y * W) + x] & 0xFFFFFFu)) {
                    mismatch = true;

                    break;
                }
            }

            if (!mismatch) {
                continue;
            }

            shown += 1;

            var mineRow = new StringBuilder();
            var refRow = new StringBuilder();
            var diffRow = new StringBuilder();

            for (var x = 0; x < W; x += 1) {
                var mine = (fb[(y * W) + x] & 0xFFFFFFu);
                var rf = (refImg.Pixels[(y * W) + x] & 0xFFFFFFu);

                mineRow.Append(Shade(mine));
                refRow.Append(Shade(rf));
                diffRow.Append(mine == rf ? ' ' : '^');
            }

            m_output.WriteLine($"--- row {y} ---");
            m_output.WriteLine("mine: " + mineRow);
            m_output.WriteLine("ref : " + refRow);
            m_output.WriteLine("diff: " + diffRow);
        }
    }
}
