using System.Numerics;
using System.Runtime.InteropServices;
using Puck.Abstractions.Cameras;
using Puck.Abstractions.Presentation;
using Puck.Shaders;
using Puck.SignedDistance;
using Xunit;

namespace Puck.SdfVm.Tests;

/// <summary>Holds <see cref="SdfFrameBlock"/> to the pass block the kernels read: every value the <c>sdf.world</c> package
/// declares is written, at the offset its generated declaration reads, and the mesh pass's interface lays the same block
/// out member for member, so it binds the block its node writes.</summary>
public sealed class SdfFrameBlockLawTests {
    // The values the writer leaves as their zero default on every frame: the extent, which the node writes, and the view
    // base, since each instance renders its one view at row zero.
    private static readonly string[] ZeroValues = [ShaderFrameInterface.Extent, SdfWorldPackage.ViewBase];
    // A camera basis none of whose components is zero.
    private static readonly Quaternion Basis = Quaternion.CreateFromYawPitchRoll(pitch: 0.4f, roll: 0.5f, yaw: 0.3f);

    private static SdfFrame Frame() {
        var builder = new SdfProgramBuilder();
        var material = builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One));

        builder.Sphere(
            radius: 1f,
            material: material
        );

        return new SdfFrame(
            Program: builder.Build(),
            ProgramChanged: true,
            Views: [
                new SdfViewSnapshot(
                    Camera: new CameraSnapshot(
                        AspectRatio: 1.5f,
                        Forward: Vector3.Transform(rotation: Basis, value: Vector3.UnitZ),
                        Position: new Vector3(x: 1f, y: 2f, z: 3f),
                        Right: Vector3.Transform(rotation: Basis, value: Vector3.UnitX),
                        TanHalfFieldOfView: 0.5f,
                        Up: Vector3.Transform(rotation: Basis, value: Vector3.UnitY)
                    ) { FrustumOffset = new Vector2(x: 0.25f, y: 0.5f) },
                    Region: new NormalizedRect(Height: 1f, Width: 1f, X: 0f, Y: 0f)
                ),
            ],
            Time: 7f
        ) {
            AmbientScale = 0.5f,
            DebugSliceAxis = 2f,
            DebugSliceOffset = 3f,
            DisableAmbientOcclusion = true,
            DisableFarBound = true,
            DisableScreenLights = true,
            DisableShadowCull = true,
            DisableSoftShadows = true,
            EnableShadowProxy = true,
            FarDistance = 30f,
            GridFlags = 3u,
            GridFloorY = 1f,
            GridObjectFrame = new Quaternion(w: 0.9f, x: 0.1f, y: 0.2f, z: 0.3f),
            GridObjectOrigin = new Vector3(x: 4f, y: 5f, z: 6f),
            GridObjectPatchRadius = 2f,
            GridObjectPitch = new Vector2(x: 0.5f, y: 0.75f),
            GridWorldPitch = new Vector2(x: 1f, y: 2f),
            ShadowDistanceScale = 0.5f,
            SunScale = 0.75f,
            UseCameraTileShadowMask = true,
            UseFastAmbientOcclusion = true,
            UseFastSoftShadowMarch = true,
            UseFiniteDifferenceNormals = true,
        };
    }

    [Fact]
    public void EveryDeclaredValueIsWrittenWhereItsDeclarationReadsIt() {
        var frame = Frame();
        var environment = new float[SdfEnvironment.LaneCount];
        var block = new byte[SdfFrameBlock.SizeBytes];

        SdfFrameBlock.BakeEnvironment(
            frame: frame,
            rows: environment
        );
        SdfFrameBlock.Write(
            block: block,
            frame: frame,
            height: 200u,
            sceneTime: frame.Time,
            tables: new SdfPassValues(
                DebugMode: 4,
                Environment: environment,
                InstanceMaskWordCount: 2u,
                MeshDraws: 5u,
                SampleIndex: 9u,
                ScreenCount: 3u
            ),
            view: 0,
            width: 300u
        );

        var parameters = SdfWorldInterfaces.WorldParameters;
        var pass = parameters.Layout.Groups.Single(predicate: static group => (group.Group == ShaderInterfaceGroup.Pass));

        foreach (var member in pass.BlockMembers.Where(predicate: static member => !member.Name.StartsWith(comparisonType: StringComparison.Ordinal, value: "_pad"))) {
            var bytes = block.AsSpan(
                length: ((int)((member.Length == 0) ? member.Type.SizeBytes() : (member.Length * 16u))),
                start: ((int)member.Offset)
            );

            if (ZeroValues.Contains(value: member.Name)) {
                Assert.True(condition: !bytes.ContainsAnyExcept(value: ((byte)0)), userMessage: member.Name);
            } else if (string.Equals(a: member.Name, b: SdfWorldPackage.Environment, comparisonType: StringComparison.Ordinal)) {
                Assert.True(condition: bytes.SequenceEqual(other: MemoryMarshal.AsBytes(span: environment.AsSpan())), userMessage: member.Name);
            } else {
                // Every component of a written value is non-zero here, so a component the writer misses reads as zero.
                for (var component = 0; (component < member.Type.ComponentCount()); component++) {
                    Assert.True(condition: bytes.Slice(length: 4, start: (component * 4)).ContainsAnyExcept(value: ((byte)0)), userMessage: $"{member.Name}[{component}]");
                }
            }
        }

        Assert.Equal(
            actual: BitConverter.ToSingle(value: block, startIndex: ((int)parameters.BlockOffsetOf(member: SdfWorldPackage.FarDistance))),
            expected: 30f
        );
        Assert.Equal(
            actual: BitConverter.ToUInt32(value: block, startIndex: ((int)parameters.BlockOffsetOf(member: SdfWorldPackage.TileGrid))),
            expected: ((300u + (SdfWorldPackage.TileSize - 1u)) / SdfWorldPackage.TileSize)
        );
    }
    [Fact]
    public void TheMeshInterfaceLaysOutTheWorldPassBlockMemberForMember() {
        static IReadOnlyList<ShaderInterfaceBlockMember> PassBlock(ShaderInterfaceLayout layout) =>
            layout.Groups.Single(predicate: static group => (group.Group == ShaderInterfaceGroup.Pass)).BlockMembers;

        Assert.Equal(
            actual: PassBlock(layout: SdfWorldInterfaces.MeshLayout),
            expected: PassBlock(layout: SdfWorldInterfaces.WorldLayout)
        );
    }
    [Fact]
    public void TheEnvironmentArrayHoldsEveryEnvironmentRow() =>
        Assert.Equal(
            actual: SdfWorldPackage.EnvironmentRows,
            expected: ((uint)SdfEnvironment.RowCount)
        );
}
