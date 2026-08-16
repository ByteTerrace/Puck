using System.Runtime.InteropServices;

namespace Puck.Platform.Windows.Interop;

[StructLayout(LayoutKind.Sequential)]
internal struct Rectangle {
    public int Left;
    public int Top;
    public int Right;
    public int Bottom;
}
