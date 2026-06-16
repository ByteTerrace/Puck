using System.Diagnostics.CodeAnalysis;

namespace Puck.Platform;

public interface INativeImageCaptureService {
    bool IsSupported { get; }

    bool TryCreateScreenCapture(int width, int height, [NotNullWhen(true)] out INativeImageCaptureSession? session);
    bool TryCreateWindowCapture(string windowTitleFragment, int width, int height, [NotNullWhen(true)] out INativeImageCaptureSession? session);
}
