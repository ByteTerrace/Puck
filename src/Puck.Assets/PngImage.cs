namespace Puck.Assets;

/// <summary>A decoded PNG image: tightly packed 8-bit RGBA pixels, row-major, 4 bytes (R, G, B, A) each, no row padding.</summary>
/// <param name="RgbaPixels">The pixels, exactly <c>Width * Height * 4</c> bytes.</param>
/// <param name="Width">The image width in pixels.</param>
/// <param name="Height">The image height in pixels.</param>
public readonly record struct PngImage(byte[] RgbaPixels, int Width, int Height);
