using Puck.Maths;
using System.Numerics;
using System.Runtime.InteropServices;
using Puck.Abstractions.Gpu;

namespace Puck.SdfVm;

public sealed partial class SdfMeshIrradianceScene {
    // The draws the scene keeps, as many as it counts of its rented array.
    private ReadOnlySpan<Instance> Draws => m_content.Instances.AsSpan(start: 0, length: m_content.InstanceCount);

    internal bool HasGpuPlacements(IReadOnlyDictionary<SdfMesh, SdfMeshRegionMesh> placements, IReadOnlySet<SdfMesh> owed) {
        foreach (ref readonly var instance in Draws) {
            if (!placements.ContainsKey(instance.Mesh) || owed.Contains(instance.Mesh)) { return false; }
        }
        return true;
    }
    internal Dictionary<SdfMesh, SdfMeshRegionMesh> CaptureGpuPlacements(IReadOnlyDictionary<SdfMesh, SdfMeshRegionMesh> placements) {
        var captured = new Dictionary<SdfMesh, SdfMeshRegionMesh>(ReferenceEqualityComparer.Instance);
        foreach (ref readonly var instance in Draws) { captured.TryAdd(instance.Mesh, placements[instance.Mesh]); }
        return captured;
    }
    internal bool MatchesGpuPlacements(IReadOnlyDictionary<SdfMesh, SdfMeshRegionMesh> current, IReadOnlyDictionary<SdfMesh, SdfMeshRegionMesh> captured) {
        foreach (ref readonly var instance in Draws) {
            if (!captured.TryGetValue(instance.Mesh, out var prior)) { return false; }
            var placed = current[instance.Mesh];
            if (placed.VertexWord != prior.VertexWord || placed.IndexWord != prior.IndexWord ||
                (!instance.TriangleMaterials.IsEmpty && placed.MaterialWord != prior.MaterialWord)) { return false; }
        }
        return true;
    }
    /// <summary>Gets the words a GPU export's prefix holds (the top level's nodes, its entries and one record per draw):
    /// what each scene change uploads whatever geometry it shares with the scene before it.</summary>
    public int PrefixWords => checked(m_content.NodeCount * SdfMeshProbeTrace.NodeWords + m_content.EntryCount * SdfMeshProbeTrace.EntryWords + m_content.InstanceCount * SdfMeshProbeTrace.InstanceWords);

    // The draws as a top level's instances, in the order the export writes their records, so an instance's index is its
    // record's (SdfMeshProbeGpuWork.Rays): each draw's geometry, the mesh whose placement holds the geometry's words, and
    // its transform. A reader holds a scene claim.
    internal int GpuInstanceCount => m_content.InstanceCount;
    internal (Geometry Geometry, SdfMesh Mesh, Matrix4x4 ObjectToWorld) GpuInstance(int index) {
        ref readonly var instance = ref Draws[index];

        return (instance.Geometry, instance.Mesh, instance.ObjectToWorld);
    }
    /// <summary>Exports the existing hierarchy in traversal order, referencing triangles in an already placed mesh
    /// region. Word zero is the top root's address, or zero for an empty scene, and word one the first instance record's
    /// (<see cref="SdfMeshProbeTrace.InstanceBaseWord"/>). No triangles or hierarchy are rebuilt.
    /// The caller retains this scene and the region's placements until the GPU submission completes.</summary>
    /// <param name="placements">The mesh placements of the upload the trace will bind.</param>
    /// <returns>Nodes, leaf indices, braid entries and instance records with absolute word addresses.</returns>
    public uint[] ExportGpu(IReadOnlyDictionary<SdfMesh, SdfMeshRegionMesh> placements) {
        var export = CreateGpuExport(placements);
        var words = new uint[export.Words];
        export.Read(0, words);
        return words;
    }

    // The runtime stages directly from the scene's existing arrays: geometry nodes and leaf ordinals, and the prefix (the
    // top level's nodes, entries and instance records), each word written when a reader asks for it, so no part of the
    // export gains a second host copy. A reader holds a scene claim and the captured placements for as long as it reads,
    // as SdfMeshProbeGpuWork's upload does.
    internal GpuExport CreateGpuExport(IReadOnlyDictionary<SdfMesh, SdfMeshRegionMesh> placements, GpuArena? arena = null) {
        ArgumentNullException.ThrowIfNull(placements);
        arena ??= new GpuArena();
        var content = m_content;
        var geometries = new Dictionary<Geometry, (int Nodes, int Triangles, int Frames)>(capacity: m_geometries.Count);
        var segments = new List<GpuSegment>(capacity: ((3 * m_geometries.Count) + 2));
        var changed = new List<(int Word, int Count)>();
        foreach (var geometry in m_geometries.Values) {
            var count = geometry.GpuWords;
            if (count == 0) { continue; }
            if (!arena.Geometries.TryGetValue(geometry, out var placement)) {
                placement = new GpuPlacement(arena.Allocate(count));
                arena.Geometries.Add(geometry, placement);
            }
            var nodes = placement.Word;
            var written = placement.WrittenWords;
            if (written < count) { changed.Add((nodes + written, count - written)); }
            var triangles = checked(nodes + geometry.Nodes.Length * SdfMeshProbeTrace.PackedNodeWords);
            var frames = checked(triangles + geometry.Triangles.Length);
            geometries.Add(geometry, (nodes, triangles, frames));
            segments.Add(new(nodes, geometry.Nodes.Length * SdfMeshProbeTrace.PackedNodeWords, GpuSegmentKind.Nodes, geometry, triangles));
            segments.Add(new(triangles, geometry.Triangles.Length, GpuSegmentKind.Triangles, geometry, 0));
            segments.Add(new(frames, geometry.Frames.Length * SdfMeshProbeTrace.FrameWords, GpuSegmentKind.Frames, geometry, 0));
        }
        // The prefix is allocated last, so a geometry that cannot be placed throws before anything only this export
        // would free; a geometry placed meanwhile stays in the arena, which frees it at the next publication that does
        // not name it. An empty scene still exports one zero prefix word, so a prefix allocation always exists to free.
        var prefixCount = Math.Max(1, PrefixWords);
        var prefix = arena.Allocate(prefixCount);
        var prefixBytes = ((long)prefixCount * sizeof(uint));
        Counters.Add(kind: SdfMeshIrradiance.PrefixBytes, amount: prefixBytes);
        Counters.Raise(kind: SdfMeshIrradiance.PeakPrefixBytes, value: prefixBytes);
        var nodeWords = content.NodeCount * SdfMeshProbeTrace.NodeWords;
        var entryWords = content.EntryCount * SdfMeshProbeTrace.EntryWords;
        var instanceWords = content.InstanceCount * SdfMeshProbeTrace.InstanceWords;
        var entries = checked(prefix + nodeWords);
        var instances = checked(entries + entryWords);
        var root = content.NodeCount == 0 ? 0u : (uint)prefix;
        segments.Add(new(prefix, prefixCount, GpuSegmentKind.Prefix, null, 0));
        changed.Add((prefix, prefixCount));
        segments.Add(new(0, SdfMeshProbeTrace.RootWords, GpuSegmentKind.Root, null, 0));
        segments.Sort(static (a, b) => a.Word.CompareTo(b.Word));
        return new GpuExport(arena.Words, Read) { Changed = changed, Root = root, Instances = (uint)instances, Prefix = prefix, Geometries = geometries };

        void Read(int word, Span<uint> destination) {
            var low = PartitionPoint.Of(span: CollectionsMarshal.AsSpan(segments), state: word, isBefore: static (at, segment) => (segment.Word + segment.Count <= at));
            // One record of any kind the export holds, the widest an instance's.
            Span<uint> record = stackalloc uint[SdfMeshProbeTrace.InstanceWords];
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
                switch (segment.Kind) {
                    case GpuSegmentKind.Root:
                        for (var index = 0; (index < target.Length); index++) {
                            target[index] = (((offset + index) == SdfMeshProbeTrace.InstanceBaseWord) ? (uint)instances : root);
                        }
                        break;
                    case GpuSegmentKind.Triangles: MemoryMarshal.Cast<int, uint>(segment.Geometry!.Triangles.AsSpan(offset, take)).CopyTo(target); break;
                    default:
                        // Fixed-size records: each one the run reaches is written whole and the part it covers copied.
                        while (!target.IsEmpty) {
                            var (part, start, stride) = segment.Kind switch {
                                GpuSegmentKind.Nodes => (PrefixPart.None, 0, SdfMeshProbeTrace.PackedNodeWords),
                                GpuSegmentKind.Frames => (PrefixPart.None, 0, SdfMeshProbeTrace.FrameWords),
                                _ => PrefixRecord(offset),
                            };
                            if (stride == 0) { target.Clear(); break; }
                            var index = (offset - start) / stride;
                            var within = (offset - start) % stride;
                            var words = record[..stride];
                            switch (segment.Kind) {
                                case GpuSegmentKind.Nodes: WritePackedNode(segment.Geometry!.Nodes[index], words, segment.Word, segment.Leaves); break;
                                case GpuSegmentKind.Frames: WriteFrame(segment.Geometry!.Frames[index], words); break;
                                default: WritePrefixRecord(part, index, words); break;
                            }
                            var length = Math.Min(target.Length, stride - within);
                            words.Slice(within, length).CopyTo(target);
                            offset += length;
                            target = target[length..];
                        }
                        break;
                }
                word += take;
                destination = destination[take..];
            }
        }
        // Which part of the prefix holds an offset (nodes, then entries, then instance records), where that part starts,
        // and how wide its records are; zero wide past them, in an empty scene's one zero word.
        (PrefixPart Part, int Start, int Stride) PrefixRecord(int offset) =>
            (offset < nodeWords) ? (PrefixPart.Node, 0, SdfMeshProbeTrace.NodeWords) :
            (offset < (nodeWords + entryWords)) ? (PrefixPart.Entry, nodeWords, SdfMeshProbeTrace.EntryWords) :
            (offset < (nodeWords + entryWords + instanceWords)) ? (PrefixPart.Instance, (nodeWords + entryWords), SdfMeshProbeTrace.InstanceWords) : (PrefixPart.None, 0, 0);
        void WritePrefixRecord(PrefixPart part, int index, Span<uint> words) {
            switch (part) {
                case PrefixPart.Node:
                    WriteNodes(content.Nodes.AsSpan(index, 1), words, prefix, entries, SdfMeshProbeTrace.EntryWords);
                    break;
                case PrefixPart.Entry:
                    var entry = content.Entries[index];
                    words[0] = checked((uint)(instances + entry.Instance * SdfMeshProbeTrace.InstanceWords));
                    var placed = geometries[content.Instances[entry.Instance].Geometry];
                    words[1] = checked((uint)(placed.Nodes + entry.Node * SdfMeshProbeTrace.PackedNodeWords));
                    words[2] = checked((uint)(placed.Frames + entry.Braid * SdfMeshProbeTrace.FrameWords));
                    break;
                default:
                    ref readonly var instance = ref content.Instances[index];
                    words.Clear();
                    WriteInstance(instance: in instance, index: index, placement: placements[instance.Mesh], record: words);
                    break;
            }
        }
    }

    private enum PrefixPart : byte { None, Node, Entry, Instance }

    // One instance record: its inverse transform, its winding-aware normal matrix, its mesh's placement and material base,
    // whether its transform is identity, its ordinal and whether it encloses a solid. The words it leaves unwritten stay as
    // the caller cleared them.
    private static void WriteInstance(in Instance instance, int index, SdfMeshRegionMesh placement, Span<uint> record) {
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
        record[SdfMeshProbeTrace.SolidWord] = instance.Solid ? 1u : 0u;
    }

    // Allocations still read by the published root are freed only after the replacement root's fence.
    // The arena's unit is a word; word 0 is the root and never freed.
    internal sealed class GpuArena {
        private readonly GpuRangeAllocator m_ranges = new(size: ((ulong)uint.MaxValue >> SdfMeshProbeTrace.NodeCountBits));
        private int m_prefix;
        private int m_words;
        internal Dictionary<Geometry, GpuPlacement> Geometries { get; } = [];
        internal int Words => m_words;
        internal GpuArena() => Allocate(SdfMeshProbeTrace.RootWords);
        internal int Allocate(int words) {
            if (!m_ranges.TryAllocate(count: (ulong)words, start: out var start)) {
                throw new InvalidOperationException("The probe scene exceeds its packed node address space.");
            }
            var word = checked((int)start);
            m_words = Math.Max(m_words, checked(word + words));
            return word;
        }
        internal void Publish(GpuExport export) {
            if (m_prefix != 0) { _ = m_ranges.Free(start: (ulong)m_prefix); }
            foreach (var geometry in Geometries.Keys.Where(geometry => !export.Geometries.ContainsKey(geometry)).ToArray()) {
                _ = m_ranges.Free(start: (ulong)Geometries[geometry].Word);
                Geometries.Remove(geometry);
            }
            m_prefix = export.Prefix;
        }
        internal void Abandon(GpuExport export) {
            // Geometry is immutable and its completed chunks remain useful across roster changes. Only the obsolete
            // scene prefix is discarded. A later export resumes each geometry at its fence-proven written prefix.
            _ = m_ranges.Free(start: (ulong)export.Prefix);
        }
        internal void Complete(GpuExport export, int word, int count) {
            foreach (var geometry in export.Geometries.Keys) {
                var placement = Geometries[geometry];
                var start = Math.Max(word, placement.Word);
                var end = Math.Min(word + count, placement.Word + geometry.GpuWords);
                if (start < end) { placement.Written.Add(start - placement.Word, end - start); }
            }
        }
    }

    internal sealed class GpuPlacement(int word) {
        internal int Word { get; } = word;
        // Completion callbacks can arrive in slot-index order, at most SlotCount disjoint runs ahead of the prefix.
        internal GpuUploadRuns Written { get; } = new(SdfGpuWork.SlotCount + 2);
        internal int WrittenWords => Written.Count != 0 && Written.Start(0) == 0 ? Written.Length(0) : 0;
    }

    private enum GpuSegmentKind : byte { Root, Prefix, Nodes, Triangles, Frames }
    // A run of the export's words: word zero's root, the prefix, or one geometry's packed nodes (whose leaves start at
    // Leaves), triangle ordinals or braid frames.
    private readonly record struct GpuSegment(int Word, int Count, GpuSegmentKind Kind, Geometry? Geometry, int Leaves);
    internal delegate void GpuWordWriter(int word, Span<uint> destination);
    internal sealed class GpuExport(int words, GpuWordWriter read) {
        public int Words { get; } = words;
        internal required List<(int Word, int Count)> Changed { get; init; }
        internal required uint Root { get; init; }
        // The first instance record's address, the scene's InstanceBaseWord.
        internal required uint Instances { get; init; }
        internal required int Prefix { get; init; }
        // Each geometry the export names, with its node, triangle and frame words' addresses.
        internal required IReadOnlyDictionary<Geometry, (int Nodes, int Triangles, int Frames)> Geometries { get; init; }
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
    // A geometry node's record: its three words of steps, then its link as an address (an inner node's first child's
    // record, a leaf's first triangle ordinal).
    private static void WritePackedNode(in PackedNode node, Span<uint> words, int nodeBase, int leafBase) {
        words[0] = node.Word(index: 0);
        words[1] = node.Word(index: 1);
        words[2] = node.Word(index: 2);
        var first = checked(node.Count == 0 ? nodeBase + node.First * SdfMeshProbeTrace.PackedNodeWords : leafBase + node.First);
        words[3] = checked((uint)first * (1u << SdfMeshProbeTrace.NodeCountBits)) | (uint)node.Count;
    }
    private static void WriteFrame(in BraidFrame frame, Span<uint> words) {
        var floats = MemoryMarshal.Cast<uint, float>(words);
        floats[0] = frame.Low.X; floats[1] = frame.Low.Y; floats[2] = frame.Low.Z;
        floats[3] = frame.Step.X; floats[4] = frame.Step.Y; floats[5] = frame.Step.Z;
    }
}
