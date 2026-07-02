using System.Runtime.InteropServices;

namespace Puck.Platform.Windows.Interop;

// The Win32 DEVMODEW graphics-device mode, in its DISPLAY-device union layout (POINTL position + orientation +
// fixed-output occupy the same 16 bytes the printer fields would). Only the display-mode fields are consumed here
// (PelsWidth/PelsHeight to match the active resolution, DisplayFrequency for the refresh rate); the rest preserve the
// native layout so the marshaled offsets line up. ByValTStr makes it non-blittable, so its EnumDisplaySettings import
// stays a classic DllImport.
[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal struct DevMode {
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
    public string DeviceName;
    public ushort SpecVersion;
    public ushort DriverVersion;
    public ushort Size;
    public ushort DriverExtra;
    public uint Fields;
    public int PositionX;
    public int PositionY;
    public uint DisplayOrientation;
    public uint DisplayFixedOutput;
    public short Color;
    public short Duplex;
    public short YResolution;
    public short TtOption;
    public short Collate;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
    public string FormName;
    public ushort LogPixels;
    public uint BitsPerPel;
    public uint PelsWidth;
    public uint PelsHeight;
    public uint DisplayFlags;
    public uint DisplayFrequency;
    public uint IcmMethod;
    public uint IcmIntent;
    public uint MediaType;
    public uint DitherType;
    public uint Reserved1;
    public uint Reserved2;
    public uint PanningWidth;
    public uint PanningHeight;
}
