namespace Puck.Abstractions;

/// <summary>
/// The egress end of the capture pipeline: it consumes captured frames (encoding them, hashing them, or
/// forwarding them) and owns whatever resources that requires. Frames arrive as the CPU-pixel
/// <see cref="Surface"/> variant — the producing adapter has already crossed any device boundary.
/// </summary>
public interface ICaptureSink : IDisposable {
    /// <summary>Consumes one captured frame.</summary>
    /// <param name="frame">The frame to consume; its <see cref="Surface"/> carries tightly packed CPU pixels.</param>
    void Consume(in CaptureFrame frame);
}
