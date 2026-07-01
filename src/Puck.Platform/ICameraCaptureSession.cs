namespace Puck.Platform;

/// <summary>
/// A live camera capture session: a backend-neutral <see cref="IFrameCaptureSource"/> whose frames arrive
/// asynchronously on an internal grabber thread and are handed to the (single-threaded) puller as the most recent
/// frame — latest-frame-wins, stale frames dropped, the puller never blocks. This confines all threading to the
/// implementation and preserves the single-threaded pull contract for everything downstream.
/// <see cref="IFrameCaptureSource.TryCapture"/> returns the newest frame as a CPU-pixel <see cref="Surface"/> in
/// <see cref="SurfaceFormat.B8G8R8A8Unorm"/>, or <see langword="false"/> until the first frame arrives.
/// </summary>
public interface ICameraCaptureSession : IFrameCaptureSource, IDisposable {
    /// <summary>A monotonically increasing counter of frames the device has delivered — a puller compares it against the
    /// value it last processed to skip re-uploading an unchanged frame (the newest-frame-wins drop policy), so the render
    /// pump is never blocked re-doing work between the camera's own (e.g. 30 fps) arrivals.</summary>
    long FrameVersion { get; }
    /// <summary>Whether the feed has permanently stopped (device unplugged, end of stream, or a mid-stream error) — the
    /// consumer's signal to dispose this session and re-open the device.</summary>
    bool IsEnded { get; }
    /// <summary>The <see cref="System.Diagnostics.Stopwatch"/> timestamp of the most recent frame's arrival (stamped on
    /// the grabber thread at publish, so it shares the render pacer's clock domain) — the genlock arrival signal.</summary>
    long LastFrameTimestamp { get; }
    /// <summary>The negotiated frame height in pixels.</summary>
    int Height { get; }
    /// <summary>A human-readable device name, for diagnostics.</summary>
    string Name { get; }
    /// <summary>The negotiated frame width in pixels.</summary>
    int Width { get; }
}
