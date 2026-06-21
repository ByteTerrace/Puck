namespace Puck.Input.Output;

/// <summary>An 8-bit-per-channel RGB color for a controller's settable indicator (e.g. the DualSense light bar).</summary>
/// <param name="Red">The red channel, 0..255.</param>
/// <param name="Green">The green channel, 0..255.</param>
/// <param name="Blue">The blue channel, 0..255.</param>
public readonly record struct LedColor(byte Red, byte Green, byte Blue);
