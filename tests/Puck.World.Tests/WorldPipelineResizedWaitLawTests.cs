using Puck.Shaders;
using Puck.Testing;
using Puck.World.Client;

using Xunit;

namespace Puck.World.Tests;

/// <summary>
/// Laws for <c>pipeline.wait &lt;name&gt; resized &lt;width&gt; &lt;height&gt;</c>. A render node builds a resize's
/// pipelines off the frame thread, and the instance keeps presenting the old extent while it builds, so the phase holds
/// until the graph at the new extent has installed, and a script that resets after it counts every submission at the new
/// extent. A resize is not a step, so a paused instance builds and installs it the same way. The node's pipeline factory
/// is held the way a cold driver cache holds it.
/// </summary>
public sealed class WorldPipelineResizedWaitLawTests {
    private const uint Extent = 8;

    private static CompiledShaderPipeline Fill() {
        var plan = new ShaderPipelineCompiler().Compile(definition: new ShaderPipelineDefinition(
            name: "fill",
            outputs: ["image"],
            passes: [new ShaderPipelinePass(
                EntryPoint: "main",
                Inputs: [],
                Kind: ShaderPipelineDocumentPassKind.Compute,
                Name: "fill",
                Outputs: [new ResourceReference(
                    Binding: 0,
                    Name: "image"
                )],
                Source: "fill.hlsl"
            )],
            resources: [new ShaderPipelineResource(
                Dimensions: ShaderPipelineDimensions.Relative(),
                Format: "R8G8B8A8Unorm",
                Name: "image"
            )]
        ));
        var bytecode = new Dictionary<ShaderStage, ReadOnlyMemory<byte>> { [ShaderStage.Compute] = new byte[] { 0x03, 0x02, 0x23, 0x07 } };

        return new CompiledShaderPipeline(
            plan: plan,
            shaders: new Dictionary<string, CompiledShader> {
                ["fill"] = new(
                    diagnostics: [],
                    dxil: bytecode,
                    name: "fill",
                    sourceHash: "fill",
                    sourcePath: "fill.hlsl",
                    spirv: bytecode
                ),
            }
        );
    }

    [InlineData(false)]
    [InlineData(true)]
    [Theory]
    public void AResizedWaitHoldsUntilTheGraphAtTheNewExtentInstallsPausedOrRunning(bool paused) {
        using var directory = new TemporaryDirectory();
        using var gate = new ManualResetEventSlim(initialState: true);
        var reports = new List<string>();
        var gpu = new FakeGpuDevice(reportVersion: 0) {
            BeforeComputePipeline = () => gate.Wait(),
        };
        using var node = new ShaderPipelineRenderNode(
            deviceContext: gpu,
            height: Extent,
            hostsOnDirectX: false,
            name: "fill",
            width: Extent
        );
        using var runtime = new WorldPipelineRuntime(
            documentDirectory: directory.RootPath,
            packager: new ShaderPackager(compiler: new ShaderCompiler(
                cacheDirectory: directory.PathOf(name: "cache"),
                toolchainDirectory: directory.PathOf(name: "no-tools")
            ))
        ) {
            Report = (name, message) => reports.Add(item: $"[pipeline: {name} {message}]"),
        };

        runtime.Register(
            name: "fill",
            node: node
        );
        node.Swap(pipeline: Fill());
        Assert.True(condition: SpinWait.SpinUntil(
            condition: () => {
                _ = node.ProduceFrame(context: default);

                return node.IsReady;
            },
            timeout: TimeSpan.FromSeconds(value: 30)
        ));
        node.Paused = paused;

        // The driver holds every pipeline creation: the resize cannot install. The gate opens however the law ends, so the
        // node's disposal never waits on a held build.
        gate.Reset();

        Func<bool> hold;

        try {
            node.Resize(
                height: (Extent * 2),
                width: (Extent * 2)
            );
            hold = runtime.ArmWait(
                extent: ((Extent * 2), (Extent * 2)),
                name: "fill",
                phase: WorldPipelinePhase.Resized,
                seconds: 30,
                submissions: 0
            );

            for (var frame = 0; (frame < 8); frame++) {
                _ = node.ProduceFrame(context: default);
            }

            Assert.Equal(
                actual: (Held: hold(), Extent: node.Extent),
                expected: (Held: true, Extent: (Extent, Extent))
            );
        } finally {
            gate.Set();
        }

        Assert.True(condition: SpinWait.SpinUntil(
            condition: () => {
                _ = node.ProduceFrame(context: default);

                return !hold();
            },
            timeout: TimeSpan.FromSeconds(value: 30)
        ));
        Assert.Equal(
            actual: (Extent: node.Extent, Paused: node.Paused),
            expected: (Extent: ((Extent * 2), (Extent * 2)), Paused: paused)
        );

        Assert.Single(
            collection: reports,
            predicate: static line => (line == "[pipeline: fill wait resized 16 16 reached]")
        );
    }
}
