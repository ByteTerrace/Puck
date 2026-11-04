using System.Numerics;
using System.Runtime.InteropServices;
using Puck.Abstractions.Gpu;

namespace Puck.SdfVm;

// One float4 per point or saved-pose row, uploaded before the bake samples it. Keep one bound row until actual demand.
public sealed partial class SdfWorldTables {
    private const int MeshBakePointRegionIndex = 11;
    private const int MeshBakePointRowBytes = 16;

    private readonly GpuRegion m_meshBakePointRegion;

    /// <summary>Writes a bake dispatch's sample points followed by its saved poses. The input table grows independently
    /// of the fixed sample readback budget; before first demand only one row is bound.</summary>
    /// <param name="points">The sample points and saved pose rows.</param>
    public void WriteMeshBakePoints(ReadOnlySpan<Vector4> points) {
        if (points.IsEmpty) { return; }
        // Validate the complete byte span before growing any allocation. AsBytes refuses a length above int.MaxValue.
        var data = MemoryMarshal.AsBytes(span: points);
        var bytes = (long)data.Length;

        if (bytes > m_meshBakePointRegion.ByteCount) {
            // This upload is one byte span. Both that representation and the shared region bound its capacity, in rows.
            var maximum = ((Math.Min(val1: GpuRegion.MaxByteCount, val2: (long)int.MaxValue) / MeshBakePointRowBytes) * MeshBakePointRowBytes);
            var capacity = Math.Max(val1: bytes, val2: Math.Min(val1: checked(2L * m_meshBakePointRegion.ByteCount), val2: maximum));
            Retire(replaced: m_meshBakePointRegion.Grow(byteCount: capacity));
            m_bindingRevision++;
        }
        _ = m_meshBakePointRegion.Write(bytes: data, offset: 0);
    }
}
