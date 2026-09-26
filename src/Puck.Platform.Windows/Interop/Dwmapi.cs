using System.Runtime.InteropServices;

namespace Puck.Platform.Windows.Interop;

internal static partial class Dwmapi {
    // DWMWA_EXTENDED_FRAME_BOUNDS: a window's visible frame in physical screen pixels, drop shadow excluded, whatever
    // the caller's DPI awareness.
    public const uint ExtendedFrameBounds = 9;

    [LibraryImport("dwmapi.dll")]
    public static partial int DwmGetWindowAttribute(nint windowHandle, uint attribute, out Rectangle value, uint size);
}
