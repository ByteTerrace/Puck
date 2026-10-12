using System.Buffers.Binary;
using System.Numerics;
using Puck.Testing;

namespace Puck.Shaders.Tests;

/// <summary>A pass reads its frame values and extent only through the declarations generated from its interface, and the
/// host writes them through <see cref="ShaderPipelineParameterLayout.WriteFrame"/> and
/// <see cref="ShaderPipelineParameterLayout.WriteExtent"/>. These laws compile every shipped pass, find where DXC placed
/// each member in both bytecodes of every pass that reads a block (DXC drops a block its configuration never reads),
/// and hold the bytes the host writer put there to the value it was given; and they hold the checked-in declarations of
/// the shipped film grain set to the generator.</summary>
public sealed class ShaderFrameBlockLawTests {
    public static TheoryData<string> ShippedSources => new(values: [
        "tests/Puck.World.Canaries/pipeline-package/tint.graph.json",
        "worlds/genesis/card.hlsl",
    ]);

    [MemberData(memberName: nameof(ShippedSources))]
    [Theory]
    public void A_shipped_pass_reads_every_frame_member_where_the_host_writer_puts_it(string source) {
        Assert.SkipWhen(
            condition: (new ShaderToolchain().Locate(name: ShaderCompiler.DxcTool) is null),
            reason: "DXC is required to compile the shipped pipeline sources."
        );

        using var cache = new TemporaryDirectory(prefix: "puck-frame-block-");

        var result = new ShaderPipelineLoader(compiler: new ShaderCompiler(cacheDirectory: cache.RootPath)).Load(
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
            var blocks = HostBlocks(layout: layout);
            var shader = result.Pipeline.Shaders[pass.Name];

            foreach (var (_, module) in shader.SpirvByStage) {
                var reflected = SpirvInterfaceReader.Read(module: module.Span);

                Assert.Null(@object: layout.Layout.Mismatch(reflected: reflected));
                AssertHostBlocks(
                    blocks: blocks,
                    reflected: reflected
                );
            }

            if (OperatingSystem.IsWindows()) {
                using var dxil = DxilInterfaceReader.Load(toolchain: new ShaderToolchain());

                foreach (var (_, container) in shader.DxilByStage) {
                    Assert.Null(@object: layout.Layout.Mismatch(reflected: dxil.Read(container: container.Span)));
                }
            }
        }
    }
}
