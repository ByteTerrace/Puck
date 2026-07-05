namespace Puck.HumbleGamingBrick.Interfaces;

/// <summary>
/// The analog readout of the Pocket Camera's Mitsubishi M64282FP image sensor — the one seam through which the outside
/// world (a host webcam, a still image, a deterministic test pattern) reaches the emulated machine. It is a
/// <b>host input</b>, exactly like the joypad: the cartridge <see cref="Read"/>s it synchronously at the instant the
/// ROM triggers a capture, latching whatever the source currently holds, and everything downstream — the sensor's
/// gain/exposure/edge/dither processing and the tiled image it deposits in save RAM — is pure integer arithmetic over
/// that latched plane. So a live camera is the only non-deterministic ingredient, quarantined to the capture instant;
/// a fixed <see cref="Read"/> makes the whole capture bit-reproducible (the POST battery relies on exactly that).
/// </summary>
public interface ICameraSensor {
    /// <summary>Fills <paramref name="destination"/> with the sensor's current readout: one byte per photosite, row by
    /// row, <c>0</c> = darkest and <c>255</c> = brightest, laid out as <see cref="SensorImage.Width"/> columns by
    /// <see cref="SensorImage.Height"/> rows (<see cref="SensorImage.PixelCount"/> bytes). Called once per capture; the
    /// implementation must fill every byte (it is not pre-cleared).</summary>
    /// <param name="destination">The plane to fill; at least <see cref="SensorImage.PixelCount"/> bytes.</param>
    void Read(Span<byte> destination);
}
