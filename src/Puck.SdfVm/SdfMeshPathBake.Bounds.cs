using System.Numerics;
using Puck.Maths;
using Puck.SignedDistance;
using Puck.SignedDistance.Queries;
using BakeGate = Puck.SdfVm.SdfMeshBakeReport.Gate;

namespace Puck.SdfVm;

// The final lattice's blocks the surface cannot reach, proved before any slab is sampled. The lattice is cut into blocks
// of BoundBlock samples a side (the last block of an axis ends on the lattice's last plane, overlapping its neighbour),
// and the group's own field (SdfMeshAudit.Field, the render reading the audit measures a mesh against) encloses every
// value its fixed-point evaluator answers over a box of blocks (SdfFieldEvaluator.TryDistanceBounds), splitting the box
// where the enclosure straddles the band, down to one block. A block whose enclosure lies wholly past the band, outside or
// inside, is never sampled: the host writes each of its samples the enclosure's nearer end, which keeps the sample's sign
// and holds it at least the band from zero, so no reader of the lattice (the crossed cells and their ring, a refinement's
// sheet and gap tests, the crossings and their gradients) takes it for a sample near the surface. Every other block is
// sampled as a block slab (SdfMeshBakeRequest.Blocks).
//
// Invariant: no block holding a sample within the band of the surface is skipped. Enforced by the one function that
// classifies a box (Classify), whose only skip is an enclosure that excludes [-band, band]; the enclosure is certified
// over the box padded past every sample position the kernel computes in float. Where it stops: the enclosure is of the CPU
// evaluator's field, so a group whose GPU field the CPU cannot read (glyph thickening, a sampled snapshot, slot lanes, an
// op the render reading refuses) samples every block, and the band absorbs the two readers' rounding apart.
public sealed partial class SdfMeshPathBake {
    // A block's edge, in samples.
    private const int BoundBlock = 8;
    // A final lattice of fewer samples is sampled whole: its bounds would cost more walks than the samples they save.
    private const int BoundLeastSamples = (8 * BoundBlock * BoundBlock * BoundBlock);
    // The band, in cell diagonals, a sampled block's samples may lie within of the surface: the crossed cells' corners lie
    // within one diagonal of a zero, and their ring, sheet and gap tests read one more cell about them.
    private const float BoundBandDiagonals = 3f;
    // The resources a group's GPU field reads that its CPU field cannot see.
    private const SdfFieldResource BoundUnseen = (SdfFieldResource.GlyphAtlas | SdfFieldResource.SampledHandle | SdfFieldResource.SlotLanes);

    // A group's CPU field, or none where it cannot be read, as CpuField built it.
    internal sealed record CpuFieldHold(SdfFieldEvaluator? Evaluator);

    // The final lattice's blocks and what each takes: NaN for a sampled block, else the value every sample of it takes.
    private sealed class FinalBlocks {
        public FinalBlocks((int X, int Y, int Z) dims) {
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

        private static int Blocks(int samples) => ((samples + BoundBlock - 1) / BoundBlock);

        public int Index(int x, int y, int z) => (x + (Count.X * (y + (Count.Y * z))));

        // A block's low corner along an axis: its multiple of the block edge, the last pulled back to end on the last sample.
        public static int Corner(int block, int size, int samples) => Math.Min(val1: (block * BoundBlock), val2: (samples - size));

        public int SampledIn(int layer) {
            var sampled = 0;

            for (var y = 0; (y < Count.Y); y++) {
                for (var x = 0; (x < Count.X); x++) {
                    if (float.IsNaN(f: Fill[Index(x: x, y: y, z: layer)])) { sampled++; }
                }
            }
            return sampled;
        }
    }

    // Whether a group's GPU field is one its CPU field reads, so its bounds may stand in for samples.
    private static bool Boundable(Group group) => ((group.GlyphCell <= 0f) && !group.Geometry.ContainsGlyph && (group.SampledSnapshots.Length == 0));

    // In the take, under the gate: hands a group's final lattice to the pool to be bounded before its first slab, once.
    // True when the group is held by that job (or retired), so the take passes it by.
    private bool QueueBounds(Group group, FinalLattice final) {
        final.Bounded = true;
        if ((final.Lattice.Samples < BoundLeastSamples) || !Boundable(group: group) || (m_program is not { } program)) { return false; }
        if (!group.QueueWork()) { return true; }

        var field = Volatile.Read(location: ref m_field);

        group.Contouring = true;
        Enqueue(group: group, urgent: true, work: () => Bound(field: field, final: final, group: group, program: program));
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

    // Encloses the field over a box, padded; false where the evaluator refuses the box or answers it unbounded.
    private static bool TryBounds(SdfFieldEvaluator evaluator, Vector3 low, Vector3 high, Vector3 pad, out float lower, out float upper) {
        if (evaluator.TryDistanceBounds(lower: Fixed(point: (low - pad)), upper: Fixed(point: (high + pad)), distance: out var bounds) && !bounds.IsUnbounded) {
            (lower, upper) = (((float)((double)bounds.Lower)), ((float)((double)bounds.Upper)));
            return true;
        }
        (lower, upper) = (float.NegativeInfinity, float.PositiveInfinity);
        return false;
    }

    // On the pool, holding the group: classifies the final lattice's blocks and gives the group its first slab, or finishes
    // it as empty when no block can hold the surface.
    private void Bound(Group group, FinalLattice final, SdfProgram program, long field) {
        var evaluator = CpuField(field: field, group: group, program: program);
        var walks = 0L;
        var blocks = ((evaluator is null) ? null : Classify(evaluator: evaluator, final: final, walks: out walks));
        var empty = (blocks is { Sampled: 0 });

        if (empty) { StoreEmpty(group); }

        FinalLattice? emptied = null;

        using (EnterGate(site: BakeGate.Slab)) {
            group.Contouring = false;
            if (!GroupList.Holds(group: group) || !ReferenceEquals(objA: group.Final, objB: final)) { return; }
            m_accounts.BoundWalks += walks;
            // A field adopted since the bounds' program was read may have moved the surface: the lattice samples whole.
            if ((blocks is not null) && (Volatile.Read(location: ref m_field) == field)) {
                m_accounts.BoundGroups++;
                if (empty) {
                    m_accounts.BoundSkipped += final.Lattice.Samples;
                    emptied = EndEmptyFinal(group: group);
                } else {
                    final.Blocks = blocks;
                    m_accounts.BoundSkipped += Math.Max(val1: 0L, val2: (final.Lattice.Samples - ((long)blocks.Sampled * ((blocks.Size.X * blocks.Size.Y) * blocks.Size.Z))));
                }
            } else {
                m_accounts.BoundRefused++;
            }
            if (emptied is null) { group.Next = SlabOf(group: group); }
        }
        emptied?.Dispose();
    }

    // The final lattice's blocks, each sampled or skipped with its fill.
    private static FinalBlocks Classify(SdfFieldEvaluator evaluator, FinalLattice final, out long walks) {
        var lattice = final.Lattice;
        var blocks = new FinalBlocks(dims: lattice.Dims);
        var band = (BoundBandDiagonals * lattice.Cell.Length());
        // Past every float position the kernel computes for a sample in the box (origin + index · cell, two roundings).
        var pad = new Vector3(value: ((1.0e-5f * (lattice.Origin.Length() + (lattice.Cell.Length() * lattice.Dims.X))) + (2f / 65536f)));
        var count = 0L;

        Vector3 SampleOf(int x, int y, int z) => new(
            x: FinalBlocks.Corner(block: x, samples: blocks.Dims.X, size: blocks.Size.X),
            y: FinalBlocks.Corner(block: y, samples: blocks.Dims.Y, size: blocks.Size.Y),
            z: FinalBlocks.Corner(block: z, samples: blocks.Dims.Z, size: blocks.Size.Z)
        );

        Descend(high: blocks.Count, low: (0, 0, 0), settles: (from, to, last) => {
            // One cell past its blocks' samples on every side, so each lattice cell with a corner in a skipped block lies
            // in that block's box: a zero between two skipped blocks' facing samples is in both boxes.
            var low = (lattice.Origin + ((SampleOf(x: from.X, y: from.Y, z: from.Z) - Vector3.One) * lattice.Cell));
            var high = (lattice.Origin + ((SampleOf(x: (to.X - 1), y: (to.Y - 1), z: (to.Z - 1)) + new Vector3(x: blocks.Size.X, y: blocks.Size.Y, z: blocks.Size.Z)) * lattice.Cell));
            var fill = float.NaN;

            count++;
            if (TryBounds(evaluator: evaluator, high: high, low: low, lower: out var lower, pad: pad, upper: out var upper)) {
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

    private static FixedPosition Fixed(Vector3 point) => FixedPosition.FromLocal(local: FixedVector3.FromVector3(value: point));

    // The next slab of a bounded final lattice: whole layers of blocks from the plane its last slab ended at, the sampled
    // blocks of as many layers as LatticeTaps takes (at least one sampled block), and every later layer when none of them
    // samples a block.
    private static SdfMeshBakeRequest BlockSlabOf(FinalLattice final, FinalBlocks blocks) {
        var perBlock = ((blocks.Size.X * blocks.Size.Y) * blocks.Size.Z);
        var first = (final.Planes / BoundBlock);
        var end = first;
        var sampled = 0;

        while (end < blocks.Count.Z) {
            var layer = blocks.SampledIn(layer: end);

            if ((sampled > 0) && (((long)(sampled + layer) * perBlock) > LatticeTaps)) { break; }
            sampled += layer;
            end++;
        }

        var rest = end;

        while ((rest < blocks.Count.Z) && (blocks.SampledIn(layer: rest) == 0)) { rest++; }
        if (rest == blocks.Count.Z) { end = rest; }

        var rows = new Vector4[sampled];
        var at = 0;

        for (var z = first; (z < end); z++) {
            for (var y = 0; (y < blocks.Count.Y); y++) {
                for (var x = 0; (x < blocks.Count.X); x++) {
                    if (!float.IsNaN(f: blocks.Fill[blocks.Index(x: x, y: y, z: z)])) { continue; }
                    rows[at++] = new Vector4(
                        x: FinalBlocks.Corner(block: x, samples: blocks.Dims.X, size: blocks.Size.X),
                        y: FinalBlocks.Corner(block: y, samples: blocks.Dims.Y, size: blocks.Size.Y),
                        z: FinalBlocks.Corner(block: z, samples: blocks.Dims.Z, size: blocks.Size.Z),
                        w: 0f
                    );
                }
            }
        }

        return final.Lattice with {
            Dims = (blocks.Size.X, blocks.Size.Y, (blocks.Size.Z * sampled)),
            Slab = (first * BoundBlock),
            BlockPlanes = (Math.Min(val1: (end * BoundBlock), val2: blocks.Dims.Z) - (first * BoundBlock)),
            Blocks = rows,
        };
    }

    // Copies a block slab's answer into its final lattice: first the skipped blocks of its layers, each sample its block's
    // fill and material zero, then every sampled block's samples over them, so a sample two blocks share takes its measure.
    private static void DecodeBlocks(FinalLattice final, in SdfMeshBakeRequest request, ReadOnlySpan<int> words) {
        var blocks = final.Blocks!;
        var values = final.Values;
        var materials = final.Materials;
        var (dx, dy, dz) = blocks.Dims;
        var first = (request.Slab / BoundBlock);
        var end = Math.Min(val1: blocks.Count.Z, val2: ((request.Slab + request.SlabPlanes + BoundBlock - 1) / BoundBlock));

        for (var bz = first; (bz < end); bz++) {
            var cz = FinalBlocks.Corner(block: bz, samples: dz, size: blocks.Size.Z);

            for (var by = 0; (by < blocks.Count.Y); by++) {
                var cy = FinalBlocks.Corner(block: by, samples: dy, size: blocks.Size.Y);

                for (var bx = 0; (bx < blocks.Count.X); bx++) {
                    var fill = blocks.Fill[blocks.Index(x: bx, y: by, z: bz)];

                    if (float.IsNaN(f: fill)) { continue; }

                    var cx = FinalBlocks.Corner(block: bx, samples: dx, size: blocks.Size.X);

                    for (var z = 0; (z < blocks.Size.Z); z++) {
                        for (var y = 0; (y < blocks.Size.Y); y++) {
                            var row = (cx + (dx * ((cy + y) + (dy * (cz + z)))));

                            values.Slice(start: row, length: blocks.Size.X).Fill(value: fill);
                            materials.Slice(start: row, length: blocks.Size.X).Clear();
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
            materials[index] = words[count + local];
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
