using Puck.Abstractions;

namespace Puck.Platform;

/// <summary>
/// The async→sync bridge for a push-source camera: a grabber thread publishes the newest frame; the render-thread
/// puller copies the most recent one out (latest-frame-wins, stale frames dropped). Frames go through a
/// <see cref="PublishBuffer{T}"/> of pixel images filled in place, so a puller copying a frame never waits on the
/// grabber and the grabber never waits on a puller. All threading a camera introduces is confined here, so the pull
/// seam stays single-threaded.
/// </summary>
public sealed class LatestFrameBuffer {
    private readonly PublishBuffer<Frame> m_frames = new(create: static () => new Frame());

    /// <summary>Gets the <see cref="System.Diagnostics.Stopwatch"/> timestamp of the most recent <see cref="Publish"/>,
    /// stamped at the publish site (the grabber thread), so it shares the render pacer's clock domain; the genlock
    /// arrival signal. Zero before the first frame.</summary>
    public long LastTimestamp => m_frames.Read(read: static (in Frame frame, long version, int state) => frame.Timestamp, state: 0);
    /// <summary>Gets a monotonically increasing counter bumped on every <see cref="Publish"/>, which lets a puller detect
    /// whether a new frame has arrived (and skip re-processing an unchanged one) without copying the pixels out.</summary>
    public long Version => m_frames.Version;

    /// <summary>Publishes a newly captured frame (called from the grabber thread).</summary>
    /// <param name="pixels">The frame's tightly packed pixels.</param>
    /// <param name="width">The frame width in pixels.</param>
    /// <param name="height">The frame height in pixels.</param>
    public void Publish(ReadOnlySpan<byte> pixels, int width, int height) => m_frames.Publish(
        fill: static (ref Frame frame, scoped Captured captured) => {
            if (frame.Pixels.Length != captured.Pixels.Length) {
                frame.Pixels = new byte[captured.Pixels.Length];
            }

            captured.Pixels.CopyTo(destination: frame.Pixels);
            frame.Height = captured.Height;
            frame.Width = captured.Width;
            frame.Timestamp = System.Diagnostics.Stopwatch.GetTimestamp();
        },
        state: new Captured(height: height, pixels: pixels, width: width)
    );
    /// <summary>Copies the most recent frame into <paramref name="destination"/> (called from the puller), growing it if
    /// needed.</summary>
    /// <param name="destination">The reused destination buffer; replaced with an array of the frame's length if it
    /// differs.</param>
    /// <param name="width">The frame width in pixels.</param>
    /// <param name="height">The frame height in pixels.</param>
    /// <returns><see langword="true"/> if a frame was copied out; <see langword="false"/> until the first frame has
    /// arrived.</returns>
    public bool TryGetLatest(ref byte[] destination, out int width, out int height) {
        var result = m_frames.Read(state: destination, read: static (in Frame frame, long version, byte[] pixels) => {
            if (version == 0L) { return (Success: false, Pixels: pixels, Width: 0, Height: 0); }
            if (pixels.Length != frame.Pixels.Length) { pixels = new byte[frame.Pixels.Length]; }
            frame.Pixels.CopyTo(array: pixels, index: 0);
            return (Success: true, Pixels: pixels, Width: frame.Width, Height: frame.Height);
        });

        destination = result.Pixels;
        width = result.Width;
        height = result.Height;
        return result.Success;
    }

    private sealed class Frame {
        public byte[] Pixels = [];
        public int Height;
        public long Timestamp;
        public int Width;
    }
    private readonly ref struct Captured(int height, ReadOnlySpan<byte> pixels, int width) {
        public readonly int Height = height;
        public readonly ReadOnlySpan<byte> Pixels = pixels;
        public readonly int Width = width;
    }
}
