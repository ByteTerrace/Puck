using System.Runtime.InteropServices;

namespace Puck.Platform.Windows.Interop;

[StructLayout(LayoutKind.Sequential)]
internal struct Point {
    public int X;
    public int Y;
}
