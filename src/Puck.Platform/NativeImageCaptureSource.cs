namespace Puck.Platform;

/// <summary>
/// Adapts a native (OS) <see cref="INativeImageCaptureSession"/> — the GDI screen/window grab — to the
/// backend-neutral <see cref="IFrameCaptureSource"/> seam, emitting each grab as a CPU-pixel
/// <see cref="Surface"/> in <see cref="SurfaceFormat.B8G8R8A8Unorm"/>.
/// </summary>
/// <remarks>The emitted surface views a buffer reused across captures (copied out of the session each grab);
/// it is valid until the next <see cref="TryCapture"/>, so a caller that retains it must copy first. Not
/// thread-safe — drive from a single capture loop.</remarks>
public sealed class NativeImageCaptureSource : IFrameCaptureSource, IDisposable {
    private byte[] m_buffer = [];
    private readonly INativeImageCaptureSession m_session;

    /// <summary>Initializes a new instance of the <see cref="NativeImageCaptureSource"/> class.</summary>
    /// <param name="session">The native capture session to pull frames from. Owned by this source and disposed with it.</param>
    public NativeImageCaptureSource(INativeImageCaptureSession session) {
        ArgumentNullException.ThrowIfNull(session);

        m_session = session;
    }

    /// <inheritdoc/>
    public bool TryCapture(out Surface surface) {
        if (!m_session.TryCapture()) {
            surface = default;
            return false;
        }

        var pixels = m_session.Pixels;

        if (m_buffer.Length != pixels.Length) {
            m_buffer = new byte[pixels.Length];
        }

        pixels.CopyTo(destination: m_buffer);
        surface = new Surface(
            Format: SurfaceFormat.B8G8R8A8Unorm,
            Height: (uint)m_session.Height,
            ImageViewHandle: 0,
            Pixels: m_buffer,
            Width: (uint)m_session.Width
        );
        return true;
    }
    /// <inheritdoc/>
    public void Dispose() {
        m_session.Dispose();
    }
}
