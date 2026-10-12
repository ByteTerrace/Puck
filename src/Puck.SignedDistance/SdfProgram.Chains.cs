using System.Runtime.InteropServices;

namespace Puck.SignedDistance;

// The chain table (see the packed layout in SdfProgram.cs): each directory segment's chains, the program's
// specialization set, and their packing.
public sealed partial class SdfProgram {
    private readonly ChainTable m_chainTable;

    /// <summary>Gets the number of chains the program's segments run, every segment's together: the length of the packed
    /// identity list, in which a chain two segments share appears twice.</summary>
    public int ChainCount => m_chainTable.Chains.Length;
    /// <summary>Gets the number of directory segments that carry a rigid-leaf plan (<see cref="SegmentRigidPlanFlag"/>).
    /// Their chains are in the table too: a kernel without dynamic transforms walks a dynamic rigid segment's
    /// instructions.</summary>
    public int RigidSegmentCount {
        get {
            var segments = SegmentDirectoryWord();
            var count = 0;

            for (var segment = 0; (segment < SkipSegmentCount); segment++) {
                if ((m_words[((segments + ((DirectoryHeaderVectors + (BoundRecordVectors * segment)) * WordsPerVector)) + WordsPerVector)] & SegmentRigidPlanFlag) != 0u) {
                    count++;
                }
            }

            return count;
        }
    }
    /// <summary>Gets the program's specialization set: its distinct chains and its vocabulary.</summary>
    public SdfSpecializationSet Specialization => m_chainTable.Set;

    /// <summary>Returns the chains one directory segment runs, in program order. They cover the segment's instructions
    /// exactly, each once.</summary>
    /// <param name="segment">The zero-based directory entry, below <see cref="SkipSegmentCount"/>.</param>
    /// <returns>The segment's chains.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The entry is absent.</exception>
    public ReadOnlySpan<SdfChain> ChainsOf(int segment) {
        ArgumentOutOfRangeException.ThrowIfNegative(value: segment);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(value: segment, other: (m_chainTable.SegmentFirst.Length - 1));

        var first = m_chainTable.SegmentFirst[segment];

        return m_chainTable.Chains.AsSpan(start: first, length: (m_chainTable.SegmentFirst[(segment + 1)] - first));
    }

    // Cuts every directory segment into chains: a chain closes at each compose (ShapeBlend, PopField) and wherever a
    // segment range (SegmentRanges) ends, so a directory entry that merged several ranges runs exactly their chains.
    // Collects the vocabulary from every instruction, which covers rigid leaves and compiled part programs, since both
    // evaluate the program's own shapes.
    private ChainTable BuildChainTable(List<BoundRecord> segments) {
        var rangeStarts = new bool[m_instructions.Length];

        foreach (var (start, _) in SegmentRanges()) {
            rangeStarts[start] = true;
        }

        var distinct = new Dictionary<uint[], SdfChain>(comparer: ChainKeyComparer.Instance);
        var lookup = distinct.GetAlternateLookup<ReadOnlySpan<uint>>();
        var identities = new Dictionary<uint, SdfChain>();
        var chains = new List<SdfChain>(capacity: segments.Count);
        var segmentFirst = new int[(segments.Count + 1)];
        var steps = new List<SdfChainStep>();
        var key = new uint[SdfChain.KeyWords(steps: 8)];

        for (var segmentIndex = 0; (segmentIndex < segments.Count); segmentIndex++) {
            var segment = segments[segmentIndex];

            segmentFirst[segmentIndex] = chains.Count;

            for (var index = segment.Instruction; (index < segment.End); index++) {
                var instruction = m_instructions[index];

                if (rangeStarts[index] && (steps.Count != 0)) {
                    Close(end: SdfChainEnd.Cut);
                }

                switch (instruction.Op) {
                    case SdfOp.ShapeBlend:
                        Close(end: SdfChainEnd.Shape);
                        break;
                    case SdfOp.PopField:
                        Close(end: SdfChainEnd.Pop);
                        break;
                    default:
                        steps.Add(item: SdfChainStep.Of(instruction: in instruction));
                        break;
                }
            }

            if (steps.Count != 0) {
                Close(end: SdfChainEnd.Cut);
            }
        }

        segmentFirst[segments.Count] = chains.Count;

        var shapes = new SortedSet<SdfShapeType>();
        var blends = new SortedSet<SdfBlendOp>();
        var composes = new SortedSet<SdfBlendOp>();

        foreach (var instruction in m_instructions) {
            if (instruction.Op == SdfOp.ShapeBlend) {
                _ = shapes.Add(item: ((SdfShapeType)instruction.Shape));
                _ = blends.Add(item: ((SdfBlendOp)instruction.Blend));
            } else if (instruction.Op == SdfOp.PopField) {
                _ = composes.Add(item: ((SdfBlendOp)instruction.Blend));
            }
        }

        return new ChainTable(
            Chains: [.. chains],
            SegmentFirst: segmentFirst,
            Set: new SdfSpecializationSet(
                blends: [.. blends],
                chains: [.. distinct.Values.OrderBy(keySelector: static chain => chain.Identity)],
                composes: [.. composes],
                shapes: [.. shapes]
            )
        );

        void Close(SdfChainEnd end) {
            var stepSpan = CollectionsMarshal.AsSpan(list: steps);
            var words = SdfChain.KeyWords(steps: stepSpan.Length);

            if (key.Length < words) {
                key = new uint[Math.Max(val1: words, val2: (2 * key.Length))];
            }

            SdfChain.WriteKey(end: end, key: key, steps: stepSpan);

            var keySpan = key.AsSpan(length: words, start: 0);

            if (!lookup.TryGetValue(key: keySpan, value: out var chain)) {
                chain = new SdfChain(end: end, steps: steps);

                if (identities.TryGetValue(key: chain.Identity, value: out var other)) {
                    throw new InvalidOperationException(message: $"The chains '{other}' and '{chain}' share the 32-bit identity 0x{chain.Identity:X8}, so one program's chain table cannot name both; change either chain.");
                }

                identities.Add(key: chain.Identity, value: chain);
                distinct.Add(key: keySpan.ToArray(), value: chain);
            }

            chains.Add(item: chain);
            steps.Clear();
        }
    }
    // Packs the chain table at `offset` and records the offset in the world-segment list header's free lane:
    //   header  = (chain count, absolute vector offset of the identity list, 0, 0)
    //   entries = two segments a vector, each (first chain, chain count), in directory order
    //   list    = four chain identities a vector, every segment's chains in directory order
    private void PackChainTable(int offset, int worldSegmentOffset) {
        var segmentCount = (m_chainTable.SegmentFirst.Length - 1);
        var entries = (offset + DirectoryHeaderVectors);
        var identities = (entries + ChainTable.EntryVectors(segments: segmentCount));

        m_words[((worldSegmentOffset * WordsPerVector) + ChainTableLane)] = ((uint)offset);
        m_words[((offset * WordsPerVector) + ChainCountLane)] = ((uint)m_chainTable.Chains.Length);
        m_words[((offset * WordsPerVector) + ChainIdentitiesLane)] = ((uint)identities);

        for (var segment = 0; (segment < segmentCount); segment++) {
            var entry = (((entries + (segment / ChainSegmentsPerVector)) * WordsPerVector) + ((segment % ChainSegmentsPerVector) * ChainSegmentWords));
            var first = m_chainTable.SegmentFirst[segment];

            m_words[(entry + ChainSegmentFirstWord)] = ((uint)first);
            m_words[(entry + ChainSegmentCountWord)] = ((uint)(m_chainTable.SegmentFirst[(segment + 1)] - first));
        }

        for (var chain = 0; (chain < m_chainTable.Chains.Length); chain++) {
            m_words[(((identities + (chain / ChainIdentitiesPerVector)) * WordsPerVector) + (chain % ChainIdentitiesPerVector))] = m_chainTable.Chains[chain].Identity;
        }
    }
    // The chain-table words a run of directory segments owns: their entries and their chains' identities.
    private int ChainTableWordsOf(int firstSegment, int endSegment) =>
        ((ChainSegmentWords * (endSegment - firstSegment)) + (m_chainTable.SegmentFirst[endSegment] - m_chainTable.SegmentFirst[firstSegment]));

    // Every directory segment's chains in order (SegmentFirst[s] .. SegmentFirst[s + 1]), and the program's set.
    private sealed record ChainTable(SdfChain[] Chains, int[] SegmentFirst, SdfSpecializationSet Set) {
        // The vectors the packed table spans: its header, its segment entries and its identity list.
        public int Vectors => ChainTableVectorsOf(chains: Chains.Length, segments: (SegmentFirst.Length - 1));

        public static int EntryVectors(int segments) => ((segments + (ChainSegmentsPerVector - 1)) / ChainSegmentsPerVector);
    }
    // Compares chain keys (SdfChain.WriteKey) by content, so a key built in scratch finds its chain without allocating.
    private sealed class ChainKeyComparer : IEqualityComparer<uint[]>, IAlternateEqualityComparer<ReadOnlySpan<uint>, uint[]> {
        public static ChainKeyComparer Instance { get; } = new();

        public uint[] Create(ReadOnlySpan<uint> alternate) => alternate.ToArray();
        public bool Equals(uint[]? x, uint[]? y) => ((x is null) ? (y is null) : ((y is not null) && x.AsSpan().SequenceEqual(other: y)));
        public bool Equals(ReadOnlySpan<uint> alternate, uint[] other) => alternate.SequenceEqual(other: other);
        public int GetHashCode(uint[] obj) => GetHashCode(alternate: obj);
        public int GetHashCode(ReadOnlySpan<uint> alternate) {
            var hash = new HashCode();

            hash.AddBytes(value: MemoryMarshal.AsBytes(span: alternate));

            return hash.ToHashCode();
        }
    }
}
