namespace Puck.Abstractions.Windowing;

/// <summary>A rectangle of the desktop, in desktop pixels.</summary>
/// <param name="X">The left edge.</param>
/// <param name="Y">The top edge.</param>
/// <param name="Width">The width.</param>
/// <param name="Height">The height.</param>
public readonly record struct NativeDesktopArea(int X, int Y, int Width, int Height);
