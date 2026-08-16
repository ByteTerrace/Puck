namespace Puck.Assets;

/// <summary>One decoded animation frame.</summary>
/// <param name="RgbaPixels">The pixels, tightly packed 8-bit RGBA, row-major, 4 bytes (R, G, B, A) each, no row padding.</param>
/// <param name="DelayNumerator">The frame delay numerator, in <paramref name="DelayDenominator"/>ths of a second.</param>
/// <param name="DelayDenominator">The frame delay denominator; 0 reads as 100 per the APNG specification.</param>
public readonly record struct PngAnimationFrame(byte[] RgbaPixels, ushort DelayNumerator, ushort DelayDenominator);
/// <summary>A decoded APNG animation: full-size frames over one canvas.</summary>
/// <param name="Frames">The frames, in play order.</param>
/// <param name="Width">The canvas width in pixels.</param>
/// <param name="Height">The canvas height in pixels.</param>
/// <param name="PlayCount">How many times the animation loops; 0 loops forever.</param>
public readonly record struct PngAnimation(IReadOnlyList<PngAnimationFrame> Frames, int Width, int Height, uint PlayCount);
