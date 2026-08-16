using System.Runtime.InteropServices;

namespace Puck.Platform.Windows.Interop;

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal struct WindowClassEx {
    public uint Size;
    public uint Style;
    public WndProc? WindowProcedure;
    public int ClassExtraBytes;
    public int WindowExtraBytes;
    public nint InstanceHandle;
    public nint IconHandle;
    public nint CursorHandle;
    public nint BackgroundBrushHandle;
    public nint MenuName;
    public string? ClassName;
    public nint SmallIconHandle;
}
