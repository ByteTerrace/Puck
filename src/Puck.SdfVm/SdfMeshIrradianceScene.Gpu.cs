using System.Numerics;
using System.Runtime.InteropServices;
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
    internal GpuExport CreateGpuExport(IReadOnlyDictionary<SdfMesh, SdfMeshRegionMesh> placements, GpuArena? arena = null) {
        ArgumentNullException.ThrowIfNull(placements);
        arena ??= new GpuArena();
        var geometries = new Dictionary<Geometry, (int Nodes, int Triangles)>();
        var prefixWords = checked(m_nodes.Length * SdfMeshProbeTrace.NodeWords + m_entries.Length * SdfMeshProbeTrace.EntryWords + m_instances.Length * SdfMeshProbeTrace.InstanceWords);
        var prefix = arena.Allocate(Math.Max(1, prefixWords));
        var entries = checked(prefix + m_nodes.Length * SdfMeshProbeTrace.NodeWords);
        var instances = checked(entries + m_entries.Length * SdfMeshProbeTrace.EntryWords);
        var segments = new List<GpuSegment>();
        var changed = new List<(int Word, int Count)>();
        foreach (var geometry in m_geometries.Values) {
            var count = checked(geometry.Nodes.Length * SdfMeshProbeTrace.NodeWords + geometry.Triangles.Length);
            if (count == 0) { continue; }
            if (!arena.Geometries.TryGetValue(geometry, out var nodes)) {
                nodes = arena.Allocate(count);
                arena.Geometries.Add(geometry, nodes);
                changed.Add((nodes, count));
            }
            var triangles = checked(nodes + geometry.Nodes.Length * SdfMeshProbeTrace.NodeWords);
            geometries.Add(geometry, (nodes, triangles));
            segments.Add(new(nodes, geometry.Nodes.Length * SdfMeshProbeTrace.NodeWords, null, geometry.Nodes, null, triangles));
            segments.Add(new(triangles, geometry.Triangles.Length, null, null, geometry.Triangles, 0));
        }
        var result = new uint[Math.Max(1, prefixWords)];
        var root = m_nodes.Length == 0 ? 0u : (uint)prefix;
        WriteNodes(m_nodes, result, prefix, entries, SdfMeshProbeTrace.EntryWords);
        segments.Add(new(prefix, result.Length, result, null, null, 0));
        changed.Add((prefix, result.Length));
        segments.Add(new(0, 1, [root], null, null, 0));
        segments.Sort(static (a, b) => a.Word.CompareTo(b.Word));
        for (var index = 0; index < m_entries.Length; index++) {
            var entry = m_entries[index];
            result[entries - prefix + index * SdfMeshProbeTrace.EntryWords] = checked((uint)(instances + entry.Instance * SdfMeshProbeTrace.InstanceWords));
            result[entries - prefix + index * SdfMeshProbeTrace.EntryWords + 1] = checked((uint)(geometries[m_instances[entry.Instance].Geometry].Nodes + entry.Node * SdfMeshProbeTrace.NodeWords));
        }
        for (var index = 0; index < m_instances.Length; index++) {
            var instance = m_instances[index];
            var placement = placements[instance.Mesh];
            var record = result.AsSpan(instances - prefix + index * SdfMeshProbeTrace.InstanceWords, SdfMeshProbeTrace.InstanceWords);
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
        return new GpuExport(arena.Words, Read) { Changed = changed, Root = root, Prefix = prefix, Geometries = geometries.Keys.ToHashSet() };

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
                if (low == segments.Count) { destination.Clear(); break; }
                if (word < segments[low].Word) {
                    var gap = Math.Min(destination.Length, segments[low].Word - word);
                    destination[..gap].Clear();
                    destination = destination[gap..];
                    word += gap;
                    continue;
                }
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

    // Allocations still read by the published root are freed only after the replacement root's fence.
    // GpuMemoryRanges supplies the shared first-fit/coalescing mechanism; its unit here is bytes.
    internal sealed class GpuArena {
        private readonly GpuMemoryRanges m_ranges = new(((ulong)uint.MaxValue >> SdfMeshProbeTrace.NodeCountBits) * sizeof(uint));
        private int m_prefix;
        private int m_words;
        internal Dictionary<Geometry, int> Geometries { get; } = [];
        internal int Words => m_words;
        internal GpuArena() => Allocate(1);
        internal int Allocate(int words) {
            if (!m_ranges.TryAllocate(checked((ulong)words * sizeof(uint)), sizeof(uint), out var offset)) {
                throw new InvalidOperationException("The probe scene exceeds its packed node address space.");
            }
            var word = checked((int)(offset / sizeof(uint)));
            m_words = Math.Max(m_words, checked(word + words));
            return word;
        }
        internal void Publish(GpuExport export) {
            if (m_prefix != 0) { m_ranges.Free((ulong)m_prefix * sizeof(uint), (ulong)m_prefixWords * sizeof(uint)); }
            foreach (var geometry in Geometries.Keys.Where(geometry => !export.Geometries.Contains(geometry)).ToArray()) {
                var count = checked(geometry.Nodes.Length * SdfMeshProbeTrace.NodeWords + geometry.Triangles.Length);
                m_ranges.Free((ulong)Geometries[geometry] * sizeof(uint), (ulong)count * sizeof(uint));
                Geometries.Remove(geometry);
            }
            m_prefix = export.Prefix;
            m_prefixWords = export.Changed[^1].Count;
        }
        private int m_prefixWords;
    }

    private sealed record GpuSegment(int Word, int Count, uint[]? Words, Node[]? Nodes, int[]? Triangles, int Leaves);
    internal delegate void GpuWordWriter(int word, Span<uint> destination);
    internal sealed class GpuExport(int words, GpuWordWriter read) {
        public int Words { get; } = words;
        internal required List<(int Word, int Count)> Changed { get; init; }
        internal required uint Root { get; init; }
        internal required int Prefix { get; init; }
        internal HashSet<Geometry> Geometries { get; init; } = [];
        public void Read(int word, Span<uint> destination) => read(word, destination);
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
