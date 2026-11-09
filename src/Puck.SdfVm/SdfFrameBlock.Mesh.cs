using System.Numerics;
using Puck.Shaders;
using static Puck.Shaders.ShaderParameterWriter;

namespace Puck.SdfVm;

public static partial class SdfFrameBlock {
    private static readonly int MeshShadowCascadesOffset = Offset(member: SdfWorldPackage.MeshShadowCascades);
    private static readonly int MeshShadowCountOffset = Offset(member: SdfWorldPackage.MeshShadowCount);
    private static readonly int MeshShadowParametersOffset = Offset(member: SdfWorldPackage.MeshShadowParameters);
    private static readonly int MeshShadowTexelsOffset = Offset(member: SdfWorldPackage.MeshShadowTexels);
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
    /// <summary>Writes the irradiance volume the views pass reads (<see cref="SdfMeshIrradiance"/>): each level's window
    /// and spacing along each axis. A level not published writes zero spacing and views then light as without that
    /// level; a surface no published level answers reads the light the view's camera sample measured.</summary>
    /// <param name="block">The pass block <see cref="Write"/> filled.</param>
    /// <param name="volume">The volume's parameters.</param>
    public static void WriteMeshIrradiance(Span<byte> block, in SdfMeshIrradianceParameters volume) {
        var levels = Offset(member: SdfWorldPackage.MeshIrradianceLevels);
        var spacings = Offset(member: SdfWorldPackage.MeshIrradianceSpacings);

        for (var level = 0; (level < SdfMeshIrradianceProbe.Levels); level++) {
            // A level not published keeps its probe counts, which place every coarser level's slots, and reads as absent
            // by its zero spacing.
            var window = (volume.Enabled ? volume.Window(level: level) : default);
            var present = window.Present;
            var dims = ((uint)window.Dims.X) | (((uint)window.Dims.Y) << 10) | (((uint)window.Dims.Z) << 20);
            var row = (levels + (level * 16));

            WriteRow(block: block, offset: (spacings + (level * 16)), value: (present ? window.Spacing : Vector3.Zero), w: 0f);
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
