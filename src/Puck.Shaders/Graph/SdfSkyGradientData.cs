using System.Runtime.InteropServices;

namespace Puck.Shaders;

/// <summary>The contiguous stop range of one sky gradient. Stops use their existing direction-Y coordinate;
/// the common resolver owns ordering and all validation.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public record struct SdfSkyGradientData {
    /// <summary>The first record in the shared native stop table.</summary>
    public uint FirstStop;
    /// <summary>The number of records in this gradient.</summary>
    public uint StopCount;
    /// <summary>Zeroed alignment.</summary>
    public uint Reserved0;
    /// <summary>Zeroed alignment.</summary>
    public uint Reserved1;
}
