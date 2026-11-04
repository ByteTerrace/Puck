using System.Numerics;
using Puck.Shaders;
using static Puck.Shaders.ShaderParameterWriter;

namespace Puck.SdfVm;

/// <summary>Writes the mask and sampling parameters of one mesh-bake dispatch, independently of view rendering.</summary>
public static class SdfMeshBakeBlock {
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
    /// work counters, and the lattices one bake dispatch samples and the instances each selects, four rows a lattice
    /// (<see cref="SdfWorldPackage.MeshBakeLattices"/>), the lattices' samples back to back; the fourth names the point
    /// table entry its group's poses start at, their first slot and how many slots they cover
    /// (<see cref="SdfMeshBakeBatch.Poses"/>).</summary>
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

        WriteUInt32(block: block, offset: BakeInstanceMaskWordCountOffset, value: instanceMaskWordCount);
        WriteUInt32(block: block, offset: BakeWorkCounterRowOffset, value: workCounterRow);
        WriteUInt32(block: block, offset: BakeWorkCounterDetailRowOffset, value: workCounterDetailRow);
        WriteUInt32(block: block, offset: MeshBakeCountOffset, value: ((uint)batch.Lattices.Length));
        for (var index = 0; (index < batch.Lattices.Length); index++) {
            var lattice = batch.Lattices[index];
            var row = (MeshBakeLatticesOffset + ((index * SdfWorldPackage.MeshBakeLatticeRows) * 16));

            WriteVector3(block: block, offset: row, value: lattice.Origin);
            WriteUInt32(block: block, offset: (row + 12), value: first);
            WriteVector3(block: block, offset: (row + 16), value: lattice.Cell);
            WriteUInt32(block: block, offset: (row + 28), value: unchecked((uint)lattice.Selector));
            WriteUInt32(block: block, offset: (row + 32), value: ((uint)lattice.Dims.X));
            WriteUInt32(block: block, offset: (row + 36), value: ((uint)lattice.Dims.Y));
            WriteUInt32(block: block, offset: (row + 40), value: ((uint)lattice.Dims.Z));
            // The lattice's mode in the low two bits (three samples the point table with gradients, two without, zero a
            // grid), a slab's first plane of its final lattice above them, and, in the high half, the run of instances a
            // static selector names.
            if ((lattice.Slab >= MaxSlabPlanes) || (((uint)lattice.Instances) > ushort.MaxValue)) {
                throw new ArgumentException(message: $"A bake lattice names its first plane in 14 bits and its instance run in 16; this one names plane {lattice.Slab} and {lattice.Instances} instances.", paramName: nameof(batch));
            }
            WriteUInt32(block: block, offset: (row + 44), value: (lattice.Points.IsEmpty ? 0u : (lattice.Gradients ? 3u : 2u)) | (((uint)Math.Max(val1: 0, val2: lattice.Slab)) << 2) | (((uint)lattice.Instances) << 16));

            var (poseFirst, poseRows) = ((index < batch.Poses.Length) ? batch.Poses[index] : (0, ReadOnlyMemory<Vector4>.Empty));

            WriteUInt32(block: block, offset: (row + 48), value: ((uint)batch.PoseBase(lattice: index)));
            WriteUInt32(block: block, offset: (row + 52), value: ((uint)poseFirst));
            WriteUInt32(block: block, offset: (row + 56), value: ((uint)(poseRows.Length / 3)));
            WriteUInt32(block: block, offset: (row + 60), value: unchecked((uint)lattice.FrameSlot));
            first += ((uint)lattice.Samples);
        }
    }
}
