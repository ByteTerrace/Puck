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

    /// <summary>Writes a bake dispatch's sample points followed by its saved poses. The input table grows independently
    /// of the fixed sample readback budget, so a full lattice still has room for its poses; the upload sends what differs.</summary>
    /// <param name="points">The sample points and saved pose rows.</param>
    public void WriteMeshBakePoints(ReadOnlySpan<Vector4> points) {
        if (points.IsEmpty) { return; }
        var bytes = (((long)points.Length) * 16);

        if (bytes > m_meshBakePointRegion.ByteCount) {
            Retire(replaced: m_meshBakePointRegion.Grow(byteCount: Math.Max(val1: bytes, val2: (2 * m_meshBakePointRegion.ByteCount))));
            m_bindingRevision++;
        }
        _ = m_meshBakePointRegion.Write(bytes: MemoryMarshal.AsBytes(span: points), offset: 0);
    }
}
