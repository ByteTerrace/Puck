using System.Diagnostics.CodeAnalysis;

namespace Puck.Platform.Windows;

public sealed class Win32NativeImageCaptureService : INativeImageCaptureService {
    public bool IsSupported => true;

    public bool TryCreateScreenCapture(int width, int height, [NotNullWhen(true)] out INativeImageCaptureSession? session) {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value: width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value: height);

        if (Win32ScreenCaptureSession.TryCreate(
            height: height,
            session: out var screenCaptureSession,
            width: width
        )) {
            session = screenCaptureSession;
            return true;
        }

        session = null;
        return false;
    }
    public bool TryCreateWindowCapture(string windowTitleFragment, int width, int height, [NotNullWhen(true)] out INativeImageCaptureSession? session) {
        ArgumentException.ThrowIfNullOrWhiteSpace(argument: windowTitleFragment);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value: width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value: height);

        if (Win32WindowCaptureSession.TryCreate(
            height: height,
            session: out var windowCaptureSession,
            width: width,
            windowTitleFragment: windowTitleFragment
        )) {
            session = windowCaptureSession;
            return true;
        }

        session = null;
        return false;
    }
}
