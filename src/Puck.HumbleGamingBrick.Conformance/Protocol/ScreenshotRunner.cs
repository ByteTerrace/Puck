using Puck.HumbleGamingBrick.Conformance.Imaging;

namespace Puck.HumbleGamingBrick.Conformance.Protocol;

/// <summary>Runs a ROM whose result is a rendered image and compares the framebuffer to a reference PNG. Frame
/// blending is disabled so the latched frame is exactly what the test drew; for CGB the color path is set to the raw
/// 5→8-bit expansion the reference images use. A test that ends with <c>LD B,B</c> (mealybug/acid2) is captured at
/// that point; a continuously-rendering test (scribbltests) is captured after a fixed number of frames.</summary>
internal static class ScreenshotRunner {
    private const int ScreenWidth = 160;
    private const int ScreenHeight = 144;

    public static TestOutcome Run(RomCase romCase, Sm83Machine machine) {
        if ((romCase.ReferenceImagePath is null) || !File.Exists(path: romCase.ReferenceImagePath)) {
            return new(Case: romCase, Status: TestStatus.Inconclusive, Detail: "no reference image");
        }

        PngImage reference;

        try {
            reference = PngDecoder.Decode(path: romCase.ReferenceImagePath);
        }
        catch (InvalidDataException exception) {
            return new(Case: romCase, Status: TestStatus.Inconclusive, Detail: "reference decode failed: " + exception.Message);
        }

        if ((reference.Width != ScreenWidth) || (reference.Height != ScreenHeight)) {
            return new(Case: romCase, Status: TestStatus.Inconclusive, Detail: FormattableString.Invariant($"unexpected reference size {reference.Width}x{reference.Height}"));
        }

        var ppu = machine.Ppu;

        ppu.FrameBlendingEnabled = false;

        // The reference PNGs use the raw 5→8-bit channel expansion ((c<<3)|(c>>2)); match it for CGB comparisons.
        if ((romCase.Model == ConsoleModel.Cgb) && (ppu is Ppu colorPpu)) {
            colorPpu.ColorCorrection = CgbColorCorrection.Disabled;
        }

        var rendered = romCase.FrameLimit > 0
            ? RunFixedFrames(romCase: romCase, machine: machine)
            : RunToBreakpoint(romCase: romCase, machine: machine);

        if (!rendered) {
            return new(Case: romCase, Status: TestStatus.Inconclusive, Detail: "no frame rendered within cycle cap");
        }

        return Compare(romCase: romCase, frame: ppu.Framebuffer, reference: reference);
    }

    private static bool RunFixedFrames(RomCase romCase, Sm83Machine machine) {
        machine.Run(cycles: (ulong)(romCase.FrameLimit * RomCatalog.CyclesPerFrame));

        return true;
    }

    private static bool RunToBreakpoint(RomCase romCase, Sm83Machine machine) {
        var bus = machine.Bus;
        var cpu = machine.Cpu;
        var cap = (ulong)romCase.CycleLimit;
        var instructions = 0L;
        var framesSeen = 0;

        _ = machine.Ppu.ConsumeFrameReady();

        while (bus.ElapsedDots < cap) {
            if ((instructions > 16L) && (bus.ReadByte(address: cpu.ProgramCounter) == 0x40)) {
                break;
            }

            machine.Step();
            instructions += 1L;

            if (machine.Ppu.ConsumeFrameReady()) {
                framesSeen += 1;
            }
        }

        return (framesSeen > 0);
    }

    private static TestOutcome Compare(RomCase romCase, ReadOnlySpan<uint> frame, PngImage reference) {
        var mismatches = 0;
        var firstX = -1;
        var firstY = -1;

        for (var i = 0; i < reference.Pixels.Length; i += 1) {
            var actual = (frame[i] & 0x00FFFFFFu);
            var expected = (reference.Pixels[i] & 0x00FFFFFFu);

            if (actual != expected) {
                if (mismatches == 0) {
                    firstX = (i % ScreenWidth);
                    firstY = (i / ScreenWidth);
                }

                mismatches += 1;
            }
        }

        if (mismatches == 0) {
            return new(Case: romCase, Status: TestStatus.Pass, Detail: "pixel-exact match");
        }

        return new(
            Case: romCase,
            Status: TestStatus.Fail,
            Detail: FormattableString.Invariant($"{mismatches}/{reference.Pixels.Length} pixels differ; first at ({firstX},{firstY})")
        );
    }
}
