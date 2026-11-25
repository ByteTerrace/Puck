using System.Numerics;
using Puck.Shaders;
using static Puck.Shaders.ShaderParameterWriter;

namespace Puck.SdfVm;

/// <summary>Writes the mask and sampling parameters of one mesh-bake dispatch, independently of view rendering.</summary>
public static class SdfMeshBakeBlock {
    internal const int LatticeBytes = (SdfWorldPackage.MeshBakeLatticeRows * 16);
    // The mesh bake's own pass block, which its mask and sampler read in place of the world interface's.
    private static readonly ShaderPipelineParameterLayout Layout = SdfWorldInterfaces.MeshBakeParameters;
    private static readonly int BakeInstanceMaskWordCountOffset = Offset(member: SdfWorldPackage.InstanceMaskWordCount);
    private static readonly int BakeWorkCounterRowOffset = Offset(member: ShaderWorkCounters.Row);
    private static readonly int BakeWorkCounterDetailRowOffset = Offset(member: ShaderWorkCounters.DetailRow);
    private static readonly int MeshBakeLatticesOffset = Offset(member: SdfWorldPackage.MeshBakeLattices);
    private static readonly int MeshBakeCountOffset = Offset(member: SdfWorldPackage.MeshBakeCount);

    /// <summary>Gets the bytes of the mesh bake's pass block (<see cref="Write"/>), a multiple of 16.</summary>
    public static int SizeBytes => ((int)Layout.SizeBytes);

    private static int Offset(string member) =>
        ((int)Layout.BlockOffsetOf(member: member));

    /// <summary>The planes a final lattice holds at most along Z: a slab names its first plane in bits 2 to 15 of its
    /// mode word, below the instance run (<c>sdf-mesh-bake.comp.hlsl</c>, <c>sdf-mesh-bake-mask.comp.hlsl</c>).</summary>
    public const int MaxSlabPlanes = (1 << 14);
    /// <summary>Writes the mesh bake's pass block (<see cref="SdfWorldInterfaces.MeshBakeParameters"/>), all its mask
    /// and sampler read beside the extent the node writes: the live program's instance-mask width, the pass's rows of the
    /// work counters, and the lattices one bake dispatch samples and the instances each selects,
    /// <see cref="SdfWorldPackage.MeshBakeLatticeRows"/> rows a lattice (<see cref="SdfWorldPackage.MeshBakeLattices"/>),
    /// the lattices' samples back to back; the fourth names the point table entry its group's poses start at, their first
    /// slot and how many slots they cover (<see cref="SdfMeshBakeBatch.Poses"/>), and the fifth its glyphs' thickening
    /// (<see cref="SdfMeshBakeRequest.Glyphs"/>).</summary>
    /// <param name="block">The bake's pass block, at least <see cref="SizeBytes"/> bytes.</param>
    /// <param name="batch">The batch.</param>
    /// <param name="instanceMaskWordCount">The live program's instance-mask width, in words
    /// (<see cref="SdfPassValues.InstanceMaskWordCount"/>).</param>
    /// <param name="workCounterRow">The pass's row of the work counters (<see cref="ShaderWorkCounters.Row"/>).</param>
    /// <param name="workCounterDetailRow">The pass's first named detail row, zero for none
    /// (<see cref="ShaderWorkCounters.DetailRow"/>).</param>
    /// <exception cref="ArgumentNullException"><paramref name="batch"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="batch"/> holds more than
    /// <see cref="SdfWorldPackage.MeshBakeBatch"/> lattices, which the bake kernels' search assumes it never does, or a
    /// lattice whose first plane reaches <see cref="MaxSlabPlanes"/> or whose instance run passes 65,535.</exception>
    public static void Write(Span<byte> block, SdfMeshBakeBatch batch, uint instanceMaskWordCount, uint workCounterRow, uint workCounterDetailRow) {
        ArgumentNullException.ThrowIfNull(argument: batch);
        if (batch.Lattices.Length > SdfWorldPackage.MeshBakeBatch) {
            throw new ArgumentException(message: $"A bake dispatch holds at most {SdfWorldPackage.MeshBakeBatch} lattices; this batch holds {batch.Lattices.Length}.", paramName: nameof(batch));
        }

        var first = 0u;
        var outputWord = 0u;

        WriteUInt32(block: block, offset: BakeInstanceMaskWordCountOffset, value: instanceMaskWordCount);
        WriteUInt32(block: block, offset: BakeWorkCounterRowOffset, value: workCounterRow);
        WriteUInt32(block: block, offset: BakeWorkCounterDetailRowOffset, value: workCounterDetailRow);
        WriteUInt32(block: block, offset: MeshBakeCountOffset, value: ((uint)batch.Lattices.Length));
        for (var index = 0; (index < batch.Lattices.Length); index++) {
            var lattice = batch.Lattices[index];
            var (poseFirst, poseRows) = ((index < batch.Poses.Length) ? batch.Poses[index] : (0, ReadOnlyMemory<Vector4>.Empty));
            WriteLattice(block.Slice(MeshBakeLatticesOffset + (index * LatticeBytes), LatticeBytes), in lattice,
                first, batch.PoseBase(lattice: index), poseFirst, poseRows.Length, outputWord);
            outputWord += (uint)(lattice.OutputBytes / sizeof(uint));
            first += ((uint)lattice.Samples);
        }
    }

    internal static void WriteLattice(Span<byte> block, in SdfMeshBakeRequest lattice, uint first, int poseBase, int poseFirst, int poseRowCount, uint outputWord = 0) {
        WriteVector3(block: block, offset: 0, value: lattice.Origin);
        WriteUInt32(block: block, offset: 12, value: first);
        // A grid's spacing places its samples; a points lattice's samples come from the point table, so its second row
        // carries the spacing its gradient taps lie at instead, which the kernel reads rather than deriving its own.
        WriteVector3(block: block, offset: 16, value: (lattice.Points.IsEmpty ? lattice.Cell : new Vector3(value: lattice.TapSpacing)));
        WriteUInt32(block: block, offset: 28, value: unchecked((uint)lattice.Selector));
        WriteUInt32(block: block, offset: 32, value: ((uint)lattice.Dims.X));
        WriteUInt32(block: block, offset: 36, value: ((uint)lattice.Dims.Y));
        WriteUInt32(block: block, offset: 40, value: ((uint)lattice.Dims.Z));
        // The lattice's mode in the low two bits (three samples the point table with gradients, two without, one a block
        // slab, zero a grid), above them a slab's first plane of its final lattice or a block slab's block depth, and, in
        // the high half, the run of instances a static selector names.
        if ((lattice.Slab >= MaxSlabPlanes) || (((uint)lattice.Instances) > ushort.MaxValue)) {
            throw new ArgumentException(message: $"A bake lattice names its first plane in 14 bits and its instance run in 16; this one names plane {lattice.Slab} and {lattice.Instances} instances.", paramName: nameof(lattice));
        }
        var (mode, plane) = (!lattice.Points.IsEmpty ? ((lattice.Gradients ? 3u : 2u), 0u)
            : !lattice.Blocks.IsEmpty ? (1u, ((uint)lattice.BlockDepth))
            : (0u, ((uint)Math.Max(val1: 0, val2: lattice.Slab))));
        WriteUInt32(block: block, offset: 44, value: mode | (plane << 2) | (((uint)lattice.Instances) << 16));

        WriteUInt32(block: block, offset: 48, value: ((uint)poseBase));
        WriteUInt32(block: block, offset: 52, value: ((uint)poseFirst));
        WriteUInt32(block: block, offset: 56, value: ((uint)(poseRowCount / 3)));
        WriteUInt32(block: block, offset: 60, value: unchecked((uint)lattice.FrameSlot));
        // The lettering stroke rule's cell and stroke (sdfGlyph), the group's for every lattice of it.
        WriteVector4(block: block, offset: 64, value: new Vector4(x: lattice.Glyphs.Cell, y: lattice.Glyphs.Stroke, z: (lattice.LivePoses ? 1f : 0f), w: 0f));
        WriteUInt32(block, offset: 76, value: (outputWord << 2) | lattice.OutputMode);
    }
}
