namespace Puck.Platform;

/// <summary>
/// The async→sync bridge for a push-source camera: a grabber thread publishes the newest frame; the render-thread
/// puller reads the most recent one (latest-frame-wins, stale frames dropped). A lock-based double-copy — correct and
/// simple; the lock-free triple buffer the plan calls for is a later optimization. All threading a camera introduces is
/// confined here, so the pull seam stays single-threaded.
/// </summary>
internal sealed class LatestFrameBuffer {
    private readonly object m_gate = new();
    private byte[] m_frame = [];
    private bool m_hasFrame;
    private int m_height;
    private int m_width;

    /// <summary>Publishes a newly captured frame (called from the grabber thread).</summary>
    /// <param name="pixels">The frame's tightly packed pixels.</param>
    /// <param name="width">The frame width in pixels.</param>
    /// <param name="height">The frame height in pixels.</param>
    public void Publish(ReadOnlySpan<byte> pixels, int width, int height) {
        lock (m_gate) {
            if (m_frame.Length != pixels.Length) {
                m_frame = new byte[pixels.Length];
            }

            pixels.CopyTo(destination: m_frame);
            m_height = height;
            m_width = width;
            m_hasFrame = true;
        }
    }

    /// <summary>Copies the most recent frame into <paramref name="destination"/> (called from the puller), growing it if
    /// needed. Returns <see langword="false"/> until the first frame has arrived.</summary>
    /// <param name="destination">The reused destination buffer; replaced with a larger array if the frame grew.</param>
    /// <param name="width">The frame width in pixels.</param>
    /// <param name="height">The frame height in pixels.</param>
    /// <returns><see langword="true"/> if a frame was copied out.</returns>
    public bool TryGetLatest(ref byte[] destination, out int width, out int height) {
        lock (m_gate) {
            if (!m_hasFrame) {
                height = 0;
                width = 0;

                return false;
            }

            if (destination.Length != m_frame.Length) {
                destination = new byte[m_frame.Length];
            }

            m_frame.CopyTo(array: destination, index: 0);
            height = m_height;
            width = m_width;

            return true;
        }
    }
}
