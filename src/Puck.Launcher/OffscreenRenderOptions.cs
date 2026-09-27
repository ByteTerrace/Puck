namespace Puck.Launcher;

/// <summary>The target extent <see cref="OffscreenTickHostedService"/> produces frames at. There is no window to query a
/// live size from, so a composition root supplies it at registration, and a host verb may resize it between frames: the
/// next produced frame asks its render root for the new extent.</summary>
public sealed class OffscreenRenderOptions {
    // The width in the high half and the height in the low half, so a frame reads both from one write.
    private ulong m_extent;

    /// <summary>Initializes a new instance of the <see cref="OffscreenRenderOptions"/> class.</summary>
    /// <param name="width">The render target width in pixels.</param>
    /// <param name="height">The render target height in pixels.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="width"/> or <paramref name="height"/> is
    /// zero.</exception>
    public OffscreenRenderOptions(uint width, uint height) => Resize(
        height: height,
        width: width
    );

    /// <summary>Gets the render target extent, width and height as one resize wrote them.</summary>
    public (uint Width, uint Height) Extent {
        get {
            var extent = Volatile.Read(location: ref m_extent);

            return (((uint)(extent >> 32)), ((uint)extent));
        }
    }

    /// <summary>Sets the render target extent the next produced frame asks for.</summary>
    /// <param name="width">The width in pixels.</param>
    /// <param name="height">The height in pixels.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="width"/> or <paramref name="height"/> is
    /// zero.</exception>
    public void Resize(uint width, uint height) {
        ArgumentOutOfRangeException.ThrowIfZero(value: width);
        ArgumentOutOfRangeException.ThrowIfZero(value: height);
        Volatile.Write(
            location: ref m_extent,
            value: (((ulong)width) << 32) | height
        );
    }
}
