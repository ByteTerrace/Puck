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

        return PostStageOutcome.Pass(detail: "colour-bars shows its eight bars and A's blue emphasis; moving-sprite's ball moves one pixel a frame and turns left on the pad");
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
