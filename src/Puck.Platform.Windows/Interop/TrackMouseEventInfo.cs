using System.Runtime.InteropServices;

namespace Puck.Platform.Windows.Interop;

// TRACKMOUSEEVENT: Size is the struct's own byte size, Flags the TME_* request, HoverTime unused for TME_LEAVE.
[StructLayout(LayoutKind.Sequential)]
internal struct TrackMouseEventInfo {
    public uint Size;
    public uint Flags;
    public nint WindowHandle;
    public uint HoverTime;
}
