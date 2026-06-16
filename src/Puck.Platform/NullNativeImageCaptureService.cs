using System.Diagnostics.CodeAnalysis;

namespace Puck.Platform;

public sealed class NullNativeImageCaptureService : INativeImageCaptureService {
    public bool IsSupported => false;

    public bool TryCreateScreenCapture(int width, int height, [NotNullWhen(true)] out INativeImageCaptureSession? session) {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value: width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value: height);

        session = null;
        return false;
    }
    public bool TryCreateWindowCapture(string windowTitleFragment, int width, int height, [NotNullWhen(true)] out INativeImageCaptureSession? session) {
        ArgumentException.ThrowIfNullOrWhiteSpace(argument: windowTitleFragment);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value: width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value: height);

        session = null;
        return false;
    }
}
