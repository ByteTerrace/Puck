using System.Numerics;
using System.Runtime.InteropServices;
using Puck.Abstractions.Gpu;
using Puck.Shaders;

namespace Puck.SdfVm;

// The mesh bake's point table: the surface points a bake dispatch samples the field's material at
// (SdfMeshPathBake), one float4 a point, written by the residency before the frame's upload and read by the bake kernel
// through the World set (sdfMeshBakePoints). It is staged, so it lives in device-local memory, and a frame that samples no
// points writes nothing to it.
public sealed partial class SdfWorldTables {
    private const int MeshBakePointRegionIndex = 11;
    private const int MeshBakePointBytes = (SdfWorldPackage.MeshBakeMaxSamples * 16);

    private readonly GpuRegion m_meshBakePointRegion;

    /// <summary>Writes the points a bake dispatch samples into the point table, from its first entry; the upload of the
    /// frame sends what differs.</summary>
    /// <param name="points">The points, the fourth component unused; at most <see cref="SdfWorldPackage.MeshBakeMaxSamples"/>.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="points"/> holds more points than the table.</exception>
    public void WriteMeshBakePoints(ReadOnlySpan<Vector4> points) {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(value: points.Length, other: SdfWorldPackage.MeshBakeMaxSamples);

        if (points.IsEmpty) { return; }
        _ = m_meshBakePointRegion.Write(bytes: MemoryMarshal.AsBytes(span: points), offset: 0);
    }
}
