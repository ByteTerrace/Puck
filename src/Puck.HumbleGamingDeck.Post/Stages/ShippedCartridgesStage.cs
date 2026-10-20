using Puck.Machines;

namespace Puck.HumbleGamingDeck.Post;

/// <summary>
/// Runs the test cartridges the repository ships (<see cref="DeckTestCartridges"/>) and reads their pictures from the
/// PPU's pixel codes: the colour bars show their eight colours in order and take the emphasis bits A holds, and the
/// moving sprite's ball travels one pixel a frame on each axis and turns when the control pad steers it. A World screen
/// shows the same pictures; this stage sees them without a GPU.
/// </summary>
internal sealed class ShippedCartridgesStage : IPostStage<PostContext> {
    private const ushort BallColour = 0x27;
    private const ulong FrameTicks = 357_366UL;

    /// <inheritdoc/>
    public string Name => "shipped-cartridges";
    /// <inheritdoc/>
    public PostTier Tier => PostTier.A;

    /// <inheritdoc/>
    public PostStageOutcome Run(PostContext context) {
        if (ColourBars() is { } bars) {
            return PostStageOutcome.Fail(detail: $"colour-bars: {bars}");
        }
        if (MovingSprite() is { } sprite) {
            return PostStageOutcome.Fail(detail: $"moving-sprite: {sprite}");
        }

        return PostStageOutcome.Pass(detail: "colour-bars shows its eight bars in their hues and A's blue emphasis; moving-sprite's ball moves one pixel a frame and turns left on the pad");
    }

    private static string? ColourBars() {
        using var instance = Boot(image: DeckTestCartridges.ColourBars());
        var machine = instance.Machine;
        ReadOnlySpan<ushort> bars = [0x30, 0x28, 0x2C, 0x2A, 0x24, 0x16, 0x12, 0x0F];

        machine.RunCycles(masterTicks: (8 * FrameTicks));
        for (var bar = 0; (bar < bars.Length); ++bar) {
            var code = machine.Ppu.Frame[((120 * HgdPpu.Width) + ((bar * 32) + 16))];

            if (code != bars[bar]) {
                return $"bar {bar} shows code ${code:X3}, expected ${bars[bar]:X3}";
            }
            if (BarHue(colour: HgdPalette.Colours[code], bar: bar) is { } hue) {
                return $"bar {bar}'s code ${code:X3} presents as #{HgdPalette.Colours[code]:X6}, {hue}";
            }
        }

        machine.Controllers.SetButtons(
            buttons: HgdButtons.A,
            port: 0
        );
        machine.RunCycles(masterTicks: (3 * FrameTicks));

        var tinted = machine.Ppu.Frame[((120 * HgdPpu.Width) + 16)];

        return ((tinted == (0x30 | 0x100))
            ? null
            : $"with A held the first bar shows code ${tinted:X3}, expected $130 (blue emphasis on white)"
        );
    }
    // Whether a presented bar colour has its bar's hue: which channels lead and which trail. The palette decodes the
    // composite signal against the colour burst, so a burst reference off by a phase turns every hue.
    private static string? BarHue(uint colour, int bar) {
        var red = ((int)((colour >> 16) & 0xFF));
        var green = ((int)((colour >> 8) & 0xFF));
        var blue = ((int)(colour & 0xFF));

        return bar switch {
            0 => (((Math.Min(val1: Math.Min(val1: red, val2: green), val2: blue) > 0xC0)) ? null : "expected white"),
            1 => (((red > blue) && (green > blue)) ? null : "expected yellow (blue trailing)"),
            2 => (((green > red) && (blue > red)) ? null : "expected cyan (red trailing)"),
            3 => (((green > red) && (green > blue)) ? null : "expected green (green leading)"),
            4 => (((red > green) && (blue > green)) ? null : "expected magenta (green trailing)"),
            5 => (((red > green) && (red > blue)) ? null : "expected red (red leading)"),
            6 => (((blue > red) && (blue > green)) ? null : "expected blue (blue leading)"),
            _ => (((Math.Max(val1: Math.Max(val1: red, val2: green), val2: blue) < 0x20)) ? null : "expected black"),
        };
    }
    private static string? MovingSprite() {
        using var instance = Boot(image: DeckTestCartridges.MovingSprite());
        var machine = instance.Machine;

        machine.RunCycles(masterTicks: (8 * FrameTicks));
        if (Ball(frame: machine.Ppu.Frame) is not { } start) {
            return "no ball on the picture after 8 frames";
        }

        machine.RunCycles(masterTicks: (10 * FrameTicks));
        if (Ball(frame: machine.Ppu.Frame) is not { } drifted) {
            return "the ball left the picture";
        }
        if ((drifted.X != (start.X + 10)) || (drifted.Y != (start.Y + 10))) {
            return $"the ball drifted from {start} to {drifted} in 10 frames, expected 10 pixels right and down";
        }

        machine.Controllers.SetButtons(
            buttons: HgdButtons.Left,
            port: 0
        );
        machine.RunCycles(masterTicks: (10 * FrameTicks));

        if (Ball(frame: machine.Ppu.Frame) is not { } steered) {
            return "the ball left the picture while steered";
        }

        return ((steered.X < drifted.X)
            ? null
            : $"with Left held the ball moved from {drifted} to {steered}, not left"
        );
    }
    private static MachineInstance<HgdMachine, HgdMachineConfiguration> Boot(byte[] image) =>
        HgdMachineFactory.Create(configuration: new HgdMachineConfiguration(cartridge: HgdCartridge.Load(image: image)));
    // The ball's top-left pixel: the first pixel, in raster order, showing its colour.
    private static (int X, int Y)? Ball(ReadOnlySpan<ushort> frame) {
        var index = frame.IndexOf(value: BallColour);

        return ((index < 0)
            ? null
            : ((index % HgdPpu.Width), (index / HgdPpu.Width))
        );
    }
}
