using System.Runtime.InteropServices;

namespace Puck.Platform.Windows.Interop;

internal static partial class Gdi32 {
    [LibraryImport("gdi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DeleteObject(nint objectHandle);
    [LibraryImport("gdi32.dll", SetLastError = true)]
    public static partial nint CreateCompatibleDC(nint deviceContextHandle);
    [LibraryImport("gdi32.dll", SetLastError = true)]
    public static partial nint CreateDIBSection(
        nint deviceContextHandle,
        in BitmapInfoHeader bitmapInfo,
        uint usage,
        out nint bits,
        nint fileMappingHandle,
        uint fileMappingOffset
    );
    [LibraryImport("gdi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DeleteDC(nint deviceContextHandle);
    [LibraryImport("gdi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GdiFlush();
    [LibraryImport("gdi32.dll", SetLastError = true)]
    public static partial nint SelectObject(nint deviceContextHandle, nint objectHandle);
    [LibraryImport("gdi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetBrushOrgEx(nint deviceContextHandle, int x, int y, nint previousOrigin);
    [LibraryImport("gdi32.dll", SetLastError = true)]
    public static partial int SetStretchBltMode(nint deviceContextHandle, int stretchMode);
    [LibraryImport("gdi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool StretchBlt(
        nint destinationDeviceContextHandle,
        int destinationX,
        int destinationY,
        int destinationWidth,
        int destinationHeight,
        nint sourceDeviceContextHandle,
        int sourceX,
        int sourceY,
        int sourceWidth,
        int sourceHeight,
        uint rasterOperation
    );
}
