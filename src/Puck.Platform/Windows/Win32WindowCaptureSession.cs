using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using Puck.Platform.Windows.Interop;

namespace Puck.Platform.Windows;

public sealed class Win32WindowCaptureSession : INativeImageCaptureSession {
    public static bool TryCreate(string windowTitleFragment, int width, int height, [NotNullWhen(true)] out Win32WindowCaptureSession? session) {
        session = null;

        if (!Win32ImageScaler.TryCreate(
            height: height,
            scaler: out var scaler,
            width: width
        )) {
            return false;
        }

        session = new Win32WindowCaptureSession(
            scaler: scaler,
            windowTitleFragment: windowTitleFragment
        );
        return true;
    }

    private nint m_bitmapHandle;
    private nint m_memoryDeviceContext;
    private nint m_previousBitmapHandle;
    private readonly Win32ImageScaler m_scaler;
    private int m_sourceHeight;
    private int m_sourceWidth;
    private nint m_windowHandle;
    private readonly string m_windowTitleFragment;

    public int Height => m_scaler.Height;
    public ReadOnlySpan<byte> Pixels => m_scaler.Pixels;
    public int Width => m_scaler.Width;

    private Win32WindowCaptureSession(Win32ImageScaler scaler, string windowTitleFragment) {
        m_scaler = scaler;
        m_windowTitleFragment = windowTitleFragment;
    }

    public bool TryCapture() {
        return (
            TryBind() &&
            TryRender() &&
            m_scaler.ScaleFrom(
                captureLayeredWindows: false,
                sourceDeviceContext: m_memoryDeviceContext,
                sourceHeight: m_sourceHeight,
                sourceWidth: m_sourceWidth
            )
        );
    }
    public void Dispose() {
        ReleaseSurface();
        if (m_memoryDeviceContext != 0) {
            _ = Gdi32.DeleteDC(deviceContextHandle: m_memoryDeviceContext);
            m_memoryDeviceContext = 0;
        }

        m_scaler.Dispose();
        m_windowHandle = 0;
    }

    private bool TryBind() {
        if (
            (m_windowHandle != 0) &&
            User32.IsWindow(windowHandle: m_windowHandle)
        ) {
            return true;
        }

        m_windowHandle = 0;
        var match = (nint)0;

        _ = User32.EnumWindows(
            (candidate, _) => {
                if (!User32.IsWindowVisible(windowHandle: candidate)) {
                    return true;
                }

                var titleLength = User32.GetWindowTextLength(windowHandle: candidate);

                if (titleLength <= 0) {
                    return true;
                }

                var titleBuffer = new char[(titleLength + 1)];
                var copiedLength = User32.GetWindowText(
                    maxLength: titleBuffer.Length,
                    text: titleBuffer,
                    windowHandle: candidate
                );

                if (
                    (copiedLength <= 0) ||
                    !new string(
                        length: copiedLength,
                        startIndex: 0,
                        value: titleBuffer
                    ).Contains(
                        comparisonType: StringComparison.OrdinalIgnoreCase,
                        value: m_windowTitleFragment
                    )
                ) {
                    return true;
                }

                match = candidate;
                return false;
            },
            parameter: 0
        );
        m_windowHandle = match;
        return (match != 0);
    }
    private bool TryRender() {
        if (
            User32.IsIconic(windowHandle: m_windowHandle) ||
            !User32.GetWindowRect(
                rectangle: out var windowRect,
                windowHandle: m_windowHandle
            )
        ) {
            return false;
        }

        var width = (windowRect.Right - windowRect.Left);
        var height = (windowRect.Bottom - windowRect.Top);

        if (
            (width <= 0) ||
            (height <= 0)
        ) {
            return false;
        }

        if (
            ((width != m_sourceWidth) || (height != m_sourceHeight)) &&
            !TryCreateSurface(
                height: height,
                width: width
            )
        ) {
            return false;
        }

        return User32.PrintWindow(
            deviceContextHandle: m_memoryDeviceContext,
            flags: Win32Constants.PwRenderFullContent,
            windowHandle: m_windowHandle
        );
    }
    private void ReleaseSurface() {
        if (m_bitmapHandle != 0) {
            _ = Gdi32.SelectObject(
                deviceContextHandle: m_memoryDeviceContext,
                objectHandle: m_previousBitmapHandle
            );
            _ = Gdi32.DeleteObject(objectHandle: m_bitmapHandle);
            m_bitmapHandle = 0;
        }

        m_sourceHeight = 0;
        m_sourceWidth = 0;
    }
    private bool TryCreateSurface(int width, int height) {
        ReleaseSurface();
        if (m_memoryDeviceContext == 0) {
            m_memoryDeviceContext = Gdi32.CreateCompatibleDC(deviceContextHandle: 0);
            if (m_memoryDeviceContext == 0) {
                return false;
            }
        }

        var bitmapInfo = new BitmapInfoHeader {
            BitCount = 32,
            Compression = Win32Constants.BiRgb,
            Height = -height,
            Planes = 1,
            Size = (uint)Marshal.SizeOf<BitmapInfoHeader>(),
            Width = width,
        };
        var bitmapHandle = Gdi32.CreateDIBSection(
            m_memoryDeviceContext,
            in bitmapInfo,
            Win32Constants.DibRgbColors,
            out _,
            fileMappingHandle: 0,
            fileMappingOffset: 0
        );

        if (bitmapHandle == 0) {
            return false;
        }

        m_previousBitmapHandle = Gdi32.SelectObject(
            deviceContextHandle: m_memoryDeviceContext,
            objectHandle: bitmapHandle
        );
        m_bitmapHandle = bitmapHandle;
        m_sourceHeight = height;
        m_sourceWidth = width;
        return true;
    }
}
