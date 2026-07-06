namespace Puck.Post;

/// <summary>
/// CPU oracle for the local <c>gradient.comp</c> kernel (<c>Output[x,y] = float4(x/width, y/height, 0.5, 1.0)</c>).
/// Every pixel must equal that exact output within 1 LSB — the only slack is the float→UNORM8 round-mode noise — so a
/// kernel that corrupts the interior while leaving the corners monotonic (which a first-vs-last-red endpoint
/// check waves through) is caught. Shared by B1 (compute, same-device) and C3 (reverse-share, cross-API written
/// into a foreign-owned image), so the two stages assert the identical pattern.
/// </summary>
internal static class GradientCheck {
    // The blue channel is a constant 0.5 → round(127.5) lands on 127 or 128 depending on the driver's UNORM round
    // mode; allow either. Red/green use ±1 to absorb the same rounding at their per-pixel gradient values.
    private const int BlueLow = 127;
    private const int BlueHigh = 128;

    /// <summary>Returns <see langword="null"/> when every pixel matches the gradient within 1 LSB, else a description of
    /// the first mismatching pixel (channel, coordinate, actual vs expected).</summary>
    /// <param name="pixels">The R8G8B8A8 readback, row-major, 4 bytes per pixel.</param>
    /// <param name="width">The image width in pixels.</param>
    /// <param name="height">The image height in pixels.</param>
    public static string? FirstMismatch(ReadOnlySpan<byte> pixels, uint width, uint height) {
        for (var y = 0u; (y < height); y++) {
            for (var x = 0u; (x < width); x++) {
                var offset = (int)((((y * width) + x) * 4u));
                var expectedR = (int)Math.Round(x / (double)width * 255.0);
                var expectedG = (int)Math.Round(y / (double)height * 255.0);
                int r = pixels[offset];
                int g = pixels[offset + 1];
                int b = pixels[offset + 2];
                int a = pixels[offset + 3];

                if (Math.Abs(r - expectedR) > 1) {
                    return $"R at ({x},{y}) = {r}, expected {expectedR}±1 (x/width ramp)";
                }

                if (Math.Abs(g - expectedG) > 1) {
                    return $"G at ({x},{y}) = {g}, expected {expectedG}±1 (y/height ramp)";
                }

                if ((b < BlueLow) || (b > BlueHigh)) {
                    return $"B at ({x},{y}) = {b}, expected {BlueLow}..{BlueHigh} (constant 0.5)";
                }

                if (a != 255) {
                    return $"A at ({x},{y}) = {a}, expected 255";
                }
            }
        }

        return null;
    }
}
