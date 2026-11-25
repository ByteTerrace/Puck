using System.Numerics;
using Puck.Maths;
using Puck.SignedDistance;
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

    // In the take, under the gate: hands a group's final lattice to the pool to be bounded before its first slab, once.
    // True when the group is held by that job (or retired), so the take passes it by.
    private bool QueueBounds(Group group, FinalLattice final) {
        final.Bounded = true;
        if ((final.Lattice.Samples < BoundLeastSamples) || (group.GlyphCell > 0f) || group.Geometry.ContainsGlyph || (group.SampledSnapshots.Length > 0) || (m_program is not { } program)) { return false; }
        if (!group.QueueWork()) { return true; }

        var field = Volatile.Read(location: ref m_field);

        group.Contouring = true;
        Enqueue(group: group, urgent: true, work: () => Bound(field: field, final: final, group: group, program: program));
        return true;
    }

    // On the pool, holding the group: classifies the final lattice's blocks and gives the group its first slab, or finishes
    // it as empty when no block can hold the surface.
    private void Bound(Group group, FinalLattice final, SdfProgram program, long field) {
        var blocks = Classify(final: final, group: group, program: program, walks: out var walks);
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

    // The final lattice's blocks, each sampled or skipped with its fill; null when the group's CPU field cannot bound it.
    private static FinalBlocks? Classify(Group group, FinalLattice final, SdfProgram program, out long walks) {
        walks = 0L;

        var excerpt = SdfMeshAudit.Field(group: group, program: program);

        foreach (var instruction in excerpt.Instructions) {
            if ((SdfFieldOps.ResourcesOf(value: instruction.Op) & BoundUnseen) != 0) { return null; }
        }
        if (!SdfMeshAudit.TryEvaluator(field: out var evaluator, program: excerpt, refusal: out _)) { return null; }

        var lattice = final.Lattice;
        var blocks = new FinalBlocks(dims: lattice.Dims);
        var band = (BoundBandDiagonals * lattice.Cell.Length());
        // Past every float position the kernel computes for a sample in the box (origin + index · cell, two roundings).
        var pad = new Vector3(value: ((1.0e-5f * (lattice.Origin.Length() + (lattice.Cell.Length() * lattice.Dims.X))) + (2f / 65536f)));
        var count = 0L;

        void Box(int x0, int y0, int z0, int x1, int y1, int z1) {
            var low = (lattice.Origin + (new Vector3(
                x: FinalBlocks.Corner(block: x0, samples: blocks.Dims.X, size: blocks.Size.X),
                y: FinalBlocks.Corner(block: y0, samples: blocks.Dims.Y, size: blocks.Size.Y),
                z: FinalBlocks.Corner(block: z0, samples: blocks.Dims.Z, size: blocks.Size.Z)
            ) * lattice.Cell)) - pad;
            var high = (lattice.Origin + (new Vector3(
                x: (FinalBlocks.Corner(block: (x1 - 1), samples: blocks.Dims.X, size: blocks.Size.X) + blocks.Size.X - 1),
                y: (FinalBlocks.Corner(block: (y1 - 1), samples: blocks.Dims.Y, size: blocks.Size.Y) + blocks.Size.Y - 1),
                z: (FinalBlocks.Corner(block: (z1 - 1), samples: blocks.Dims.Z, size: blocks.Size.Z) + blocks.Size.Z - 1)
            ) * lattice.Cell)) + pad;
            var fill = float.NaN;

            count++;
            if (evaluator.TryDistanceBounds(lower: Fixed(point: low), upper: Fixed(point: high), distance: out var bounds) && !bounds.IsUnbounded) {
                var lower = (float)((double)bounds.Lower);
                var upper = (float)((double)bounds.Upper);

                if (lower > band) {
                    fill = lower;
                } else if (upper < -band) {
                    fill = upper;
                }
            }
            if (!float.IsNaN(f: fill) || (((x1 - x0) == 1) && ((y1 - y0) == 1) && ((z1 - z0) == 1))) {
                for (var z = z0; (z < z1); z++) {
                    for (var y = y0; (y < y1); y++) {
                        for (var x = x0; (x < x1); x++) {
                            blocks.Fill[blocks.Index(x: x, y: y, z: z)] = fill;
                        }
                    }
                }
                if (float.IsNaN(f: fill)) { blocks.Sampled++; }
                return;
            }

            // Halve the box's longest run of blocks.
            var (sx, sy, sz) = ((x1 - x0), (y1 - y0), (z1 - z0));

            if ((sx >= sy) && (sx >= sz)) {
                var middle = (x0 + (sx / 2));
                Box(x0: x0, x1: middle, y0: y0, y1: y1, z0: z0, z1: z1);
                Box(x0: middle, x1: x1, y0: y0, y1: y1, z0: z0, z1: z1);
            } else if (sy >= sz) {
                var middle = (y0 + (sy / 2));
                Box(x0: x0, x1: x1, y0: y0, y1: middle, z0: z0, z1: z1);
                Box(x0: x0, x1: x1, y0: middle, y1: y1, z0: z0, z1: z1);
            } else {
                var middle = (z0 + (sz / 2));
                Box(x0: x0, x1: x1, y0: y0, y1: y1, z0: z0, z1: middle);
                Box(x0: x0, x1: x1, y0: y0, y1: y1, z0: middle, z1: z1);
            }
        }

        Box(x0: 0, x1: blocks.Count.X, y0: 0, y1: blocks.Count.Y, z0: 0, z1: blocks.Count.Z);
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
