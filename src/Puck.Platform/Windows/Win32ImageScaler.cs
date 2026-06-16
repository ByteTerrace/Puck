using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using Puck.Platform.Windows.Interop;

namespace Puck.Platform.Windows;

public sealed class Win32ImageScaler : IDisposable {
    public static bool TryCreate(int width, int height, [NotNullWhen(true)] out Win32ImageScaler? scaler) {
        scaler = null;

        // Allocate the frame buffer before any native handle so an overflowing extent can't strand GDI objects.
        var pixels = new byte[checked(((width * height) * 4))];
        var memoryDeviceContext = Gdi32.CreateCompatibleDC(deviceContextHandle: 0);

        if (memoryDeviceContext == 0) {
            return false;
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
            memoryDeviceContext,
            in bitmapInfo,
            Win32Constants.DibRgbColors,
            out var bitmapPixels,
            fileMappingHandle: 0,
            fileMappingOffset: 0
        );

        if (
            (bitmapHandle == 0) ||
            (bitmapPixels == 0)
        ) {
            if (bitmapHandle != 0) {
                _ = Gdi32.DeleteObject(objectHandle: bitmapHandle);
            }

            _ = Gdi32.DeleteDC(deviceContextHandle: memoryDeviceContext);
            return false;
        }

        var previousBitmapHandle = Gdi32.SelectObject(
            deviceContextHandle: memoryDeviceContext,
            objectHandle: bitmapHandle
        );

        _ = Gdi32.SetStretchBltMode(
            deviceContextHandle: memoryDeviceContext,
            stretchMode: Win32Constants.Halftone
        );
        _ = Gdi32.SetBrushOrgEx(
            deviceContextHandle: memoryDeviceContext,
            previousOrigin: 0,
            x: 0,
            y: 0
        );
        scaler = new Win32ImageScaler(
            bitmapHandle: bitmapHandle,
            bitmapPixels: bitmapPixels,
            height: height,
            memoryDeviceContext: memoryDeviceContext,
            pixels: pixels,
            previousBitmapHandle: previousBitmapHandle,
            width: width
        );
        return true;
    }

    private nint m_bitmapHandle;
    private readonly nint m_bitmapPixels;
    private nint m_memoryDeviceContext;
    private readonly byte[] m_pixels;
    private readonly nint m_previousBitmapHandle;

    public int Height { get; }
    public ReadOnlySpan<byte> Pixels => m_pixels;
    public int Width { get; }

    private Win32ImageScaler(int width, int height, nint memoryDeviceContext, nint bitmapHandle, nint bitmapPixels, nint previousBitmapHandle, byte[] pixels) {
        Height = height;
        Width = width;
        m_bitmapHandle = bitmapHandle;
        m_bitmapPixels = bitmapPixels;
        m_memoryDeviceContext = memoryDeviceContext;
        m_pixels = pixels;
        m_previousBitmapHandle = previousBitmapHandle;
    }

    public bool ScaleFrom(nint sourceDeviceContext, int sourceWidth, int sourceHeight, bool captureLayeredWindows) {
        var rasterOperation = (captureLayeredWindows
            ? Win32Constants.SrcCopy | Win32Constants.CaptureBlt
            : Win32Constants.SrcCopy);

        if (!Gdi32.StretchBlt(
            destinationDeviceContextHandle: m_memoryDeviceContext,
            destinationHeight: Height,
            destinationWidth: Width,
            destinationX: 0,
            destinationY: 0,
            rasterOperation: rasterOperation,
            sourceDeviceContextHandle: sourceDeviceContext,
            sourceHeight: sourceHeight,
            sourceWidth: sourceWidth,
            sourceX: 0,
            sourceY: 0
        )) {
            return false;
        }

        _ = Gdi32.GdiFlush();
        Marshal.Copy(
            destination: m_pixels,
            length: m_pixels.Length,
            source: m_bitmapPixels,
            startIndex: 0
        );

        for (var alphaOffset = 3; (alphaOffset < m_pixels.Length); alphaOffset += 4) {
            m_pixels[alphaOffset] = byte.MaxValue;
        }

        return true;
    }
    public void Dispose() {
        if (m_memoryDeviceContext != 0) {
            _ = Gdi32.SelectObject(
                deviceContextHandle: m_memoryDeviceContext,
                objectHandle: m_previousBitmapHandle
            );
            _ = Gdi32.DeleteDC(deviceContextHandle: m_memoryDeviceContext);
            m_memoryDeviceContext = 0;
        }

        if (m_bitmapHandle != 0) {
            _ = Gdi32.DeleteObject(objectHandle: m_bitmapHandle);
            m_bitmapHandle = 0;
        }
    }
}
