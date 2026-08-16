using System.Runtime.InteropServices;

namespace Puck.Platform.Windows.Interop;

// The Win32 MONITORINFOEXW: MONITORINFO plus the GDI device name (e.g. "\\.\DISPLAY1") that EnumDisplaySettings keys
// off. ByValTStr makes it non-blittable, so its GetMonitorInfo import stays a classic DllImport.
[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal struct MonitorInfoEx {
    public uint Size;
    public Rectangle MonitorRectangle;
    public Rectangle WorkRectangle;
    public uint Flags;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
    public string DeviceName;
}
