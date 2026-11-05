using System.Numerics;
using System.Runtime.InteropServices;
using System.Buffers;
using Puck.Abstractions.Gpu;

namespace Puck.SdfVm;

public sealed partial class SdfMeshIrradianceScene {
    internal bool HasGpuPlacements(IReadOnlyDictionary<SdfMesh, SdfMeshRegionMesh> placements, IReadOnlySet<SdfMesh> owed) {
        foreach (var instance in m_instances) {
            if (!placements.ContainsKey(instance.Mesh) || owed.Contains(instance.Mesh)) { return false; }
        }
        return true;
    }
    /// <summary>Exports the existing hierarchy in traversal order, referencing triangles in an already placed mesh
    /// region. Word zero is the top root's address, or zero for an empty scene. No triangles or hierarchy are rebuilt.
    /// The caller retains this scene and the region's placements until the GPU submission completes.</summary>
    /// <param name="placements">The mesh placements of the upload the trace will bind.</param>
    /// <returns>Nodes, leaf indices, braid entries and instance records with absolute word addresses.</returns>
    public uint[] ExportGpu(IReadOnlyDictionary<SdfMesh, SdfMeshRegionMesh> placements) {
        var export = CreateGpuExport(placements);
        var words = new uint[export.Words];
        export.Read(0, words);
        return words;
    }

    // The runtime stages directly from the scene's existing arrays. Only the small instance/top-level prefix is
    // packed on the host; geometry nodes and leaf ordinals never gain a second full host copy.
    internal GpuExport CreateGpuExport(IReadOnlyDictionary<SdfMesh, SdfMeshRegionMesh> placements) {
        ArgumentNullException.ThrowIfNull(placements);
        var geometries = new Dictionary<Geometry, (int Nodes, int Triangles)>();
        var words = checked(1 + m_nodes.Length * SdfMeshProbeTrace.NodeWords);
        var entries = words;
        words = checked(words + m_entries.Length * SdfMeshProbeTrace.EntryWords);
        var instances = words;
        words = checked(words + m_instances.Length * SdfMeshProbeTrace.InstanceWords);
        var prefixWords = words;
        foreach (var geometry in m_geometries.Values) {
            var nodes = words;
            words = checked(words + geometry.Nodes.Length * SdfMeshProbeTrace.NodeWords);
            geometries.Add(geometry, (nodes, words));
            words = checked(words + geometry.Triangles.Length);
        }
        var result = new uint[prefixWords];
        result[0] = m_nodes.Length == 0 ? 0u : 1u;
        WriteNodes(m_nodes, result.AsSpan(1), 1, entries, SdfMeshProbeTrace.EntryWords);
        var segments = new List<GpuSegment> { new(0, prefixWords, result, null, null, 0) };
        foreach (var (geometry, address) in geometries) {
            segments.Add(new(address.Nodes, geometry.Nodes.Length * SdfMeshProbeTrace.NodeWords, null, geometry.Nodes, null, address.Triangles));
            segments.Add(new(address.Triangles, geometry.Triangles.Length, null, null, geometry.Triangles, 0));
        }
        for (var index = 0; index < m_entries.Length; index++) {
            var entry = m_entries[index];
            result[entries + index * SdfMeshProbeTrace.EntryWords] = checked((uint)(instances + entry.Instance * SdfMeshProbeTrace.InstanceWords));
            result[entries + index * SdfMeshProbeTrace.EntryWords + 1] = checked((uint)(geometries[m_instances[entry.Instance].Geometry].Nodes + entry.Node * SdfMeshProbeTrace.NodeWords));
        }
        for (var index = 0; index < m_instances.Length; index++) {
            var instance = m_instances[index];
            var placement = placements[instance.Mesh];
            var record = result.AsSpan(instances + index * SdfMeshProbeTrace.InstanceWords, SdfMeshProbeTrace.InstanceWords);
            var inverse = instance.WorldToObject;
            MemoryMarshal.Cast<Matrix4x4, uint>(new ReadOnlySpan<Matrix4x4>(in inverse)).CopyTo(record);
            var normal = instance.NormalToWorld;
            var matrix = MemoryMarshal.Cast<uint, float>(record.Slice(SdfMeshProbeTrace.NormalWord, 9));
            matrix[0] = normal.M11; matrix[1] = normal.M12; matrix[2] = normal.M13;
            matrix[3] = normal.M21; matrix[4] = normal.M22; matrix[5] = normal.M23;
            matrix[6] = normal.M31; matrix[7] = normal.M32; matrix[8] = normal.M33;
            record[SdfMeshProbeTrace.GeometryWord] = (uint)placement.IndexWord;
            record[SdfMeshProbeTrace.GeometryWord + 1] = (uint)placement.VertexWord;
            record[SdfMeshProbeTrace.GeometryWord + 2] = instance.TriangleMaterials.IsEmpty ? uint.MaxValue : (uint)placement.MaterialWord;
            record[SdfMeshProbeTrace.GeometryWord + 3] = unchecked((uint)instance.Material);
            record[SdfMeshProbeTrace.IdentityWord] = instance.Identity ? 1u : 0u;
            record[SdfMeshProbeTrace.InstanceIndexWord] = (uint)index;
        }
        return new GpuExport(words, Read);

        void Read(int word, Span<uint> destination) {
            var low = 0;
            var high = segments.Count;
            while (low < high) {
                var middle = (low + high) / 2;
                if (segments[middle].Word + segments[middle].Count <= word) { low = middle + 1; }
                else { high = middle; }
            }
            Span<uint> nodeWords = stackalloc uint[SdfMeshProbeTrace.NodeWords];
            while (!destination.IsEmpty) {
                var segment = segments[low++];
                var offset = word - segment.Word;
                var take = Math.Min(destination.Length, segment.Count - offset);
                var target = destination[..take];
                if (segment.Words is { } packed) { packed.AsSpan(offset, take).CopyTo(target); }
                else if (segment.Triangles is { } triangles) { MemoryMarshal.Cast<int, uint>(triangles.AsSpan(offset, take)).CopyTo(target); }
                else {
                    while (!target.IsEmpty) {
                        var index = offset / SdfMeshProbeTrace.NodeWords;
                        var within = offset % SdfMeshProbeTrace.NodeWords;
                        WriteNodes(segment.Nodes!.AsSpan(index, 1), nodeWords, segment.Word, segment.Leaves, 1);
                        var length = Math.Min(target.Length, SdfMeshProbeTrace.NodeWords - within);
                        nodeWords.Slice(within, length).CopyTo(target);
                        offset += length;
                        target = target[length..];
                    }
                }
                word += take;
                destination = destination[take..];
            }
        }
    }

    private sealed record GpuSegment(int Word, int Count, uint[]? Words, Node[]? Nodes, int[]? Triangles, int Leaves);
    internal delegate void GpuWordWriter(int word, Span<uint> destination);
    internal sealed class GpuExport(int words, GpuWordWriter read) : IGpuRegionSource {
        public int Words { get; } = words;
        public void Read(int word, Span<uint> destination) => read(word, destination);
        public void WriteWords(IGpuStorageBuffer buffer, ulong destinationOffsetBytes, int word, int count) {
            var scratch = ArrayPool<uint>.Shared.Rent(65536);
            try {
                while (count > 0) {
                    var take = Math.Min(count, scratch.Length);
                    read(word, scratch.AsSpan(0, take));
                    buffer.Write<uint>(scratch.AsSpan(0, take), destinationOffsetBytes);
                    word += take;
                    count -= take;
                    destinationOffsetBytes += (ulong)take * 4;
                }
            } finally { ArrayPool<uint>.Shared.Return(scratch); }
        }
    }

    private static void WriteNodes(ReadOnlySpan<Node> nodes, Span<uint> words, int nodeBase, int leafBase, int leafStride) {
        for (var index = 0; index < nodes.Length; index++) {
            var node = nodes[index];
            var record = words.Slice(index * SdfMeshProbeTrace.NodeWords, SdfMeshProbeTrace.NodeWords);
            var floats = MemoryMarshal.Cast<uint, float>(record);
            floats[0] = node.Low.X; floats[1] = node.Low.Y; floats[2] = node.Low.Z;
            floats[3] = node.High.X; floats[4] = node.High.Y; floats[5] = node.High.Z;
            var first = checked(node.Count == 0 ? nodeBase + node.First * SdfMeshProbeTrace.NodeWords : leafBase + node.First * leafStride);
            record[6] = checked((uint)first * (1u << SdfMeshProbeTrace.NodeCountBits)) | (uint)node.Count;
        }
    }
}
