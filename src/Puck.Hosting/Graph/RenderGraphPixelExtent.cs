namespace Puck.Hosting;

/// <summary>An exact authored output size. Reader footprints determine whether it is demanded, never its dimensions.</summary>
/// <param name="Width">The positive width in pixels.</param>
/// <param name="Height">The positive height in pixels.</param>
public readonly record struct RenderGraphPixelExtent(int Width, int Height);
