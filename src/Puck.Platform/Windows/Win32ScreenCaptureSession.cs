using System.Diagnostics.CodeAnalysis;
using Puck.Platform.Windows.Interop;

namespace Puck.Platform.Windows;

public sealed class Win32ScreenCaptureSession : INativeImageCaptureSession {
    public static bool TryCreate(int width, int height, [NotNullWhen(true)] out Win32ScreenCaptureSession? session) {
        session = null;

        var screenDeviceContext = User32.GetDC(windowHandle: 0);

        if (screenDeviceContext == 0) {
            return false;
        }

        if (!Win32ImageScaler.TryCreate(
            height: height,
            scaler: out var scaler,
            width: width
        )) {
            _ = User32.ReleaseDC(
                deviceContextHandle: screenDeviceContext,
                windowHandle: 0
            );
            return false;
        }

        session = new Win32ScreenCaptureSession(
            scaler: scaler,
            screenDeviceContext: screenDeviceContext
        );
        return true;
    }

    private readonly Win32ImageScaler m_scaler;
    private nint m_screenDeviceContext;

    public int Height => m_scaler.Height;
    public ReadOnlySpan<byte> Pixels => m_scaler.Pixels;
    public int Width => m_scaler.Width;

    private Win32ScreenCaptureSession(Win32ImageScaler scaler, nint screenDeviceContext) {
        m_scaler = scaler;
        m_screenDeviceContext = screenDeviceContext;
    }

    public bool TryCapture() {
        var screenWidth = User32.GetSystemMetrics(index: Win32Constants.SmCxScreen);
        var screenHeight = User32.GetSystemMetrics(index: Win32Constants.SmCyScreen);

        if (
            (screenWidth <= 0) ||
            (screenHeight <= 0)
        ) {
            return false;
        }

        return m_scaler.ScaleFrom(
            captureLayeredWindows: true,
            sourceDeviceContext: m_screenDeviceContext,
            sourceHeight: screenHeight,
            sourceWidth: screenWidth
        );
    }
    public void Dispose() {
        m_scaler.Dispose();
        if (m_screenDeviceContext != 0) {
            _ = User32.ReleaseDC(
                deviceContextHandle: m_screenDeviceContext,
                windowHandle: 0
            );
            m_screenDeviceContext = 0;
        }
    }
}
