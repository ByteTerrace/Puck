using System.Runtime.InteropServices;

namespace Puck.Platform.Windows.Interop;

// The Win32 MINMAXINFO a WM_GETMINMAXINFO's lParam points at, filled with the system's defaults before the window
// procedure sees it.
[StructLayout(LayoutKind.Sequential)]
internal struct MinMaxInfo {
    public Point Reserved;
    public Point MaxSize;
    public Point MaxPosition;
    public Point MinTrackSize;
    public Point MaxTrackSize;
}
