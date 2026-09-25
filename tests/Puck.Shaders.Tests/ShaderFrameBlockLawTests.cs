using System.Buffers.Binary;
using System.Numerics;

namespace Puck.Shaders.Tests;

/// <summary>A pass reads its frame block only through the declarations generated from its interface, and the host
/// writes it through <see cref="ShaderPipelineParameterLayout.WriteFrame"/>. These laws compile every shipped pass, find
/// where DXC placed each member in both bytecodes of every pass that reads the block (DXC drops a block its
/// configuration never reads), and hold the bytes the host writer put there to the value it was given; and they hold
/// the checked-in declarations of the shipped film grain set to the generator.</summary>
public sealed class ShaderFrameBlockLawTests {
    private static readonly ShaderFrameValues Values = new(
        CameraFov: 0.75f,
        CameraPosition: new Vector3(
            x: 1.5f,
            y: 2.5f,
            z: 3.5f
        ),
        CameraTarget: new Vector3(
            x: 4.5f,
            y: 5.5f,
            z: 6.5f
        ),
        CameraUp: new Vector3(
            x: 7.5f,
            y: 8.5f,
            z: 9.5f
        ),
        Pointer: new Vector2(
            x: 10.5f,
            y: 11.5f
        ),
        PointerDown: true,
        PointerPresses: 12,
        Time: 13.5,
        TimeDelta: 0.25
    );
    // The host's value of every frame member word, by member name and component.
    private static readonly Dictionary<string, uint[]> Expected = new(comparer: StringComparer.Ordinal) {
        [ShaderFrameInterface.Extent] = [640, 360],
        [ShaderFrameInterface.Pointer] = [Bits(value: 10.5f), Bits(value: 11.5f)],
        [ShaderFrameInterface.Tick] = [0x89ABCDEFu, 0x01234567u],
        [ShaderFrameInterface.Time] = [Bits(value: 13.5f)],
        [ShaderFrameInterface.TimeDelta] = [Bits(value: 0.25f)],
        [ShaderFrameInterface.Frame] = [77],
        [ShaderFrameInterface.TickRate] = [((uint)Puck.Hosting.EngineTicks.PerSecond)],
        [ShaderFrameInterface.PointerDown] = [1],
        [ShaderFrameInterface.PointerPresses] = [12],
        [ShaderFrameInterface.CameraPosition] = [Bits(value: 1.5f), Bits(value: 2.5f), Bits(value: 3.5f)],
        [ShaderFrameInterface.CameraFov] = [Bits(value: 0.75f)],
        [ShaderFrameInterface.CameraTarget] = [Bits(value: 4.5f), Bits(value: 5.5f), Bits(value: 6.5f)],
        [ShaderFrameInterface.CameraUp] = [Bits(value: 7.5f), Bits(value: 8.5f), Bits(value: 9.5f)],
    };

    public static TheoryData<string> ShippedSources => new(values: [
        "src/Puck.World/Assets/pipelines/ink.graph.json",
        "tests/Puck.World.Canaries/pipeline-package/tint.graph.json",
        "src/Puck.World/Assets/pipelines/moth.hlsl",
        "worlds/genesis/card.hlsl",
    ]);

    private static uint Bits(float value) => BitConverter.SingleToUInt32Bits(value: value);
    private static byte[] HostBlock(ShaderPipelineParameterLayout layout) {
        var block = new byte[layout.SizeBytes];

        layout.WriteFrame(
            block: block,
            frame: 77,
            height: 360,
            tick: 0x0123456789ABCDEFUL,
            values: Values,
            width: 640
        );

        return block;
    }
    // Holds every frame member the module reflects to the word the host writer put at the reflected offset.
    private static int AssertHostWords(ShaderInterfaceBinding reflected, byte[] block) {
        var checkedMembers = 0;

        foreach (var member in reflected.Members) {
            if (!Expected.TryGetValue(
                key: member.Name,
                value: out var words
            )) {
                continue;
            }

            for (var word = 0; (word < words.Length); word++) {
                Assert.Equal(
                    actual: BinaryPrimitives.ReadUInt32LittleEndian(source: block.AsSpan(start: (((int)member.Offset) + (word * 4)))),
                    expected: words[word]
                );
            }

            checkedMembers++;
        }

        return checkedMembers;
    }

    [Fact]
    public void The_host_writer_puts_every_frame_member_where_the_layout_places_it() {
        var layout = ShaderPipelineParameterLayout.For(
            config: null,
            interfaceName: "writer"
        );
        var block = HostBlock(layout: layout);
        var group = layout.Layout.PushedGroup!;

        Assert.Equal(
            actual: AssertHostWords(
                block: block,
                reflected: group.Bindings[0]
            ),
            expected: ShaderFrameInterface.Members.Count
        );
    }
    [MemberData(memberName: nameof(ShippedSources))]
    [Theory]
    public void A_shipped_pass_reads_every_frame_member_where_the_host_writer_puts_it(string source) {
        Assert.SkipWhen(
            condition: (new ShaderToolchain().Locate(name: ShaderCompiler.DxcTool) is null),
            reason: "DXC is required to compile the shipped pipeline sources."
        );

        var cache = Directory.CreateTempSubdirectory(prefix: "puck-frame-block-");

        try {
            var result = new ShaderPipelineLoader(compiler: new ShaderCompiler(cacheDirectory: cache.FullName)).Load(
                cancellationToken: TestContext.Current.CancellationToken,
                name: Path.GetFileNameWithoutExtension(path: source),
                path: RepositoryPaths.Resolve(relativePath: source)
            );

            Assert.True(
                condition: (result.Status == ShaderPipelineLoadStatus.Compiled),
                userMessage: result.Message
            );


            foreach (var pass in result.Pipeline!.Plan.Passes) {
                var layout = pass.Parameters;
                var block = HostBlock(layout: layout);
                var shader = result.Pipeline.Shaders[pass.Name];

                foreach (var (_, module) in shader.SpirvByStage) {
                    var reflected = SpirvInterfaceReader.Read(module: module.Span);

                    Assert.Null(@object: layout.Layout.PushedBlockMismatch(reflected: reflected));

                    if (reflected.SingleOrDefault(predicate: static binding => binding.Pushed) is { } pushed) {
                        Assert.Equal(
                            actual: AssertHostWords(
                                block: block,
                                reflected: pushed
                            ),
                            expected: ShaderFrameInterface.Members.Count
                        );
                    }
                }

                if (OperatingSystem.IsWindows()) {
                    using var dxil = DxilInterfaceReader.Load(toolchain: new ShaderToolchain());

                    foreach (var (_, container) in shader.DxilByStage) {
                        Assert.Null(@object: layout.Layout.PushedBlockMismatch(reflected: dxil.Read(container: container.Span)));
                    }
                }
            }
        } finally {
            cache.Delete(recursive: true);
        }
    }
    [Fact]
    public void The_film_grain_set_includes_the_declarations_its_frame_layout_generates() {
        var directory = RepositoryPaths.Resolve(relativePath: "src/Puck.SdfVm/Assets/Shaders/Sdf");
        var manifest = ShaderSetManifest.Load(manifestPath: Path.Combine(
            path1: AppContext.BaseDirectory,
            path2: "Assets",
            path3: "Shaders",
            path4: "Sdf/sdf-film-grain.puck.shader.json"
        ));
        var shaderInterface = manifest.FrameLayout.Interface;

        Assert.Equal(
            actual: File.ReadAllText(path: Path.Combine(
                path1: directory,
                path2: ShaderFrameInterface.IncludeFileName(interfaceName: shaderInterface.Name)
            )),
            expected: ShaderInterfaceHlsl.Generate(shaderInterface: shaderInterface)
        );
        Assert.Null(@object: manifest.FrameLayout.Layout.PushedBlockMismatch(reflected: SpirvInterfaceReader.Read(module: manifest.Bytecode["sdf-film-grain.frag.spv"].Span)));
    }
}
