using System.Buffers;
using System.Numerics;
using Puck.SignedDistance;
using Puck.SignedDistance.Queries;
using BakeGate = Puck.SdfVm.SdfMeshBakeReport.Gate;

namespace Puck.SdfVm;

// The grid lattices' blocks the surface cannot reach, proved before the lattice is sampled: a coarse search's lattice
// (Refine) and a final lattice (its slabs). The lattice is cut into blocks of BoundBlock samples a side (the last block
// of an axis ends on the lattice's last sample, overlapping its neighbour), and the group's own field (SdfMeshAudit.Field,
// the render reading the audit measures a mesh against) encloses every value its fixed-point evaluator answers over a box
// of blocks (SdfFieldEvaluator.TryDistanceBounds), splitting the box where the enclosure straddles the band, down to one
// block. A block whose enclosure lies wholly past the band, outside or inside, is never sampled: the host writes each of
// its samples the enclosure's nearer end, which keeps the sample's sign and holds it at least the band from zero. Every
// other block is sampled as a block lattice (SdfMeshBakeRequest.Blocks).
//
// Invariant: no lattice cell with a corner in a skipped block holds a zero, and every skipped sample reads at least the
// band from zero with its true sign. Every reader of a lattice judges a sample near the surface only by a small value or
// a sign change across a cell (Refine's narrowing, SdfSampledLattice's crossed cells, rings, sheet and gap tests), so it
// judges the lattice exactly as it judges the fully sampled one. Enforced by the one function that classifies a box
// (Classify): its only skip is an enclosure that excludes [-band, band] over the block's samples widened by one cell on
// every side and padded past every float position the kernel computes. Where it stops: the enclosure is of the CPU
// evaluator's field, so a group whose GPU field the CPU cannot read (glyph thickening, a sampled snapshot, slot lanes, an
// op the render reading refuses) samples every block, and the band absorbs the two readers' rounding apart.
public sealed partial class SdfMeshPathBake {
    // A block's edge, in samples.
    private const int BoundBlock = 4;
    // A lattice of fewer samples is sampled whole: its bounds would cost more walks than the samples they save.
    private const int BoundLeastSamples = 4096;
    // The band, in cell diagonals, a sampled block's samples may lie within of the surface: the crossed cells' corners lie
    // within one diagonal of a zero, and their ring, sheet and gap tests read one more cell about them.
    private const float BoundBandDiagonals = 3f;
    // The resources a group's GPU field reads that its CPU field cannot see.
    private const SdfFieldResource BoundUnseen = (SdfFieldResource.GlyphAtlas | SdfFieldResource.SampledHandle | SdfFieldResource.SlotLanes);

    // A group's CPU field, or none where it cannot be read, as CpuField built it.
    internal sealed record CpuFieldHold(SdfFieldEvaluator? Evaluator);

    // A grid lattice's blocks and what each takes: NaN for a sampled block, else the value every sample of it takes.
    internal sealed class LatticeBlocks {
        public LatticeBlocks((int X, int Y, int Z) dims) {
            Size = (Math.Min(val1: BoundBlock, val2: dims.X), Math.Min(val1: BoundBlock, val2: dims.Y), Math.Min(val1: BoundBlock, val2: dims.Z));
            Count = (Blocks(samples: dims.X), Blocks(samples: dims.Y), Blocks(samples: dims.Z));
            Dims = dims;
            Fill = new float[(Count.X * Count.Y) * Count.Z];
        }

        public (int X, int Y, int Z) Dims { get; }
        public (int X, int Y, int Z) Size { get; }
        public (int X, int Y, int Z) Count { get; }
        public float[] Fill { get; }
        public int Sampled { get; set; }
        public int PerBlock => ((Size.X * Size.Y) * Size.Z);
        // The samples no block samples, counted once however many blocks overlap them.
        public long Skipped => Math.Max(val1: 0L, val2: ((((long)Dims.X * Dims.Y) * Dims.Z) - ((long)Sampled * PerBlock)));

        private static int Blocks(int samples) => ((samples + BoundBlock - 1) / BoundBlock);

        public int Index(int x, int y, int z) => (x + (Count.X * (y + (Count.Y * z))));

        // A block's low corner along an axis: its multiple of the block edge, the last pulled back to end on the last sample.
        public static int Corner(int block, int size, int samples) => Math.Min(val1: (block * BoundBlock), val2: (samples - size));

        public Vector3 CornerOf(int x, int y, int z) => new(
            x: Corner(block: x, samples: Dims.X, size: Size.X),
            y: Corner(block: y, samples: Dims.Y, size: Size.Y),
            z: Corner(block: z, samples: Dims.Z, size: Size.Z)
        );

        public int SampledIn(int layer) {
            var sampled = 0;

            for (var y = 0; (y < Count.Y); y++) {
                for (var x = 0; (x < Count.X); x++) {
                    if (float.IsNaN(f: Fill[Index(x: x, y: y, z: layer)])) { sampled++; }
                }
            }
            return sampled;
        }

        // The block lattice of `lattice` sampling the sampled blocks of layers [first, end), or of every layer.
        public SdfMeshBakeRequest Request(in SdfMeshBakeRequest lattice, int first, int end) {
            var rows = new Vector4[Enumerable.Range(start: first, count: (end - first)).Sum(selector: SampledIn)];
            var at = 0;

            for (var z = first; (z < end); z++) {
                for (var y = 0; (y < Count.Y); y++) {
                    for (var x = 0; (x < Count.X); x++) {
                        if (float.IsNaN(f: Fill[Index(x: x, y: y, z: z)])) { rows[at++] = new Vector4(value: CornerOf(x: x, y: y, z: z), w: 0f); }
                    }
                }
            }

            return lattice with { Dims = (Size.X, Size.Y, (Size.Z * rows.Length)), Blocks = rows, BlockFill = this };
        }

        // Writes a block lattice's answer into the whole lattice's values (and materials, when given): first the skipped
        // blocks of layers [first, end), each sample its block's fill and material zero, then every sampled block's samples
        // over them, so a sample two blocks share takes its measure.
        public void Expand(in SdfMeshBakeRequest request, ReadOnlySpan<int> words, int first, int end, Span<float> values, Span<int> materials) {
            var (dx, dy, dz) = Dims;

            for (var bz = first; (bz < end); bz++) {
                var cz = Corner(block: bz, samples: dz, size: Size.Z);

                for (var by = 0; (by < Count.Y); by++) {
                    var cy = Corner(block: by, samples: dy, size: Size.Y);

                    for (var bx = 0; (bx < Count.X); bx++) {
                        var fill = Fill[Index(x: bx, y: by, z: bz)];

                        if (float.IsNaN(f: fill)) { continue; }

                        var cx = Corner(block: bx, samples: dx, size: Size.X);

                        for (var z = 0; (z < Size.Z); z++) {
                            for (var y = 0; (y < Size.Y); y++) {
                                var row = (cx + (dx * ((cy + y) + (dy * (cz + z)))));

                                values.Slice(start: row, length: Size.X).Fill(value: fill);
                                if (!materials.IsEmpty) { materials.Slice(start: row, length: Size.X).Clear(); }
                            }
                        }
                    }
                }
            }

            var count = request.Samples;
            var corners = request.Blocks.Span;

            for (var local = 0; (local < count); local++) {
                var (block, x, y, z) = request.BlockSample(local: local);
                var corner = corners[block];
                var index = (((int)corner.X + x) + (dx * (((int)corner.Y + y) + (dy * ((int)corner.Z + z)))));

                values[index] = BitConverter.Int32BitsToSingle(value: words[local]);
                if (!materials.IsEmpty) { materials[index] = words[count + local]; }
            }
        }
    }

    // Whether a group's GPU field is one its CPU field reads, so its bounds may stand in for samples.
    private static bool Boundable(Group group) => ((group.GlyphCell <= 0f) && !group.Geometry.ContainsGlyph && (group.SampledSnapshots.Length == 0));

    // In the take, under the gate: hands a group's grid lattice to the pool to be bounded before it is sampled, once: a
    // coarse search's lattice, or a final lattice before its first slab. True when the group is held by that job (or
    // retired), so the take passes it by.
    private bool QueueBounds(Group group, in SdfMeshBakeRequest next) {
        var final = ((next.Slab >= 0) ? group.Final : null);

        if (final is not null) { final.Bounded = true; }

        var lattice = (final?.Lattice ?? next);

        if ((lattice.Samples < BoundLeastSamples) || !Boundable(group: group) || (m_program is null)) {
            if (final is null) { group.Next = (next with { Bounded = true }); }
            return false;
        }
        if (!group.QueueWork()) { return true; }

        var field = Volatile.Read(location: ref m_field);
        var request = next;

        group.Contouring = true;
        Enqueue(group: group, urgent: true, work: () => Bound(field: field, final: final, group: group, request: request));
        return true;
    }

    // The group's CPU field for the bake's field version, built once and kept on the group for its later stages; null when
    // the field reads what the CPU cannot see or the evaluator refuses it. On the pool, holding the group.
    private static SdfFieldEvaluator? CpuField(Group group, SdfProgram program, long field) {
        if ((group.BoundField is { } held) && (group.BoundFieldVersion == field)) { return held.Evaluator; }

        var excerpt = SdfMeshAudit.Field(group: group, program: program);
        SdfFieldEvaluator? evaluator = null;

        if (!excerpt.Instructions.Any(predicate: static instruction => ((SdfFieldOps.ResourcesOf(value: instruction.Op) & BoundUnseen) != 0))) {
            _ = SdfMeshAudit.TryEvaluator(field: out evaluator, program: excerpt, refusal: out _);
        }
        group.BoundField = new(Evaluator: evaluator);
        group.BoundFieldVersion = field;
        return evaluator;
    }

    // Splits a box of cells [low, high) by halving its longest run until `settles` settles each part; a part of one cell is
    // asked with `last` set and must settle.
    private static void Descend((int X, int Y, int Z) low, (int X, int Y, int Z) high, Func<(int X, int Y, int Z), (int X, int Y, int Z), bool, bool> settles) {
        var (sx, sy, sz) = ((high.X - low.X), (high.Y - low.Y), (high.Z - low.Z));

        if (settles(low, high, ((sx == 1) && (sy == 1) && (sz == 1)))) { return; }
        if ((sx >= sy) && (sx >= sz)) {
            var middle = (low.X + (sx / 2));
            Descend(high: (middle, high.Y, high.Z), low: low, settles: settles);
            Descend(high: high, low: (middle, low.Y, low.Z), settles: settles);
        } else if (sy >= sz) {
            var middle = (low.Y + (sy / 2));
            Descend(high: (high.X, middle, high.Z), low: low, settles: settles);
            Descend(high: high, low: (low.X, middle, low.Z), settles: settles);
        } else {
            var middle = (low.Z + (sz / 2));
            Descend(high: (high.X, high.Y, middle), low: low, settles: settles);
            Descend(high: high, low: (low.X, low.Y, middle), settles: settles);
        }
    }

    // On the pool, holding the group: classifies the lattice's blocks and gives the group the lattice it samples next (the
    // coarse lattice's blocks, or the final lattice's first slab), or finishes the group as empty when no block can hold the
    // surface. The program is read under a claim of the job's own, and only while the bake still meshes the field the job
    // was queued for; a field adopted since leaves the lattice unbounded, as a moved field does below.
    private void Bound(Group group, FinalLattice? final, in SdfMeshBakeRequest request, long field) {
        SdfFieldEvaluator? evaluator = null;

        if ((group.BoundField is { } held) && (group.BoundFieldVersion == field)) {
            evaluator = held.Evaluator;
        } else if (ClaimProgram(field: field) is { } program) {
            try { evaluator = CpuField(field: field, group: group, program: program); } finally { program.Release(); }
        }
        var walks = 0L;
        var blocks = ((evaluator is null) ? null : Classify(evaluator: evaluator, lattice: (final?.Lattice ?? request), walks: out walks));
        var empty = (blocks is { Sampled: 0 });

        if (empty) { StoreEmpty(group); }

        FinalLattice? emptied = null;

        using (EnterGate(site: BakeGate.Slab)) {
            group.Contouring = false;
            if (!GroupList.Holds(group: group) || !ReferenceEquals(objA: group.Final, objB: final)) { return; }
            m_accounts.BoundWalks += walks;
            // A field adopted since the bounds' program was read may have moved the surface: the lattice samples whole.
            var bounded = ((blocks is not null) && (Volatile.Read(location: ref m_field) == field));

            if (bounded) {
                m_accounts.BoundLattices++;
                m_accounts.BoundSkipped += blocks!.Skipped;
            } else {
                m_accounts.BoundRefused++;
            }
            if (final is not null) {
                if (bounded && empty) {
                    emptied = EndEmptyFinal(group: group);
                } else {
                    final.Blocks = (bounded ? blocks : null);
                    group.Next = SlabOf(group: group);
                }
            } else if (bounded && empty) {
                group.FoundNoSurface(how: $" after {group.Refinements} refinements, by its bounds");
                group.Next = null;
                group.Done = true;
                group.Mesh = null;
                Finish();
            } else {
                group.Next = (bounded ? blocks!.Request(end: blocks.Count.Z, first: 0, lattice: in request) : request) with { Bounded = true };
            }
        }
        emptied?.Dispose();
    }

    // The lattice's blocks, each sampled or skipped with its fill.
    private static LatticeBlocks Classify(SdfFieldEvaluator evaluator, in SdfMeshBakeRequest lattice, out long walks) {
        var blocks = new LatticeBlocks(dims: lattice.Dims);
        var (origin, cell) = (lattice.Origin, lattice.Cell);
        var band = (BoundBandDiagonals * cell.Length());
        // Past every float position the kernel computes for a sample in the box (origin + index · cell, two roundings).
        var pad = new Vector3(value: ((1.0e-5f * (origin.Length() + (cell.Length() * Math.Max(val1: lattice.Dims.X, val2: Math.Max(val1: lattice.Dims.Y, val2: lattice.Dims.Z))))) + (2f / 65536f)));
        var size = new Vector3(x: blocks.Size.X, y: blocks.Size.Y, z: blocks.Size.Z);
        var count = 0L;

        Descend(high: blocks.Count, low: (0, 0, 0), settles: (from, to, last) => {
            // One cell past its blocks' samples on every side, so each lattice cell with a corner in a skipped block lies
            // in that block's box: a zero between two skipped blocks' facing samples is in both boxes.
            var low = ((origin + ((blocks.CornerOf(x: from.X, y: from.Y, z: from.Z) - Vector3.One) * cell)) - pad);
            var high = ((origin + ((blocks.CornerOf(x: (to.X - 1), y: (to.Y - 1), z: (to.Z - 1)) + size) * cell)) + pad);
            var fill = float.NaN;

            count++;
            if (evaluator.TryDistanceBounds(lower: SdfMeshAudit.Fixed(point: low), upper: SdfMeshAudit.Fixed(point: high), distance: out var bounds) && !bounds.IsUnbounded) {
                var lower = ((float)((double)bounds.Lower));
                var upper = ((float)((double)bounds.Upper));

                if (lower > band) {
                    fill = lower;
                } else if (upper < -band) {
                    fill = upper;
                }
            }
            if (float.IsNaN(f: fill) && !last) { return false; }
            for (var z = from.Z; (z < to.Z); z++) {
                for (var y = from.Y; (y < to.Y); y++) {
                    for (var x = from.X; (x < to.X); x++) {
                        blocks.Fill[blocks.Index(x: x, y: y, z: z)] = fill;
                    }
                }
            }
            if (float.IsNaN(f: fill)) { blocks.Sampled++; }
            return true;
        });
        walks = count;
        return blocks;
    }


    // The next slab of a bounded final lattice: whole layers of blocks from the plane its last slab ended at, the sampled
    // blocks of as many layers as LatticeTaps takes (at least one sampled block), and every later layer when none of them
    // samples a block.
    private static SdfMeshBakeRequest BlockSlabOf(FinalLattice final, LatticeBlocks blocks) {
        var first = (final.Planes / BoundBlock);
        var end = first;
        var sampled = 0;

        while (end < blocks.Count.Z) {
            var layer = blocks.SampledIn(layer: end);

            if ((sampled > 0) && (((long)(sampled + layer) * blocks.PerBlock) > LatticeTaps)) { break; }
            sampled += layer;
            end++;
        }

        var rest = end;

        while ((rest < blocks.Count.Z) && (blocks.SampledIn(layer: rest) == 0)) { rest++; }
        if (rest == blocks.Count.Z) { end = rest; }

        return blocks.Request(end: end, first: first, lattice: final.Lattice) with {
            Slab = (first * BoundBlock),
            BlockPlanes = (Math.Min(val1: (end * BoundBlock), val2: blocks.Dims.Z) - (first * BoundBlock)),
        };
    }

    // Copies a block slab's answer into its final lattice's layers.
    private static void DecodeBlocks(FinalLattice final, in SdfMeshBakeRequest request, ReadOnlySpan<int> words) {
        var blocks = request.BlockFill!;

        blocks.Expand(
            end: Math.Min(val1: blocks.Count.Z, val2: ((request.Slab + request.SlabPlanes + BoundBlock - 1) / BoundBlock)),
            first: (request.Slab / BoundBlock),
            materials: final.Materials,
            request: in request,
            values: final.Values,
            words: words
        );
    }

    // Refines a group from a coarse block lattice's answer, expanded into the whole lattice's values.
    private void RefineBlocks(Group group, in SdfMeshBakeRequest request, ReadOnlySpan<float> answered) {
        var blocks = request.BlockFill!;
        var whole = (request with { Dims = blocks.Dims, Blocks = default, BlockFill = null });
        var values = ArrayPool<float>.Shared.Rent(minimumLength: whole.Samples);

        try {
            blocks.Expand(end: blocks.Count.Z, first: 0, materials: default, request: in request, values: values, words: System.Runtime.InteropServices.MemoryMarshal.Cast<float, int>(answered));
            Refine(dispatched: request.Samples, group: group, request: in whole, values: values.AsSpan(start: 0, length: whole.Samples));
        } finally {
            ArrayPool<float>.Shared.Return(array: values);
        }
    }

    // Under the gate: ends a group whose final lattice holds no crossed cell, handing back the lattice for its caller to
    // dispose off the gate.
    private FinalLattice? EndEmptyFinal(Group group) {
        var emptied = group.Final;

        group.Outcome = "no crossed cell in the final lattice";
        group.Final = null;
        group.Next = null;
        group.Done = true;
        group.Mesh = null;
        Finish();
        return emptied;
    }
}
