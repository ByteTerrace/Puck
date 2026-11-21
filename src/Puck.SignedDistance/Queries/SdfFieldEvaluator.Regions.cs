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
// - An inert join (RegionJoinClears): the join's candidate, enclosed over the box by the interval rules, stands past the
//   accumulator's upper end by the blend's identity margin, so the join leaves every point's distance and material as
//   they were. This needs no certificate, so it reaches the shapes and folds that have none. A chain whose every join
//   is inert is dropped whole, its point ops with it; in a chain that goes on to a join that is not, the inert joins
//   before that one are dropped singly and the chain's point ops stay.
// - Everything before a chain whose first join that is not inert takes the root accumulator over (RegionJoinClears, the
//   other way): the candidate stands below the accumulator's lower end, and below the far seed a walk starts from, by
//   the same margin. A walk that enters the stream at that chain's ResetPoint reaches the join holding the far seed,
//   since the chain's earlier joins are dropped, and leaves it holding the candidate and its material, as the whole
//   stream's walk does. The rule holds only for that first join, at the root's depth: a chain that has joined by
//   another blend, edited the field or crossed a scope holds what a walk from its ResetPoint does not. The first two
//   drops are skips at a chain's own place in the stream, which keep every chain that stands before the nearest
//   geometry in stream order; this is the one that drops those, as a program emitted in rows needs.
//
// A jump lands on a chain's or an instance's first instruction, and a chain keeps its ResetPoint and point ops unless
// it is dropped whole, so a point walk's root frame finds its prefix on whichever chain it reads first. A point walk
// over a tape still makes its own skips at its own point.
//
// Regions are cubes of a power-of-two edge. A root region is recorded over the whole stream. A child, half its parent's
// edge, is recorded over its parent's tape: what the parent dropped is dropped for every point of the child. A region
// divides until its tape is short or its edge is the least. A region is recorded by the second walk that reaches it;
// the first marks it and reads the region around it, so scattered points record nothing. A region is immutable once
// published and is published by compare and exchange, so walks on any thread share the index; two threads may record
// one region twice, to the same tape. The index holds at most RegionLimit marks and regions and drops them all on
// reaching it.
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

    // The tape of the smallest recorded region a point of the frame lies in, or none when no walk has read the point's
    // root region before. A region is recorded on the second walk that reaches it and only marked on the first, which
    // reads the region around it (the whole stream, for a root): a point no later walk comes near costs one walk, and
    // an index that drops its regions loses only those walks came back to.
    private bool TryRegionTape(FixedVector3 point, out ReadOnlySpan<int> tape) {
        var regions = m_regions!;
        var (x, y, z) = (point.X.Value, point.Y.Value, point.Z.Value);
        var shift = RegionRootShift;
        var key = new RegionKey(X: (x >> shift), Y: (y >> shift), Z: (z >> shift));

        tape = default;

        if (!regions.Roots.TryGetValue(key: key, value: out var node)) {
            if (regions.Roots.TryAdd(key: key, value: RegionNode.Reached)) {
                regions.Admit();
            }

            return false;
        }

        if (ReferenceEquals(objA: node, objB: RegionNode.Reached)) {
            node = RecordRegion(parent: null, shift: shift, x: key.X, y: key.Y, z: key.Z);
            // A walk that loses the race, or finds the index dropped, reads the tape it recorded itself.
            _ = regions.Roots.TryUpdate(comparisonValue: RegionNode.Reached, key: key, newValue: node);
        }

        while ((shift > RegionLeafShift) && (node.Instructions > RegionShortTape)) {
            shift--;

            var children = (node.Children ?? (Interlocked.CompareExchange(comparand: null, location1: ref node.Children, value: new RegionNode?[8]) ?? node.Children!));
            var slot = ((int)((((x >> shift) & 1L) | (((y >> shift) & 1L) << 1)) | (((z >> shift) & 1L) << 2)));
            var child = (Volatile.Read(location: ref children[slot]) ?? Interlocked.CompareExchange(comparand: null, location1: ref children[slot], value: RegionNode.Reached));

            if (child is null) {
                break;
            }

            if (ReferenceEquals(objA: child, objB: RegionNode.Reached)) {
                var recorded = RecordRegion(parent: node, shift: shift, x: (x >> shift), y: (y >> shift), z: (z >> shift));
                var held = Interlocked.CompareExchange(comparand: RegionNode.Reached, location1: ref children[slot], value: recorded);

                child = (ReferenceEquals(objA: held, objB: RegionNode.Reached) ? recorded : held!);
                regions.Admit();
            }

            node = child;
        }

        tape = node.Tape;

        return true;
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

        // Only a region that divides hands its instances to a child's record.
        var divides = ((shift > RegionLeafShift) && (instructions > RegionShortTape));

        return new RegionNode(bounds: (divides ? BoundsIn(candidates: parent?.Bounds, tape: tape) : []), instructions: instructions, tape: tape);
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

        // Counts one region marked or recorded, and drops every region when the index has reached its limit. The walk
        // that takes the count from the limit's far side back to zero is the one that drops them, so a count another
        // walk adds meanwhile is kept. A walk that holds a dropped region reads it to its end.
        public void Admit() {
            var count = Interlocked.Increment(location: ref m_count);

            if ((count > RegionLimit) && (Interlocked.CompareExchange(comparand: count, location1: ref m_count, value: 0) == count)) {
                Roots.Clear();
                RegionWork.Count(kind: RegionResets);
            }
        }
    }
    // One region: its tape as pairs of a range's first instruction and the one after its last, the cullable instances
    // the tape holds (of a region that divides), the instructions it holds, and its eight children by the low bit of
    // each axis's cell, each null until a walk reaches it and Reached until a second walk records it.
    private sealed class RegionNode(int[] tape, int[] bounds, int instructions) {
        // The mark of a region one walk has reached and none has recorded.
        public static readonly RegionNode Reached = new(bounds: [], instructions: 0, tape: EmptyTape);

        public readonly int[] Bounds = bounds;
        public readonly int Instructions = instructions;
        public readonly int[] Tape = tape;
        public RegionNode?[]? Children;
    }
    // A root region's cell on each axis: the raw coordinate shifted down by RegionRootShift.
    private readonly record struct RegionKey(long X, long Y, long Z);
}
