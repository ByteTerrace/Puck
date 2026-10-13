using System.Numerics;
using Puck.Shaders;

namespace Puck.SdfVm;

public static partial class SdfFrameBlock {
    private static readonly int RenderPathOffset = Offset(member: SdfWorldPackage.RenderPath);
    private static readonly int MeshShadowCascadesOffset = Offset(member: SdfWorldPackage.MeshShadowCascades);
    private static readonly int MeshShadowCountOffset = Offset(member: SdfWorldPackage.MeshShadowCount);
    private static readonly int MeshShadowParametersOffset = Offset(member: SdfWorldPackage.MeshShadowParameters);
    private static readonly int MeshBakeLatticesOffset = Offset(member: SdfWorldPackage.MeshBakeLattices);
    private static readonly int MeshBakeCountOffset = Offset(member: SdfWorldPackage.MeshBakeCount);
    // A light slice naming one map of one row and one column: any slice sdfIndirectLightSlice accepts puts the mesh pass's
    // stages in their orthographic light-camera form, which the mesh path's cascades draw through.
    private const uint OrthographicSlice = (1u | (1u << SdfIndirectLightLayout.SliceCountShift) | (1u << SdfIndirectLightLayout.SliceColumnCountShift));

    /// <summary>Writes the render path the pass's graph runs: one on the baked-mesh path, whose surface pass writes a mesh
    /// pixel's shadow row, zero on the interpreter path.</summary>
    /// <param name="block">The pass block <see cref="Write"/> filled.</param>
    /// <param name="mesh">Whether the graph is the mesh path's (<see cref="SdfWorldPackage.MeshPathFragment"/>).</param>
    public static void WriteRenderPath(Span<byte> block, bool mesh) =>
        WriteUInt32(block: block, offset: RenderPathOffset, value: (mesh ? 1u : 0u));
    /// <summary>Writes the shadow cascades the mesh path's surface pass reads.</summary>
    /// <param name="block">The pass block <see cref="Write"/> filled.</param>
    /// <param name="cascades">The frame's cascades.</param>
    public static void WriteMeshShadows(Span<byte> block, in SdfMeshShadowCascades cascades) {
        WriteUInt32(block: block, offset: MeshShadowCountOffset, value: ((uint)cascades.Count));
        WriteSingle(block: block, offset: MeshShadowParametersOffset, value: cascades.PenumbraSlope);
        WriteSingle(block: block, offset: (MeshShadowParametersOffset + sizeof(float)), value: SdfMeshShadowCascades.ConstantBias);
        WriteSingle(block: block, offset: (MeshShadowParametersOffset + (2 * sizeof(float))), value: SdfMeshShadowCascades.NormalOffsetTexels);
        WriteSingle(block: block, offset: (MeshShadowParametersOffset + (3 * sizeof(float))), value: cascades.SecondPenumbraSlope);

        for (var index = 0; (index < SdfWorldPackage.MeshShadowCascadeCount); index++) {
            var camera = cascades[index];
            var row = (MeshShadowCascadesOffset + (index * SdfWorldPackage.MeshShadowCascadeRows * 16));

            WriteRow(block: block, offset: row, value: camera.Position, w: camera.HalfHeight);
            WriteRow(block: block, offset: (row + 16), value: camera.Right, w: 1f);
            WriteRow(block: block, offset: (row + 32), value: camera.Up, w: camera.Near);
            WriteRow(block: block, offset: (row + 48), value: camera.Forward, w: camera.Far);
        }
    }
    /// <summary>Writes one cascade's orthographic light camera as the view a mesh-path shadow raster pass draws from: the
    /// view rows the mesh pass's stages read, a light slice that puts them in their orthographic form, the cascade target's
    /// extent, and no jitter.</summary>
    /// <param name="block">The pass block <see cref="Write"/> filled.</param>
    /// <param name="camera">The cascade's light camera.</param>
    public static void WriteShadowCamera(Span<byte> block, in SdfMeshShadowCamera camera) {
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
        WriteUInt32(block: block, offset: ImageExtent, value: SdfWorldPackage.MeshShadowResolution);
        WriteUInt32(block: block, offset: (ImageExtent + sizeof(uint)), value: SdfWorldPackage.MeshShadowResolution);
        WriteUInt32(block: block, offset: Offset(member: SdfWorldPackage.LightSlice), value: OrthographicSlice);
        WriteTemporal(block: block, jitter: Vector2.Zero, historyFrames: 0, temporal: false);
    }
    /// <summary>Writes the lattices one bake dispatch samples and the instances each selects, three rows a lattice
    /// (<see cref="SdfWorldPackage.MeshBakeLattices"/>), the lattices' samples back to back.</summary>
    /// <param name="block">The pass block <see cref="Write"/> filled.</param>
    /// <param name="batch">The batch.</param>
    public static void WriteMeshBake(Span<byte> block, SdfMeshBakeBatch batch) {
        ArgumentNullException.ThrowIfNull(argument: batch);

        var first = 0u;

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
            // The lattice's mode: two samples the point table, one a grid with gradients, zero a grid without.
            WriteUInt32(block: block, offset: (row + 44), value: (lattice.Points.IsEmpty ? (lattice.Gradients ? 1u : 0u) : 2u));
            first += ((uint)lattice.Samples);
        }
    }

    /// <summary>Writes the irradiance volume the mesh path's views pass reads (<see cref="SdfMeshIrradiance"/>); a disabled
    /// volume writes zeros, and views then light as without indirect light.</summary>
    /// <param name="block">The pass block <see cref="Write"/> filled.</param>
    /// <param name="volume">The volume's parameters.</param>
    public static void WriteMeshIrradiance(Span<byte> block, in SdfMeshIrradianceParameters volume) {
        var offset = Offset(member: SdfWorldPackage.MeshIrradianceVolume);
        var low = Offset(member: SdfWorldPackage.MeshIrradianceLow);
        var dims = (volume.Enabled ? (((uint)volume.Dims.X) | (((uint)volume.Dims.Y) << 10) | (((uint)volume.Dims.Z) << 20)) : 0u);

        WriteUInt32(block: block, offset: offset, value: (volume.Enabled ? 1u : 0u));
        WriteSingle(block: block, offset: (offset + sizeof(uint)), value: (volume.Enabled ? volume.Spacing : 0f));
        WriteUInt32(block: block, offset: (offset + (2 * sizeof(uint))), value: dims);
        WriteUInt32(block: block, offset: (offset + (3 * sizeof(uint))), value: 0u);
        WriteUInt32(block: block, offset: low, value: unchecked((uint)volume.Low.X));
        WriteUInt32(block: block, offset: (low + sizeof(uint)), value: unchecked((uint)volume.Low.Y));
        WriteUInt32(block: block, offset: (low + (2 * sizeof(uint))), value: unchecked((uint)volume.Low.Z));
        WriteUInt32(block: block, offset: (low + (3 * sizeof(uint))), value: 0u);
    }

    private static void WriteRow(Span<byte> block, int offset, Vector3 value, float w) {
        WriteVector3(block: block, offset: offset, value: value);
        WriteSingle(block: block, offset: (offset + (3 * sizeof(float))), value: w);
    }
}
