using Puck.Shaders;
using Puck.Testing;

using Xunit;

namespace Puck.World.Client.Tests;

/// <summary>
/// Laws for <c>pipeline.wait &lt;name&gt; resized &lt;width&gt; &lt;height&gt;</c>. A render node builds a resize's
/// pipelines off the frame thread, and the instance keeps presenting the old extent while it builds, so the phase holds
/// until the graph at the new extent has installed, and a script that resets after it counts every submission at the new
/// extent. A resize is not a step, so a paused instance builds and installs it the same way. A resize joins the pass
/// pipelines the installed graph already leases, so its build creates nothing a law could hold; the hold is shown on the
/// frame that starts the build, which never takes it, however fast the build finishes. The wait's deadline is the
/// liveness bound of every wait for work on the thread pool.
/// </summary>
public sealed class WorldPipelineResizedWaitLawTests {
    private const uint Extent = 8;

    private static CompiledShaderPipeline Fill() {
        var plan = new ShaderPipelineCompiler().Compile(definition: new RenderGraphDefinition(
            name: "fill",
            outputs: ["image"],
            passes: [new ShaderPipelinePass(
                EntryPoint: "main",
                Inputs: [],
                Kind: ShaderPipelineDocumentPassKind.Compute,
                Name: "fill",
                Outputs: ["image"],
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
        var reports = new List<string>();
        var gpu = new FakeGpuDevice();
        using var node = new ShaderPipelineRenderNode(
            pipelines: new GpuPassPipelineCache(),
            deviceContext: gpu,
            height: Extent,
            hostsOnDirectX: false,
            name: "fill",
            width: Extent
        );
        using var runtime = new WorldViewGraphHost(
            documentDirectory: directory.RootPath,
            packager: new ShaderPackager(compiler: new ShaderCompiler(
                cacheDirectory: directory.PathOf(name: "cache"),
                toolchainDirectory: directory.PathOf(name: "no-tools")
            ))
        ) {
            Report = (name, message) => reports.Add(item: $"[pipeline: {name} {message}]"),
        };

        using var instances = FakeGraphInstances.Attach(
            create: _ => node,
            host: runtime
        );

        runtime.Reconcile(views: new WorldViewDefaults(Graphs: [new WorldViewGraph(
            Name: "fill",
            Source: "fill.hlsl"
        )]));
        node.Swap(pipeline: Fill());
        TestLiveness.Until(
            step: () => {
                _ = node.ProduceFrame(context: default);

                return node.IsReady;
            }
        );
        node.Paused = paused;
        node.Resize(
            height: (Extent * 2),
            width: (Extent * 2)
        );

        var hold = runtime.ArmWait(
            extent: ((Extent * 2), (Extent * 2)),
            name: "fill",
            phase: WorldPipelinePhase.Resized,
            seconds: ((int)TestLiveness.Bound.TotalSeconds),
            submissions: 0
        );

        // The frame that starts the resize's build presents the old extent, and the wait holds.
        _ = node.ProduceFrame(context: default);
        Assert.Equal(
            actual: (Held: hold(), Extent: node.Extent),
            expected: (Held: true, Extent: (Extent, Extent))
        );

        TestLiveness.Until(
            step: () => {
                _ = node.ProduceFrame(context: default);

                return !hold();
            }
        );
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
