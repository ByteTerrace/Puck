using Puck.HumbleGamingBrick.Interfaces;

namespace Puck.HumbleGamingBrick;

/// <summary>
/// The default <see cref="ICameraSensor"/>: a fixed, deterministic gradient — brightness rising left-to-right with a
/// gentle top-to-bottom shade — so a camera cartridge always has a stable, non-random readout even when no host
/// camera is attached. It is pure position arithmetic (no state, no time, no randomness), which makes every capture over
/// it bit-reproducible; a host that wants live video registers its own sensor in place of this one.
/// </summary>
public sealed class GradientCameraSensor : ICameraSensor {
    /// <inheritdoc/>
    public void Read(Span<byte> destination) {
        for (var y = 0; (y < SensorImage.Height); ++y) {
            // A horizontal ramp 0..252 with a small per-row bias, so the plane reads as a recognizable image (a bright
            // right edge, a faint vertical banding) rather than a flat field — useful as a stand-in viewfinder.
            var rowBias = ((y * 24) / SensorImage.Height);
            var rowStart = (y * SensorImage.Width);

            for (var x = 0; (x < SensorImage.Width); ++x) {
                var value = (((x * 252) / (SensorImage.Width - 1)) + rowBias);

                destination[(rowStart + x)] = (byte)((value > 255) ? 255 : value);
            }
        }
    }
}
