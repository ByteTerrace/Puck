using System.Runtime.InteropServices;

namespace Puck.Platform.Windows.Interop;

[StructLayout(LayoutKind.Sequential)]
internal struct CreateStruct {
    public nint CreateParameters;
    public nint InstanceHandle;
    public nint MenuHandle;
    public nint ParentHandle;
    public int Height;
    public int Width;
    public int Y;
    public int X;
    public int Style;
    public nint Name;
    public nint Class;
    public uint ExtendedStyle;
}
