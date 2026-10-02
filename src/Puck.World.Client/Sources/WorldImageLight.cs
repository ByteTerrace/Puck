using System.Buffers.Binary;
using System.Numerics;
using Puck.Abstractions.Presentation;
using Puck.Abstractions.Sources;

namespace Puck.World.Client;

/// <summary>The room glow a screen's image casts: the average color of a B8G8R8A8 frame, or of a half-float scRGB one,
/// sampled every sixteenth pixel so the per-frame cost stays trivial.</summary>
public static class WorldImageLight {
    /// <summary>Returns the average color of a B8G8R8A8 frame, normalized to 0–1, sampling every sixteenth pixel.</summary>
    /// <param name="bgra">The frame's tightly packed pixels: blue, green, red, alpha.</param>
    /// <returns>The average red, green and blue; zero for an empty frame.</returns>
    public static Vector3 Average(ReadOnlySpan<byte> bgra) {
        const int Stride = (16 * 4);

        var sumRed = 0L;
        var sumGreen = 0L;
        var sumBlue = 0L;
        var samples = 0;

        for (var offset = 0; ((offset + 2) < bgra.Length); offset += Stride) {
            sumBlue += bgra[(offset + 0)];
            sumGreen += bgra[(offset + 1)];
            sumRed += bgra[(offset + 2)];
            samples++;
        }

        if (samples == 0) {
            return Vector3.Zero;
        }

        var scale = (1f / (255f * samples));

        return new Vector3(
            x: (sumRed * scale),
            y: (sumGreen * scale),
            z: (sumBlue * scale)
        );
    }
    /// <summary>Returns the average color of a half-float RGBA frame in scRGB, the frame a capture of an HDR display hands
    /// over, as working values: every sixteenth pixel's linear light averaged, then converted at the paper-white level
    /// (<see cref="ImageSourceConversion.ToWorking(ImageColorEncoding, double, double, double, double)"/>), so a
    /// highlight above SDR white casts more than one.</summary>
    /// <param name="rgba">The frame's tightly packed pixels: red, green, blue and alpha, each a little-endian half
    /// float.</param>
    /// <param name="paperWhiteNits">The paper-white level, in cd/m².</param>
    /// <returns>The average red, green and blue; zero for an empty frame.</returns>
    public static Vector3 AverageScRgb(ReadOnlySpan<byte> rgba, double paperWhiteNits) {
        const int Stride = (16 * 8);

        var sumRed = 0.0;
        var sumGreen = 0.0;
        var sumBlue = 0.0;
        var samples = 0;

        for (var offset = 0; ((offset + 6) <= rgba.Length); offset += Stride) {
            sumRed += ((double)BinaryPrimitives.ReadHalfLittleEndian(source: rgba[offset..]));
            sumGreen += ((double)BinaryPrimitives.ReadHalfLittleEndian(source: rgba[(offset + 2)..]));
            sumBlue += ((double)BinaryPrimitives.ReadHalfLittleEndian(source: rgba[(offset + 4)..]));
            samples++;
        }

        if (samples == 0) {
            return Vector3.Zero;
        }

        var (red, green, blue) = ImageSourceConversion.ToWorking(
            b: (sumBlue / samples),
            color: ImageColorEncoding.Of(colorSpace: DisplayColorSpace.ScRgb),
            g: (sumGreen / samples),
            paperWhiteNits: paperWhiteNits,
            r: (sumRed / samples)
        );

        return new Vector3(
            x: ((float)red),
            y: ((float)green),
            z: ((float)blue)
        );
    }
    /// <summary>Returns the light a packed RGBA8 fill color casts, normalized to 0–1.</summary>
    /// <param name="rgba">The packed color, red in the low byte.</param>
    /// <returns>The red, green and blue.</returns>
    public static Vector3 OfFill(uint rgba) => new(
        x: ((rgba & 0xFFU) / 255f),
        y: (((rgba >> 8) & 0xFFU) / 255f),
        z: (((rgba >> 16) & 0xFFU) / 255f)
    );
}
