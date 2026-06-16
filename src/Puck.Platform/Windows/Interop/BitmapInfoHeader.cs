using System.Runtime.InteropServices;

namespace Puck.Platform.Windows.Interop;

[StructLayout(LayoutKind.Sequential)]
internal struct BitmapInfoHeader {
    public uint Size;
    public int Width;
    public int Height;
    public ushort Planes;
    public ushort BitCount;
    public uint Compression;
    public uint SizeImage;
    public int XPixelsPerMeter;
    public int YPixelsPerMeter;
    public uint ColorsUsed;
    public uint ColorsImportant;
}
