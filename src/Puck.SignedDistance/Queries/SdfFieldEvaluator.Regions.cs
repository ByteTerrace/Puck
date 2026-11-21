using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Puck.Abstractions.Counting;
using Puck.Maths;

namespace Puck.SignedDistance.Queries;

// THE REGION INDEX of a regional evaluator: for each cube of the world a point query has touched, its tape, the ranges
// of the stream a walk at any point of the cube has to read. A point walk reads its region's tape in place of the whole
// stream and answers the same distance and material, to the bit.
//
// A tape is the record of one regional box walk over the region's box (WalkBounds with a record): every range of the
// stream that walk neither jumped over nor found inert. The walk holds the interval that encloses, at each place in the
// stream, the accumulator a point walk holds there at every point of the box (the interval interpreter's inclusion,
// SdfFieldEvaluator.Bounds.cs). Against that interval it drops three things, each of which leaves every such point
// walk's distance and material as they were:
//
// - An instance or a chain a point certificate clears at every point of the box (Reach.RegionClears): the point walk's
//   own skip (SdfFieldEvaluator.Cull.cs), asked at the box's nearest point to the certificate's centre against the
//   interval's upper end, which is at least any point's accumulator. The primer's clearance is held one raw past the
//   primer's upper end, since an instance it skips may stand before the primer in the stream, where an equal candidate
//   would keep its own material.
// - A chain whose every join the walk found inert (RegionJoinClears): the join's candidate, enclosed over the box by the
//   interval rules, stands past the accumulator's upper end by the blend's identity margin. This needs no certificate,
//   so it reaches the shapes and folds that have none.
// - Everything before a chain whose join takes the root accumulator over (RegionJoinClears, the other way): the
//   accumulator's lower end stands past the candidate by the same margin, so the accumulator after the join is the
//   candidate at every point of the box, whatever the stream read before. The first two are skips at a chain's own
//   place in the stream, which keep every chain that stands before the nearest geometry in stream order; this is the
//   one that drops those, as a program emitted in rows needs.
//
// A jump lands on a chain's or an instance's first instruction and an inert chain is dropped whole, so a tape holds
// whole chains and a point walk's root frame finds its prefix on whichever chain it reads first. A point walk over a
// tape still makes its own skips at its own point.
//
// Regions are cubes of a power-of-two edge. A root region is recorded over the whole stream. A child, half its parent's
// edge, is recorded over its parent's tape: what the parent dropped is dropped for every point of the child. A region
// divides until its tape is short or its edge is the least. A region is immutable once published and is published by
// compare and exchange, so walks on any thread share the index; two threads may record one region twice, to the same
// tape. The index holds at most RegionLimit regions and drops them all on reaching it.
public sealed partial class SdfFieldEvaluator {
    // A root region's edge, 2⁴ units, and the least edge, 2¹ units, as shifts of a raw coordinate. A root is recorded
    // over the whole stream, so its edge sets what a first touch costs: at this edge the walk visits what stands within
    // a few tens of units of the box.
    private const int RegionRootShift = (FixedQ4816.FractionBitCount + 4);
    private const int RegionLeafShift = (FixedQ4816.FractionBitCount + 1);
    // A region whose tape holds no more instructions than this does not divide.
    private const int RegionShortTape = 32;
    private const int RegionLimit = (1 << 14);

    // A tape that holds nothing: one empty range, since a walk reads an empty span as the whole stream.
    private static readonly int[] EmptyTape = [0, 0];

    [ThreadStatic]
    private static List<(int First, int End)>? RegionRecord;

    private readonly RegionIndex? m_regions;

    /// <summary>Gets the kind counting regions recorded, a region recorded twice by a race counted twice.</summary>
    public static WorkKind RegionsBuilt { get; } = new(name: "sdf.regions.built", unit: "count", workClass: WorkClass.Pacing);
    /// <summary>Gets the kind counting the instructions the box walks visited to record regions.</summary>
    public static WorkKind RegionBuildInstructions { get; } = new(name: "sdf.regions.build-instructions", unit: "count", workClass: WorkClass.Pacing);
    /// <summary>Gets the kind counting the instructions the recorded regions' tapes hold.</summary>
    public static WorkKind RegionTapeInstructions { get; } = new(name: "sdf.regions.tape-instructions", unit: "count", workClass: WorkClass.Pacing);
    /// <summary>Gets the kind counting the times an index reached its limit and dropped its regions.</summary>
    public static WorkKind RegionResets { get; } = new(name: "sdf.regions.resets", unit: "count", workClass: WorkClass.Pacing);
    /// <summary>Gets the process-wide work of every regional evaluator's index. A monotonic total; a reader takes a
    /// window by reading twice. The point walks over the tapes count in <see cref="PointWork"/>.</summary>
    public static WorkCounterSet RegionWork { get; } = new(name: "sdf.regions", kinds: [RegionsBuilt, RegionBuildInstructions, RegionTapeInstructions, RegionResets]);

    /// <summary>Gets whether the evaluator indexes its program by region: a point query walks the chains that can
    /// change the field in the region its point lies in, in place of the whole program, to the same answer.</summary>
    public bool IndexesRegions => (m_regions is not null);

    // Whether a walk has an instruction to read: inside the range it stands in, or in a later range of its tape, entered
    // no earlier than a skip has already carried the walk. A walk with no tape ends with its one range.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool NextLive(ref int index, ref int end, ReadOnlySpan<int> tape, ref int at) {
        while (index >= end) {
            if (at >= tape.Length) {
                return false;
            }

            index = Math.Max(val1: index, val2: tape[at]);
            end = tape[(at + 1)];
            at += 2;
        }

        return true;
    }

    // The tape of the region a point of the frame lies in, recording the regions on the way to it that no walk has.
    private ReadOnlySpan<int> RegionTape(FixedVector3 point) {
        var regions = m_regions!;
        var (x, y, z) = (point.X.Value, point.Y.Value, point.Z.Value);
        var shift = RegionRootShift;
        var key = new RegionKey(X: (x >> shift), Y: (y >> shift), Z: (z >> shift));

        if (!regions.Roots.TryGetValue(key: key, value: out var node)) {
            node = regions.Roots.GetOrAdd(key: key, value: RecordRegion(parent: null, shift: shift, x: key.X, y: key.Y, z: key.Z));
            regions.Admit();
        }

        while ((shift > RegionLeafShift) && (node.Instructions > RegionShortTape)) {
            shift--;

            var children = (node.Children ?? (Interlocked.CompareExchange(comparand: null, location1: ref node.Children, value: new RegionNode?[8]) ?? node.Children!));
            var slot = ((int)((((x >> shift) & 1L) | (((y >> shift) & 1L) << 1)) | (((z >> shift) & 1L) << 2)));
            var child = Volatile.Read(location: ref children[slot]);

            if (child is null) {
                var recorded = RecordRegion(parent: node, shift: shift, x: (x >> shift), y: (y >> shift), z: (z >> shift));

                child = (Interlocked.CompareExchange(comparand: null, location1: ref children[slot], value: recorded) ?? recorded);
                regions.Admit();
            }

            node = child;
        }

        return node.Tape;
    }
    // Records one region: the culling box walk over its box, over the whole stream for a root and over the parent's tape
    // for a child, with the primer chosen among the instances that walk can meet.
    private RegionNode RecordRegion(RegionNode? parent, int shift, long x, long y, long z) {
        var cullBounds = m_cullBounds;
        var query = new BoxQuery(box: new IntervalVector3(
            X: RegionSide(cell: x, frame: m_frameRaw, shift: shift),
            Y: RegionSide(cell: y, frame: m_frameRaw, shift: shift),
            Z: RegionSide(cell: z, frame: m_frameRaw, shift: shift)
        ));
        var reaches = (((BoxReachScratch is { } scratch) && (scratch.Length >= cullBounds.Length))
            ? scratch
            : (BoxReachScratch = CreateReachScratch(length: cullBounds.Length, work: SdfBoundsCache.ProcessWork, arrays: SdfBoundsCache.ScratchArraysCreated, bytes: SdfBoundsCache.ScratchPayloadCreated)));
        var primer = BoxPrimer.None;
        var record = (RegionRecord ??= []);
        var walked = 0;
        var expanded = 0;

        record.Clear();

        if (parent is null) {
            for (var bound = 0; (bound < cullBounds.Length); bound++) {
                RankBoxReach(bound: bound, primer: ref primer, query: in query, reaches: reaches, regional: true);
            }
        } else {
            foreach (var bound in parent.Bounds) {
                RankBoxReach(bound: bound, primer: ref primer, query: in query, reaches: reaches, regional: true);
            }
        }

        _ = WalkBounds(
            cull: true,
            end: m_instructions.Length,
            first: 0,
            primer: ref primer,
            query: in query,
            reaches: reaches.AsSpan(start: 0, length: cullBounds.Length),
            record: record,
            rotationsExpanded: ref expanded,
            tape: ((parent is null) ? default : parent.Tape.AsSpan()),
            walked: ref walked,
            world: query.Box
        );

        var tape = TapeOf(instructions: out var instructions, length: m_instructions.Length, record: record, within: parent?.Tape);

        RegionWork.Count(kind: RegionsBuilt);
        RegionWork.Add(amount: walked, kind: RegionBuildInstructions);
        RegionWork.Add(amount: instructions, kind: RegionTapeInstructions);

        return new RegionNode(bounds: BoundsIn(candidates: parent?.Bounds, tape: tape), instructions: instructions, tape: tape);
    }
    // One side of a region's box: the cell's raws, less what lies outside the frame, whose points no walk reads.
    private static FixedInterval RegionSide(long cell, int shift, long frame) {
        var lower = (cell << shift);

        return new FixedInterval(
            lower: FixedQ4816.FromRawBits(value: Math.Max(val1: lower, val2: -frame)),
            upper: FixedQ4816.FromRawBits(value: Math.Min(val1: (lower + ((1L << shift) - 1L)), val2: frame))
        );
    }
    // A walk's record as a tape: its ranges in stream order, joined where they meet or overlap (the primer's own walk
    // records its instance ahead of its place), held inside the stream and, for a child, inside its parent's tape.
    private static int[] TapeOf(List<(int First, int End)> record, int[]? within, int length, out int instructions) {
        record.Sort(comparison: static (left, right) => left.First.CompareTo(value: right.First));

        var joined = 0;

        for (var read = 0; (read < record.Count); read++) {
            var (first, end) = record[read];
            var last = Math.Min(val1: end, val2: length);

            if (last <= first) {
                continue;
            }

            if ((joined > 0) && (first <= record[(joined - 1)].End)) {
                record[(joined - 1)] = (record[(joined - 1)].First, Math.Max(val1: record[(joined - 1)].End, val2: last));
            } else {
                record[joined++] = (first, last);
            }
        }

        var tape = new List<int>(capacity: (2 * joined));
        var at = 0;

        instructions = 0;

        for (var index = 0; (index < joined); index++) {
            var (first, end) = record[index];

            if (within is null) {
                tape.Add(item: first);
                tape.Add(item: end);
                instructions += (end - first);

                continue;
            }

            while ((at < within.Length) && (within[(at + 1)] <= first)) {
                at += 2;
            }

            for (var held = at; ((held < within.Length) && (within[held] < end)); held += 2) {
                var from = Math.Max(val1: first, val2: within[held]);
                var to = Math.Min(val1: end, val2: within[(held + 1)]);

                if (from < to) {
                    tape.Add(item: from);
                    tape.Add(item: to);
                    instructions += (to - from);
                }
            }
        }

        return ((tape.Count == 0) ? EmptyTape : [.. tape]);
    }
    // The cullable instances whose first instruction a tape holds, of a parent's or of them all, in First order: the
    // instances a walk over the tape can test.
    private int[] BoundsIn(int[] tape, int[]? candidates) {
        var cullBounds = m_cullBounds;
        var held = new List<int>();
        var at = 0;

        for (var index = 0; (index < (candidates?.Length ?? cullBounds.Length)); index++) {
            var bound = ((candidates is null) ? index : candidates[index]);
            var first = cullBounds[bound].First;

            while ((at < tape.Length) && (tape[(at + 1)] <= first)) {
                at += 2;
            }

            if ((at < tape.Length) && (tape[at] <= first)) {
                held.Add(item: bound);
            }
        }

        return [.. held];
    }

    // The regions a regional evaluator has recorded: its root regions by cell, each the head of its own octree.
    private sealed class RegionIndex {
        public readonly ConcurrentDictionary<RegionKey, RegionNode> Roots = new();

        private int m_count;

        // Counts one region recorded, and drops every region when the index has reached its limit. A walk that holds a
        // dropped region reads it to its end; the next walk records afresh.
        public void Admit() {
            if (Interlocked.Increment(location: ref m_count) > RegionLimit) {
                Roots.Clear();
                Volatile.Write(location: ref m_count, value: 0);
                RegionWork.Count(kind: RegionResets);
            }
        }
    }
    // One region: its tape as pairs of a range's first instruction and the one after its last, the cullable instances
    // the tape holds, the instructions it holds, and its eight children by the low bit of each axis's cell, each null
    // until a walk reaches it.
    private sealed class RegionNode(int[] tape, int[] bounds, int instructions) {
        public readonly int[] Bounds = bounds;
        public readonly int Instructions = instructions;
        public readonly int[] Tape = tape;
        public RegionNode?[]? Children;
    }
    // A root region's cell on each axis: the raw coordinate shifted down by RegionRootShift.
    private readonly record struct RegionKey(long X, long Y, long Z);
}
