namespace Puck.Platform;

/// <summary>The native transport format a camera graph negotiated before any platform conversion into Puck's
/// presentation surface.</summary>
/// <param name="Subtype">The native media subtype (for example <c>YUY2</c>, <c>MJPG</c>, or <c>L8</c>).</param>
/// <param name="RateHz">The native transport cadence in frames per second, or zero when the driver did not report it.</param>
/// <param name="Mode">The named coordinated capture mode, or <see langword="null"/> for an ordinary single-stream
/// graph.</param>
public readonly record struct CameraCaptureFormat(string Subtype, double RateHz, string? Mode = null);

/// <summary>Optional camera-session diagnostics: exposes what the physical graph negotiated, independently of the
/// BGRA surface shape Puck presents to consumers.</summary>
public interface ICameraCaptureDiagnostics {
    /// <summary>Gets the native transport subtype/rate and any named coordinated capture mode.</summary>
    CameraCaptureFormat CaptureFormat { get; }
}
