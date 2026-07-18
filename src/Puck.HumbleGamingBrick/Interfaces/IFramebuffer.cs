namespace Puck.HumbleGamingBrick.Interfaces;

/// <summary>
/// The completed video output the PPU draws into: a 160×144 grid of packed <c>0x00RRGGBB</c> pixels a host (or the
/// conformance oracle) reads through the machine's container. The PPU resolves the concrete <see cref="Framebuffer"/> to
/// write it; consumers take this read-only view.
/// </summary>
public interface IFramebuffer {
    /// <summary>Gets the width in pixels (160).</summary>
    int Width { get; }
    /// <summary>Gets the height in pixels (144).</summary>
    int Height { get; }
    /// <summary>Gets the pixels in row-major order, each a packed <c>0x00RRGGBB</c> value; length is
    /// <see cref="Width"/> × <see cref="Height"/>.</summary>
    ReadOnlySpan<uint> Pixels { get; }
}
