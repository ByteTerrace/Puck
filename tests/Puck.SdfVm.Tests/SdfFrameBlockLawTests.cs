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
    // A grid none of whose components is zero.
    private static readonly GridOverlayState Grid = new(
        Flags: GridOverlayFlags.World | GridOverlayFlags.Object | GridOverlayFlags.Surface,
        LineWidth: 2f,
        ObjectFrame: new Quaternion(w: 0.9f, x: 0.1f, y: 0.2f, z: 0.3f),
        ObjectOrigin: new Vector3(x: 4f, y: 5f, z: 6f),
        ObjectPatchRadius: 2f,
        ObjectPitch: new Vector3(x: 0.5f, y: 0.25f, z: 0.75f),
        PlaneY: 1f,
        WorldFrame: new Quaternion(w: 0.8f, x: 0.2f, y: 0.4f, z: 0.4f),
        WorldOrigin: new Vector3(x: 7f, y: 8f, z: 9f),
        WorldPitch: new Vector3(x: 1f, y: 0.5f, z: 2f)
    );
    // A quality every lever of which is off its default, and the pass-block members that carry them.
    private static readonly SdfViewQuality Restricted = new() {
        DisableAmbientOcclusion = true,
        DisableFarBound = true,
        DisableSoftShadows = true,
        ShadowDistanceScale = 0.5f,
        UseCameraTileShadowMask = true,
        UseFastAmbientOcclusion = true,
        UseFastSoftShadowMarch = true,
    };
    private static readonly (string Member, SdfViewQuality Quality, uint Bits)[] QualityValues = [
        (SdfWorldPackage.CameraTileShadowMask, new() { UseCameraTileShadowMask = true }, 1u),
        (SdfWorldPackage.DisableAmbientOcclusion, new() { DisableAmbientOcclusion = true }, 1u),
        (SdfWorldPackage.DisableFarBound, new() { DisableFarBound = true }, 1u),
        (SdfWorldPackage.DisableSoftShadows, new() { DisableSoftShadows = true }, 1u),
        (SdfWorldPackage.FastAmbientOcclusion, new() { UseFastAmbientOcclusion = true }, 1u),
        (SdfWorldPackage.FastSoftShadowMarch, new() { UseFastSoftShadowMarch = true }, 1u),
        (SdfWorldPackage.ShadowDistanceScale, new() { ShadowDistanceScale = 0.5f }, BitConverter.SingleToUInt32Bits(value: 0.5f)),
    ];

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
                    ) {
                        FrustumOffset = new Vector2(x: 0.25f, y: 0.5f),
                        Near = 0.5f,
                    },
                    Region: new NormalizedRect(Height: 1f, Width: 1f, X: 0f, Y: 0f)
                ) {
                    Grid = Grid,
                    Quality = Restricted,
                },
            ],
            Time: 7f
        ) {
            AmbientScale = 0.5f,
            DebugSliceAxis = 2f,
            DebugSliceOffset = 3f,
            DisableScreenLights = true,
            DisableShadowCull = true,
            EnableShadowProxy = true,
            FarDistance = 30f,
            SunScale = 0.75f,
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
            tables: new SdfPassValues(
                DebugMode: 4,
                Environment: environment,
                InstanceMaskWordCount: 2u,
                MeshDraws: 5u,
                ScreenCount: 3u
            ),
            view: 0,
            width: 300u
        );

        SdfFrameBlock.WriteTemporal(block: block, jitter: new Vector2(x: 0.25f, y: -0.125f), historyFrames: 3);
        var parameters = SdfWorldInterfaces.WorldParameters;

        // The row names the pass, so the view's writer leaves it to the recorder.
        Assert.Equal(
            actual: BitConverter.ToUInt32(value: block, startIndex: ((int)parameters.BlockOffsetOf(member: ShaderWorkCounters.Row))),
            expected: 0u
        );
        SdfFrameBlock.WriteWorkCounterRow(
            block: block,
            row: 6u
        );
        Assert.Equal(
            actual: BitConverter.ToUInt32(value: block, startIndex: ((int)parameters.BlockOffsetOf(member: ShaderWorkCounters.Row))),
            expected: 6u
        );
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
    // Each shading lever, on the frame or in a view's quality, lands in its own pass-block member and nowhere else: from
    // a frame with every lever at its default, turning one lever on changes exactly that member's bytes, so no two
    // levers share or swap a member.
    [Fact]
    public void EachShadingLeverWritesItsOwnMemberAlone() {
        var authored = Frame();
        var defaults = (authored with {
            DisableScreenLights = false,
            DisableShadowCull = false,
            EnableShadowProxy = false,
            UseFiniteDifferenceNormals = false,
            Views = [(authored.Views[0] with { Quality = default })],
        });

        SdfFrame WithQuality(SdfViewQuality quality) => (defaults with { Views = [(defaults.Views[0] with { Quality = quality })] });

        (string Member, SdfFrame Frame)[] levers = [
            (SdfWorldPackage.DisableAmbientOcclusion, WithQuality(quality: new() { DisableAmbientOcclusion = true })),
            (SdfWorldPackage.DisableFarBound, WithQuality(quality: new() { DisableFarBound = true })),
            (SdfWorldPackage.DisableScreenLights, (defaults with { DisableScreenLights = true })),
            (SdfWorldPackage.DisableShadowCull, (defaults with { DisableShadowCull = true })),
            (SdfWorldPackage.DisableSoftShadows, WithQuality(quality: new() { DisableSoftShadows = true })),
            (SdfWorldPackage.EnableShadowProxy, (defaults with { EnableShadowProxy = true })),
            (SdfWorldPackage.ShadowDistanceScale, WithQuality(quality: new() { ShadowDistanceScale = 0.5f })),
            (SdfWorldPackage.CameraTileShadowMask, WithQuality(quality: new() { UseCameraTileShadowMask = true })),
            (SdfWorldPackage.FastAmbientOcclusion, WithQuality(quality: new() { UseFastAmbientOcclusion = true })),
            (SdfWorldPackage.FastSoftShadowMarch, WithQuality(quality: new() { UseFastSoftShadowMarch = true })),
            (SdfWorldPackage.FiniteDifferenceNormals, (defaults with { UseFiniteDifferenceNormals = true })),
        ];

        static byte[] Block(SdfFrame frame) {
            var block = new byte[SdfFrameBlock.SizeBytes];

            SdfFrameBlock.Write(
                block: block,
                frame: frame,
                height: 200u,
                tables: new SdfPassValues(
                    DebugMode: 0,
                    Environment: new float[SdfEnvironment.LaneCount],
                    InstanceMaskWordCount: 1u,
                    MeshDraws: 0u,
                    ScreenCount: 0u
                ),
                view: 0,
                width: 300u
            );

            return block;
        }

        var baseline = Block(frame: defaults);

        foreach (var (member, frame) in levers) {
            var block = Block(frame: frame);
            var offset = ((int)SdfWorldInterfaces.WorldParameters.BlockOffsetOf(member: member));
            var changed = Enumerable.Range(count: block.Length, start: 0).Where(predicate: index => (block[index] != baseline[index])).ToArray();

            Assert.NotEmpty(collection: changed);
            Assert.All(
                action: index => Assert.InRange(actual: index, high: (offset + 3), low: offset),
                collection: changed
            );
        }
    }
    // Quality is each view's: two views of one frame, which two instances of one residency render, write their own
    // quality into their own pass blocks and share every other value. Each lever varies independently.
    [Fact]
    public void EachViewOfOneFrameWritesItsOwnQualityAndSharesEverythingElse() {
        var authored = Frame();
        var blocks = new[] { new byte[SdfFrameBlock.SizeBytes], new byte[SdfFrameBlock.SizeBytes] };

        foreach (var (member, quality, bits) in QualityValues) {
            var frame = (authored with { Views = [(authored.Views[0] with { Quality = quality }), (authored.Views[0] with { Quality = default })] });

            for (var view = 0; (view < blocks.Length); view++) {
                SdfFrameBlock.Write(
                    block: blocks[view],
                    frame: frame,
                    height: 200u,
                    tables: new SdfPassValues(
                        DebugMode: 0,
                        Environment: new float[SdfEnvironment.LaneCount],
                        InstanceMaskWordCount: 1u,
                        MeshDraws: 0u,
                        ScreenCount: 0u
                    ),
                    view: view,
                    width: 300u
                );
            }

            var parameters = SdfWorldInterfaces.WorldParameters;
            var offset = ((int)parameters.BlockOffsetOf(member: member));

            Assert.Equal(actual: BitConverter.ToUInt32(value: blocks[0], startIndex: offset), expected: bits);
            Assert.Equal(actual: BitConverter.ToUInt32(value: blocks[1], startIndex: offset), expected: 0u);
            for (var index = 0; (index < SdfFrameBlock.SizeBytes); index++) {
                if ((index < offset) || (index >= (offset + sizeof(uint)))) {
                    Assert.Equal(actual: blocks[1][index], expected: blocks[0][index]);
                }
            }
        }
    }
    // The pass block carries the camera's own near plane, so the bounded volumes start at the eye of a camera whose image
    // begins there (a cloud around the camera stays visible), and the surfaces render from that plane, never nearer than
    // the floor the mesh pass's depth needs, which the kernels read as the generated SDF_MINIMUM_NEAR.
    [Fact]
    public void ThePassBlockCarriesTheCamerasOwnNearAndTheSurfacesRenderFromTheFloorAtLeast() {
        Assert.Matches(
            actualString: SdfIsaHlsl.Generate(),
            expectedRegexPattern: $@"(?m)^#define SDF_MINIMUM_NEAR +{System.Text.RegularExpressions.Regex.Escape(str: SdfFrameBlock.MinimumNear.ToString(format: "R", provider: System.Globalization.CultureInfo.InvariantCulture))}$"
        );

        foreach (var (near, surfaces) in ((ReadOnlySpan<(float, float)>)[(0f, SdfFrameBlock.MinimumNear), (0.01f, SdfFrameBlock.MinimumNear), (8.9f, 8.9f)])) {
            var authored = Frame();
            var frame = (authored with { Views = [(authored.Views[0] with { Camera = (authored.Views[0].Camera with { Near = near }) })] });
            var block = new byte[SdfFrameBlock.SizeBytes];

            SdfFrameBlock.Write(
                block: block,
                frame: frame,
                height: 200u,
                tables: new SdfPassValues(
                    DebugMode: 0,
                    Environment: new float[SdfEnvironment.LaneCount],
                    InstanceMaskWordCount: 1u,
                    MeshDraws: 0u,
                    ScreenCount: 0u
                ),
                view: 0,
                width: 300u
            );

            Assert.Equal(
                actual: BitConverter.ToSingle(value: block, startIndex: ((int)SdfWorldInterfaces.WorldParameters.BlockOffsetOf(member: SdfWorldPackage.NearDistance))),
                expected: near
            );
            Assert.Equal(
                actual: SdfFrameBlock.NearOf(camera: frame.Views[0].Camera),
                expected: surfaces
            );
        }
    }
    // A view's grid is its own: of two views of one frame, the one that draws a grid carries it in its block and the other
    // carries no flags, so one seat can build on a grid while another plays.
    [Fact]
    public void EachViewsBlockCarriesItsOwnGrid() {
        var authored = Frame();
        var frame = (authored with { Views = [authored.Views[0], (authored.Views[0] with { Grid = GridOverlayState.Hidden })] });
        var parameters = SdfWorldInterfaces.WorldParameters;

        byte[] Block(int view) {
            var block = new byte[SdfFrameBlock.SizeBytes];

            SdfFrameBlock.Write(
                block: block,
                frame: frame,
                height: 200u,
                tables: new SdfPassValues(
                    DebugMode: 0,
                    Environment: new float[SdfEnvironment.LaneCount],
                    InstanceMaskWordCount: 1u,
                    MeshDraws: 0u,
                    ScreenCount: 0u
                ),
                view: view,
                width: 300u
            );

            return block;
        }

        uint Flags(byte[] block) => BitConverter.ToUInt32(startIndex: ((int)parameters.BlockOffsetOf(member: SdfWorldPackage.GridFlags)), value: block);
        float Single(byte[] block, string member, int component = 0) => BitConverter.ToSingle(startIndex: (((int)parameters.BlockOffsetOf(member: member)) + (component * sizeof(float))), value: block);

        var building = Block(view: 0);
        var playing = Block(view: 1);

        Assert.Equal(expected: ((uint)Grid.Flags), actual: Flags(block: building));
        Assert.Equal(
            expected: (Grid.PlaneY, Grid.LineWidth, Grid.WorldPitch.Y, Grid.ObjectPitch.Y, Grid.ObjectPatchRadius),
            actual: (Single(block: building, member: SdfWorldPackage.GridPlaneY), Single(block: building, member: SdfWorldPackage.GridLineWidth), Single(block: building, component: 1, member: SdfWorldPackage.GridWorldPitch), Single(block: building, component: 1, member: SdfWorldPackage.GridObjectPitch), Single(block: building, member: SdfWorldPackage.GridObjectPatchRadius))
        );
        Assert.Equal(
            expected: (Grid.WorldOrigin, Grid.WorldFrame),
            actual: (
                new Vector3(
                    x: Single(block: building, member: SdfWorldPackage.GridWorldOrigin),
                    y: Single(block: building, component: 1, member: SdfWorldPackage.GridWorldOrigin),
                    z: Single(block: building, component: 2, member: SdfWorldPackage.GridWorldOrigin)
                ),
                new Quaternion(
                    w: Single(block: building, component: 3, member: SdfWorldPackage.GridWorldFrame),
                    x: Single(block: building, member: SdfWorldPackage.GridWorldFrame),
                    y: Single(block: building, component: 1, member: SdfWorldPackage.GridWorldFrame),
                    z: Single(block: building, component: 2, member: SdfWorldPackage.GridWorldFrame)
                )
            )
        );
        Assert.Equal(expected: Quaternion.Identity, actual: new Quaternion(
            w: Single(block: playing, component: 3, member: SdfWorldPackage.GridWorldFrame),
            x: Single(block: playing, member: SdfWorldPackage.GridWorldFrame),
            y: Single(block: playing, component: 1, member: SdfWorldPackage.GridWorldFrame),
            z: Single(block: playing, component: 2, member: SdfWorldPackage.GridWorldFrame)
        ));
        Assert.Equal(expected: 0u, actual: Flags(block: playing));
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
