using System.Runtime.InteropServices;
using Puck.Abstractions.Gpu;

namespace Puck.SdfVm;

// The mesh path's irradiance volume (SdfMeshIrradiance): SdfMeshIrradiance.ProbeWords words a probe, the slots of the
// High tier's volume, written by the residency before the frame's upload and read by the views pass through the World set
// (sdfMeshIrradiance). It is staged, so it lives in device-local memory, and a frame no probe landed in writes nothing.
public sealed partial class SdfWorldTables {
    private const int MeshIrradianceRegionIndex = 12;
    private const int MeshIrradianceBytes = ((SdfMeshIrradiance.MaxProbes * SdfMeshIrradiance.ProbeWords) * sizeof(uint));

    private readonly GpuRegion m_meshIrradianceRegion;

    /// <summary>Writes the irradiance volume's probe slots from its first word; the upload of the frame sends what
    /// differs.</summary>
    /// <param name="words">The probe slots, <see cref="SdfMeshIrradiance.ProbeWords"/> words each; at most the High
    /// tier's volume.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="words"/> holds more words than the table.</exception>
    public void WriteMeshIrradiance(ReadOnlySpan<uint> words) {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(value: words.Length, other: (MeshIrradianceBytes / sizeof(uint)));

        if (words.IsEmpty) { return; }
        _ = m_meshIrradianceRegion.Write(bytes: MemoryMarshal.AsBytes(span: words), offset: 0);
    }
}
