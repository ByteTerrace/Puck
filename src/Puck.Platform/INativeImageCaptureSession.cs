namespace Puck.Platform;

public interface INativeImageCaptureSession : IDisposable {
    int Height { get; }
    ReadOnlySpan<byte> Pixels { get; }
    int Width { get; }

    bool TryCapture();
}
