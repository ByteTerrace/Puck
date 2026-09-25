namespace Puck.Abstractions.Gpu;

/// <summary>A rectangle of an attachment in whole pixels, with its origin at the top-left corner.</summary>
/// <param name="X">The left edge.</param>
/// <param name="Y">The top edge.</param>
/// <param name="Width">The width.</param>
/// <param name="Height">The height.</param>
public readonly record struct GpuPixelRect(int X, int Y, uint Width, uint Height) {
    /// <summary>Returns the rectangle covering a whole extent from its origin.</summary>
    /// <param name="width">The extent's width.</param>
    /// <param name="height">The extent's height.</param>
    /// <returns>The rectangle at the origin with that extent.</returns>
    public static GpuPixelRect Covering(uint width, uint height) => new(
        Height: height,
        Width: width,
        X: 0,
        Y: 0
    );
}
