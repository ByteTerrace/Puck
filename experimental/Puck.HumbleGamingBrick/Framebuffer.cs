using System.Runtime.InteropServices;
using Puck.HumbleGamingBrick.Interfaces;

namespace Puck.HumbleGamingBrick;

/// <summary>
/// The PPU's 160×144 pixel output buffer. The PPU writes one pixel per drawn dot through <see cref="SetPixel"/>; hosts
/// and the conformance oracle read it through <see cref="IFramebuffer"/>. It is snapshot state — a mid-frame capture
/// restores the partially drawn image exactly — serialized as the raw little-endian pixel bytes.
/// </summary>
public sealed class Framebuffer : IFramebuffer, ISnapshotable {
    /// <summary>The Game Boy screen width in pixels.</summary>
    public const int ScreenWidth = 160;
    /// <summary>The Game Boy screen height in pixels.</summary>
    public const int ScreenHeight = 144;

    private readonly uint[] m_pixels;

    /// <summary>Creates a black framebuffer.</summary>
    public Framebuffer() =>
        m_pixels = new uint[ScreenWidth * ScreenHeight];

    /// <inheritdoc/>
    public int Width =>
        ScreenWidth;
    /// <inheritdoc/>
    public int Height =>
        ScreenHeight;
    /// <inheritdoc/>
    public ReadOnlySpan<uint> Pixels =>
        m_pixels;

    /// <summary>Writes one pixel (packed <c>0x00RRGGBB</c>) at a screen coordinate.</summary>
    /// <param name="x">The column, <c>[0, 160)</c>.</param>
    /// <param name="y">The row, <c>[0, 144)</c>.</param>
    /// <param name="color">The packed color.</param>
    public void SetPixel(int x, int y, uint color) =>
        m_pixels[(y * ScreenWidth) + x] = color;
    /// <inheritdoc/>
    public void SaveState(StateWriter writer) =>
        writer.WriteBytes(value: MemoryMarshal.AsBytes(span: m_pixels.AsSpan()));
    /// <inheritdoc/>
    public void LoadState(StateReader reader) =>
        reader.ReadBytes(destination: MemoryMarshal.AsBytes(span: m_pixels.AsSpan()));
}
