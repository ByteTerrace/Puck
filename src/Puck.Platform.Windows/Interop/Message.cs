using System.Runtime.InteropServices;

namespace Puck.Platform.Windows.Interop;

[StructLayout(LayoutKind.Sequential)]
internal struct Message {
    public nint WindowHandle;
    public uint Value;
    public nint WParam;
    public nint LParam;
    public uint Time;
    public Point Point;
    public uint Private;
}
