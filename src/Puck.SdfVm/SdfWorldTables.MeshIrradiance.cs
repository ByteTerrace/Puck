using System.Runtime.InteropServices;
using Puck.Abstractions.Gpu;

namespace Puck.SdfVm;

// The mesh path's irradiance volume (SdfMeshIrradiance): SdfMeshIrradianceProbe.ProbeWords words a probe, the slots of the
// active tier's volume, written by the residency before the frame's upload and read by the views pass through the World set
// (sdfMeshIrradiance). It is staged, so it lives in device-local memory, and a frame no probe landed in writes nothing.
public sealed partial class SdfWorldTables {
    private const int MeshIrradianceRegionIndex = 12;
    private const int MeshIrradianceBytes = ((SdfMeshIrradiance.MaxProbes * SdfMeshIrradianceProbe.ProbeWords) * sizeof(uint));

    private readonly GpuRegion m_meshIrradianceRegion;

    /// <summary>Gets the irradiance region's actual device and upload-buffer bytes.</summary>
    public GpuMemoryBytes MeshIrradianceBufferBytes => m_meshIrradianceRegion.OwnedBytes;
    /// <summary>Gets the irradiance region's native host shadow and managed copy-header bytes.</summary>
    public ulong MeshIrradianceCpuBytes => (((ulong)m_meshIrradianceRegion.ShadowBytes) + m_meshIrradianceRegion.CpuScratchBytes);

    /// <summary>Fits probe storage to the published window, retiring the replaced buffer through the tables' existing
    /// frame-ring retirement. The old window stays bound until its replacement is measured.</summary>
    /// <param name="wordCount">The words in the complete published window.</param>
    public void FitMeshIrradiance(int wordCount) {
        ArgumentOutOfRangeException.ThrowIfLessThan(wordCount, SdfMeshIrradianceProbe.ProbeWords);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(wordCount, (MeshIrradianceBytes / sizeof(uint)));
        var bytes = (((long)wordCount) * sizeof(uint));

        if (bytes == m_meshIrradianceRegion.ByteCount) { return; }
        Retire(replaced: ((bytes > m_meshIrradianceRegion.ByteCount) ? m_meshIrradianceRegion.Grow(byteCount: bytes) : m_meshIrradianceRegion.Shrink(byteCount: bytes)));
        m_bindingRevision++;
    }
    /// <summary>Writes the irradiance volume's probe slots from its first word; the upload of the frame sends what
    /// differs.</summary>
    /// <param name="words">The probe slots, <see cref="SdfMeshIrradianceProbe.ProbeWords"/> words each; at most the High
    /// tier's volume.</param>
    /// <param name="wordOffset">The first changed word in the table.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="words"/> holds more words than the table.</exception>
    public void WriteMeshIrradiance(ReadOnlySpan<uint> words, int wordOffset = 0) {
        ArgumentOutOfRangeException.ThrowIfNegative(wordOffset);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(value: wordOffset, other: (MeshIrradianceBytes / sizeof(uint)));
        ArgumentOutOfRangeException.ThrowIfGreaterThan(value: words.Length, other: ((MeshIrradianceBytes / sizeof(uint)) - wordOffset));

        if (words.IsEmpty) { return; }
        _ = m_meshIrradianceRegion.Write(bytes: MemoryMarshal.AsBytes(span: words), offset: (wordOffset * sizeof(uint)));
    }
}
