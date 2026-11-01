using System.Numerics;
using Puck.Shaders;

namespace Puck.SdfVm;

public static partial class SdfFrameBlock {
    private static readonly int MeshShadowCascadesOffset = Offset(member: SdfWorldPackage.MeshShadowCascades);
    private static readonly int MeshShadowCountOffset = Offset(member: SdfWorldPackage.MeshShadowCount);
    private static readonly int MeshShadowParametersOffset = Offset(member: SdfWorldPackage.MeshShadowParameters);
    private static readonly int MeshShadowTexelsOffset = Offset(member: SdfWorldPackage.MeshShadowTexels);
    // The mesh bake's own pass block, which its mask and sampler read in place of the world interface's.
    private static readonly ShaderPipelineParameterLayout BakeLayout = SdfWorldInterfaces.MeshBakeParameters;
    private static readonly int BakeInstanceMaskWordCountOffset = BakeOffset(member: SdfWorldPackage.InstanceMaskWordCount);
    private static readonly int BakeWorkCounterRowOffset = BakeOffset(member: ShaderWorkCounters.Row);
    private static readonly int BakeWorkCounterDetailRowOffset = BakeOffset(member: ShaderWorkCounters.DetailRow);
    private static readonly int MeshBakeLatticesOffset = BakeOffset(member: SdfWorldPackage.MeshBakeLattices);
    private static readonly int MeshBakeCountOffset = BakeOffset(member: SdfWorldPackage.MeshBakeCount);

    /// <summary>Gets the bytes of the mesh bake's pass block (<see cref="WriteMeshBake"/>), a multiple of 16.</summary>
    public static int BakeSizeBytes => ((int)BakeLayout.SizeBytes);

    private static int BakeOffset(string member) =>
        ((int)BakeLayout.BlockOffsetOf(member: member));

    /// <summary>Writes the shadow cascades the surface pass reads.</summary>
    /// <param name="block">The pass block <see cref="Write"/> filled.</param>
    /// <param name="cascades">The frame's cascades.</param>
    public static void WriteMeshShadows(Span<byte> block, in SdfMeshShadowCascades cascades) {
        WriteUInt32(block: block, offset: MeshShadowCountOffset, value: ((uint)cascades.Count));
        WriteUInt32(block: block, offset: MeshShadowTexelsOffset, value: ((uint)cascades.Texels));
        WriteSingle(block: block, offset: MeshShadowParametersOffset, value: cascades.PenumbraSlope);
        WriteSingle(block: block, offset: (MeshShadowParametersOffset + sizeof(float)), value: SdfMeshShadowCascades.ConstantBias);
        WriteSingle(block: block, offset: (MeshShadowParametersOffset + (2 * sizeof(float))), value: SdfMeshShadowCascades.NormalOffsetTexels);
        WriteSingle(block: block, offset: (MeshShadowParametersOffset + (3 * sizeof(float))), value: cascades.SecondPenumbraSlope);

        for (var index = 0; (index < SdfWorldPackage.MeshShadowCascadeCount); index++) {
            var camera = cascades[index];
            var row = (MeshShadowCascadesOffset + ((index * SdfWorldPackage.MeshShadowCascadeRows) * 16));

            WriteRow(block: block, offset: row, value: camera.Position, w: camera.HalfHeight);
            WriteRow(block: block, offset: (row + 16), value: camera.Right, w: 1f);
            WriteRow(block: block, offset: (row + 32), value: camera.Up, w: camera.Near);
            WriteRow(block: block, offset: (row + 48), value: camera.Forward, w: camera.Far);
        }
    }
    /// <summary>Writes one cascade's orthographic light camera as the view a shadow cascade's raster pass draws from: the
    /// view rows the shadow pipeline's vertex stage projects with (<c>sdf-mesh-shadow.vert.hlsl</c>), the cascade's depth
    /// interval as the near and far distances, the cascade's extent, and no jitter.</summary>
    /// <param name="block">The pass block <see cref="Write"/> filled.</param>
    /// <param name="camera">The cascade's light camera.</param>
    /// <param name="texels">The texels along each side of the cascade's depth.</param>
    public static void WriteShadowCamera(Span<byte> block, in SdfMeshShadowCamera camera, int texels) {
        WriteVector3(block: block, offset: ViewPosition, value: camera.Position);
        WriteVector3(block: block, offset: ViewRight, value: camera.Right);
        WriteVector3(block: block, offset: ViewUp, value: camera.Up);
        WriteVector3(block: block, offset: ViewForward, value: camera.Forward);
        WriteSingle(block: block, offset: TanHalfFieldOfView, value: camera.HalfHeight);
        WriteSingle(block: block, offset: AspectRatio, value: 1f);
        WriteSingle(block: block, offset: FrustumOffset, value: 0f);
        WriteSingle(block: block, offset: (FrustumOffset + sizeof(float)), value: 0f);
        WriteSingle(block: block, offset: NearDistance, value: camera.Near);
        WriteSingle(block: block, offset: FarDistance, value: camera.Far);
        WriteUInt32(block: block, offset: ImageExtent, value: ((uint)texels));
        WriteUInt32(block: block, offset: (ImageExtent + sizeof(uint)), value: ((uint)texels));
        WriteTemporal(block: block, jitter: Vector2.Zero, historyFrames: 0, temporal: false);
    }
    /// <summary>The planes a final lattice holds at most along Z: a slab names its first plane in bits 2 to 15 of its
    /// mode word, below the instance run (<c>sdf-mesh-bake.comp.hlsl</c>, <c>sdf-mesh-bake-mask.comp.hlsl</c>).</summary>
    public const int MaxSlabPlanes = (1 << 14);
    /// <summary>Writes the mesh bake's pass block (<see cref="SdfWorldInterfaces.MeshBakeParameters"/>), all its mask
    /// and sampler read beside the extent the node writes: the live program's instance-mask width, the pass's rows of the
    /// work counters, and the lattices one bake dispatch samples and the instances each selects, four rows a lattice
    /// (<see cref="SdfWorldPackage.MeshBakeLattices"/>), the lattices' samples back to back; the fourth names the point
    /// table entry its group's poses start at, their first slot and how many slots they cover
    /// (<see cref="SdfMeshBakeBatch.Poses"/>).</summary>
    /// <param name="block">The bake's pass block, at least <see cref="BakeSizeBytes"/> bytes.</param>
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
    public static void WriteMeshBake(Span<byte> block, SdfMeshBakeBatch batch, uint instanceMaskWordCount, uint workCounterRow, uint workCounterDetailRow) {
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
    /// <summary>Writes the irradiance volume the views pass reads (<see cref="SdfMeshIrradiance"/>): whether views read
    /// it, then each level's spacing and window; a disabled volume, or a level not published, writes zeros, and views
    /// then light as without that level.</summary>
    /// <param name="block">The pass block <see cref="Write"/> filled.</param>
    /// <param name="volume">The volume's parameters.</param>
    public static void WriteMeshIrradiance(Span<byte> block, in SdfMeshIrradianceParameters volume) {
        var offset = Offset(member: SdfWorldPackage.MeshIrradianceVolume);
        var levels = Offset(member: SdfWorldPackage.MeshIrradianceLevels);

        WriteUInt32(block: block, offset: offset, value: (volume.Enabled ? 1u : 0u));
        for (var level = 0; (level < SdfMeshIrradianceProbe.Levels); level++) {
            // A level not published keeps its probe counts, which place every coarser level's slots, and reads as absent
            // by its zero spacing.
            var window = (volume.Enabled ? volume.Window(level: level) : default);
            var present = window.Present;
            var dims = ((uint)window.Dims.X) | (((uint)window.Dims.Y) << 10) | (((uint)window.Dims.Z) << 20);
            var row = (levels + (level * 16));

            WriteSingle(block: block, offset: (offset + ((1 + level) * sizeof(uint))), value: (present ? window.Spacing : 0f));
            WriteUInt32(block: block, offset: row, value: unchecked((uint)window.Low.X));
            WriteUInt32(block: block, offset: (row + sizeof(uint)), value: unchecked((uint)window.Low.Y));
            WriteUInt32(block: block, offset: (row + (2 * sizeof(uint))), value: unchecked((uint)window.Low.Z));
            WriteUInt32(block: block, offset: (row + (3 * sizeof(uint))), value: dims);
        }
    }

    private static void WriteRow(Span<byte> block, int offset, Vector3 value, float w) {
        WriteVector3(block: block, offset: offset, value: value);
        WriteSingle(block: block, offset: (offset + (3 * sizeof(float))), value: w);
    }
}
