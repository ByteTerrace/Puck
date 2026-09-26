using System.Runtime.InteropServices;

namespace Puck.Platform.Windows.Interop;

// GUITHREADINFO: a GUI thread's active, focus, capture, menu-owner, move-size and caret windows and its caret rect.
[StructLayout(LayoutKind.Sequential)]
internal struct GuiThreadInfo {
    public uint Size;
    public uint Flags;
    public nint Active;
    public nint Focus;
    public nint Capture;
    public nint MenuOwner;
    public nint MoveSize;
    public nint Caret;
    public Rectangle CaretRectangle;
}
