using System.Numerics;

namespace Puck.World.Client;

/// <summary>The room glow a screen's image casts: the average color of a B8G8R8A8 frame, sampled every sixteenth pixel so
/// the per-frame cost stays trivial.</summary>
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
    /// <summary>Returns the light a packed RGBA8 fill color casts, normalized to 0–1.</summary>
    /// <param name="rgba">The packed color, red in the low byte.</param>
    /// <returns>The red, green and blue.</returns>
    public static Vector3 OfFill(uint rgba) => new(
        x: ((rgba & 0xFFU) / 255f),
        y: (((rgba >> 8) & 0xFFU) / 255f),
        z: (((rgba >> 16) & 0xFFU) / 255f)
    );
}
