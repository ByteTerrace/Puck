using System.Runtime.InteropServices;

namespace Puck.Platform.Windows.Interop;

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal struct MonitorInfo {
    public uint Size;
    public Rectangle MonitorRectangle;
    public Rectangle WorkRectangle;
    public uint Flags;
}
