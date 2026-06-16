using System.Runtime.InteropServices;

namespace Puck.Platform.Windows.Interop;

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct PaintStruct {
    public nint DeviceContextHandle;
    public int EraseBackground;
    public Rectangle PaintRectangle;
    public int Restore;
    public int IncrementUpdate;
    public fixed byte Reserved[32];
}
