using System.Text.Json;
using Puck.Abstractions.Counting;
using Puck.Abstractions.Gpu;
using Puck.Hosting;
using Puck.Testing;

namespace Puck.Shaders.Tests;

/// <summary>
/// Laws of the <c>post.&lt;id&gt;</c> package (<see cref="PostProcessPackage"/>) on <see cref="FakePipelineGpu"/>: the
/// shipped film-grain set run as a package pass of a graph records a pinned recording over its input (the render pass
/// and graphics pipeline it is created for, the vertex buffer and the draw, the input written at the set's source
/// binding, and the frame group and pass blocks the draw reads byte for byte, bound config and live changes included,
/// with nothing pushed); it is handed its input shader-readable and its target in render-target layout by the node's
/// planned barriers and records none of its own; its pipeline is built off the frame thread, shared with a graph that
/// replaces its own, and released on device loss and disposal; a config that does not bind is refused by the graph compiler by name; and a
/// steady frame allocates nothing.
/// </summary>
public sealed class PostProcessPackageLawTests {
    private const uint Extent = 64;
    private const string Pass = "grain";

    private static readonly ShaderPipelineExternalImage Input = new(
        Format: GpuPixelFormat.R8G8B8A8Unorm,
        Height: Extent,
        ImageHandle: 0x7000,
        ImageViewHandle: 0x7001,
        Layout: GpuImageLayout.ShaderReadOnly,
        Width: Extent
    );

    private static string ShadersDirectory => Path.Combine(
        path1: AppContext.BaseDirectory,
        path2: "Assets",
        path3: "Shaders"
    );

    private static ShaderSetManifest FilmGrain() => ShaderSetManifest.Load(manifestPath: Path.Combine(
        path1: ShadersDirectory,
        path2: "Sdf",
        path3: "sdf-film-grain.puck.shader.json"
    ));
    private static RenderGraphPackageCatalog Catalog() => RenderGraphPackageCatalog.WithPostProcess(postProcess: ShaderSetCatalog.Scan(rootDirectory: ShadersDirectory));
    private static RenderGraphDefinition Graph(JsonElement? config, string package = "post.sdf-film-grain") => new(
        Name: "post",
        Outputs: ["output"],
        Packages: [new RenderGraphPackagePass(
            Config: config,
            Inputs: ["input"],
            Name: Pass,
            Outputs: ["output"],
            Package: package
        )],
        Resources: [
            new ShaderPipelineResource(
                Dimensions: ShaderPipelineDimensions.Relative(),
                Format: "R8G8B8A8Unorm",
                Initialization: ShaderPipelineInitialization.External,
                Name: "input"
            ),
            new ShaderPipelineResource(
                Dimensions: ShaderPipelineDimensions.Relative(),
                Format: "R8G8B8A8Unorm",
                Name: "output"
            ),
        ],
        Schema: RenderGraphSchemas.Graph
    );
    private static JsonElement Json(string text) => JsonDocument.Parse(json: text).RootElement.Clone();
    // A node running the film-grain set as the graph's one package pass over the bound input, through the factory a law
    // wraps it in, if any.
    private static ShaderPipelineRenderNode PackageNode(FakePipelineGpu gpu, JsonElement? config, Func<PostProcessPackage, IRenderGraphPackageFactory>? wrap = null, GpuCreationFaults? faults = null) {
        var manifest = FilmGrain();
        var package = new PostProcessPackage(manifest: manifest);
        var packages = new RenderGraphPackageRecorders();

        packages.Register(
            factory: (wrap?.Invoke(arg: package) ?? package),
            package: package.Id
        );

        var plan = new RenderGraphCompiler(packages: Catalog()).Compile(definition: Graph(config: config));
        var node = new ShaderPipelineRenderNode(
            pipelines: new GpuPassPipelineCache(),
            deviceContext: ((faults is null)
                ? gpu
                : new FaultingDevice(
                    faults: faults,
                    gpu: gpu
                )),
            height: Extent,
            hostsOnDirectX: false,
            name: "post",
            outputLayout: GpuImageLayout.ShaderReadOnly,
            packages: packages,
            width: Extent
        );

        node.Swap(pipeline: new CompiledShaderPipeline(
            plan: plan.Pipeline,
            shaders: new Dictionary<string, CompiledShader>(comparer: StringComparer.Ordinal)
        ));
        node.BindImage(
            image: Input,
            name: "input"
        );

        return node;
    }
    // The recording the law pins for four frames: the fullscreen pass the set is drawn by, with no push and no combined
    // sampler; the input written at the set's source binding; and the two blocks each draw reads, in hex. The frame group
    // block holds the frame counter, counting from one, and the tick rate; the pass block holds the 64x64 extent, the
    // default flicker rate of 24, then the bound config (intensity, seed, then the set's remaining fields).
    private static Recorded Pinned(string configHex) => new(
        Blocks: [.. Enumerable.Range(count: 4, start: 1).Select(selector: frame => $"{new string(c: '0', count: 48)}{frame:X2}000000E0C40000{new string(c: '0', count: 128)} 400000004000000018000000{configHex}00000000")],
        Commands: [.. Enumerable.Repeat(count: 4, element: new[] { "vertices 24 8", "draw 0 3" }).SelectMany(selector: static pair => pair)],
        Pipeline: "sdf-film-grain  8 GpuVertexAttribute { Location = 0, Format = R32G32Float, OffsetBytes = 0 }",
        RenderPass: "GpuColorAttachment { Format = R8G8B8A8Unorm, Load = Clear, Store = Store, FinalLayout = RenderTarget } ",
        RenderPasses: 4,
        Writes: [.. Enumerable.Repeat(count: 4, element: $"1 {Input.ImageViewHandle}")]
    );
    private static void ProduceUntilPublished(IRenderNode node) => Assert.True(
        condition: SpinWait.SpinUntil(
            condition: () => !node.ProduceFrame(context: default).IsEmpty,
            timeout: TimeSpan.FromSeconds(value: 30)
        ),
        userMessage: "The pass never published a frame."
    );
    // What a law compares of one pass's recording: the render pass and pipeline it was created for, the graphics
    // commands, the input written at a binding, and the frame and pass blocks each frame's draw reads, read from the
    // constant buffers of the sets it bound as the frame is recorded.
    private static Recorded Record(FakePipelineGpu gpu, IRenderNode node, Action<int>? before = null) {
        var blocks = new List<string>();
        var layout = FilmGrain().FrameLayout;

        ProduceUntilPublished(node: node);
        gpu.Recording = true;

        for (var frame = 0; (frame < 4); frame++) {
            before?.Invoke(obj: frame);

            var bound = gpu.BoundSets.Count;

            _ = node.ProduceFrame(context: default);

            var sets = gpu.BoundSets.Skip(count: bound).ToArray();
            var frameBlock = gpu.ConstantBlock(
                set: sets.Last(predicate: static set => (set.Group == 0U)).Set,
                sizeBytes: ((int)layout.FrameBlockSizeBytes)
            );
            var passBlock = gpu.ConstantBlock(
                set: sets.Last(predicate: static set => (set.Group == 3U)).Set,
                sizeBytes: ((int)layout.SizeBytes)
            );

            blocks.Add(item: $"{Convert.ToHexString(inArray: frameBlock)} {Convert.ToHexString(inArray: passBlock)}");
        }

        gpu.Recording = false;

        var (pass, description) = Assert.Single(collection: gpu.GraphicsPipelines);

        return new Recorded(
            Blocks: blocks,
            Commands: [.. gpu.GraphicsCommands.Select(selector: static command => $"{command.Command} {command.SizeBytes} {command.Count}")],
            Pipeline: $"{description.Name} {description.DepthCompare} {description.VertexInput.StrideBytes} {string.Join(separator: ",", values: description.VertexInput.Attributes)}",
            RenderPass: $"{string.Join(separator: ",", values: pass.Colors)} {pass.Depth}",
            RenderPasses: gpu.RenderPasses.Count,
            Writes: [.. gpu.DescriptorWrites.Select(selector: static write => $"{write.Binding} {write.Handle}")]
        ) {
            Pushes = [.. gpu.PushedConstants.Select(selector: static push => Convert.ToHexString(inArray: push.Data))],
        };
    }
    private static void AssertSameRecording(Recorded expected, Recorded package) {
        Assert.Equal(
            expected: expected.RenderPass,
            actual: package.RenderPass
        );
        Assert.Equal(
            expected: expected.Pipeline,
            actual: package.Pipeline
        );
        Assert.Equal(
            expected: expected.Commands,
            actual: package.Commands
        );
        Assert.Equal(
            expected: expected.Writes,
            actual: package.Writes
        );
        Assert.Equal(
            expected: expected.Blocks,
            actual: package.Blocks
        );
        Assert.Empty(collection: package.Pushes);
        Assert.Equal(
            expected: expected.RenderPasses,
            actual: package.RenderPasses
        );
        Assert.Equal(
            expected: 4,
            actual: package.RenderPasses
        );
    }

    [InlineData(null, "CDCC4C3D000000000000803F00000000")]
    [InlineData("""{"intensity":0.3,"seed":7}""", "9A99993E070000000000803F00000000")]
    [Theory]
    public void APostPassRecordsThePinnedRecordingForItsSetAndConfig(string? config, string configHex) {
        var gpu = new FakePipelineGpu();
        using var package = PackageNode(
            config: ((config is null)
                ? null
                : Json(text: config)),
            gpu: gpu
        );

        AssertSameRecording(
            expected: Pinned(configHex: configHex),
            package: Record(
                gpu: gpu,
                node: package
            )
        );
    }
    [Fact]
    public void ALiveConfigChangeReachesThePassBlockFromTheNextFrame() {
        var gpu = new FakePipelineGpu();
        using var package = PackageNode(
            config: null,
            gpu: gpu
        );
        var recorded = Record(
            before: frame => Assert.True(condition: ((frame != 2) || package.TrySetConfig(
                config: Json(text: """{"intensity":0.75}"""),
                passName: Pass,
                reason: out _
            ))),
            gpu: gpu,
            node: package
        );
        var before = Pinned(configHex: "CDCC4C3D000000000000803F00000000");
        var after = Pinned(configHex: "0000403F000000000000803F00000000");

        AssertSameRecording(
            expected: (before with {
                Blocks = [.. before.Blocks.Take(count: 2), .. after.Blocks.Skip(count: 2)],
            }),
            package: recorded
        );
    }
    [Fact]
    public void APostPassDrawsIntoItsPlannedLayoutsAndRecordsNoBarrierOfItsOwn() {
        var gpu = new FakePipelineGpu();
        ObservedPackageFactory? observed = null;
        using var node = PackageNode(
            config: null,
            gpu: gpu,
            wrap: package => (observed = new ObservedPackageFactory(
                barriers: () => gpu.Barriers.Count,
                inner: package
            ))
        );

        ProduceUntilPublished(node: node);
        gpu.Recording = true;

        for (var frame = 0; (frame < 4); frame++) {
            _ = node.ProduceFrame(context: default);
        }

        gpu.Recording = false;

        // The node records the barriers around the draw: every frame the target moves into render-target layout before
        // it and on to publication after it.
        Assert.NotEmpty(collection: gpu.Barriers);
        Assert.Equal(
            expected: (0, (GpuImageLayout.ShaderReadOnly, GpuImageLayout.RenderTarget), RenderGraphPackageOutcome.Drew),
            actual: (observed!.PackageBarriers, observed.Layouts, observed.Outcome)
        );
        Assert.True(condition: (observed.Records >= 4));
    }
    [Fact]
    public void AConfigThatDoesNotBindIsRefusedByTheGraphCompilerNamingThePass() {
        var compiler = new RenderGraphCompiler(packages: Catalog());
        var outOfRange = Assert.Throws<ShaderPipelineCompilationException>(testCode: () => compiler.Compile(definition: Graph(config: Json(text: """{"intensity":5}"""))));
        var unconfigured = Assert.Throws<ShaderPipelineCompilationException>(testCode: () => compiler.Compile(definition: Graph(
            config: Json(text: "{}"),
            package: RenderGraphPackageCatalog.Overlay
        )));

        foreach (var refusal in new[] { outOfRange, unconfigured }) {
            var diagnostic = Assert.Single(collection: refusal.Diagnostics);

            Assert.Equal(
                expected: ("RENDERGRAPH_PACKAGE_CONFIG", Pass),
                actual: (diagnostic.Code, diagnostic.Name)
            );
        }
    }
    [Fact]
    public void ThePipelineIsBuiltOffTheFrameThreadSharedWithTheGraphThatReplacesItAndReleasedOnDeviceLossAndDisposal() {
        var gpu = new FakePipelineGpu();
        var node = PackageNode(
            config: null,
            gpu: gpu
        );

        ProduceUntilPublished(node: node);

        var first = Assert.Single(
            collection: gpu.CreatedObjects,
            predicate: static created => (created.Kind == "graphics pipeline")
        );

        Assert.NotEqual(
            expected: Environment.CurrentManagedThreadId,
            actual: first.ThreadId
        );
        Assert.Equal(
            expected: 0,
            actual: first.DisposeCount
        );

        // A resize replaces the graph with the same set's pass, whose pipeline key is unchanged: the replacement joins the
        // pass-pipeline cache's entry, so the replaced graph's retirement releases its lease and disposes nothing.
        node.Resize(
            height: (Extent / 2),
            width: (Extent / 2)
        );
        Assert.True(
            condition: SpinWait.SpinUntil(
                condition: () => {
                    _ = node.ProduceFrame(context: default);

                    return (node.Extent == ((Extent / 2), (Extent / 2)));
                },
                timeout: TimeSpan.FromSeconds(value: 30)
            ),
            userMessage: "The resize never installed."
        );
        _ = node.ProduceFrame(context: default);
        _ = node.ProduceFrame(context: default);
        Assert.Equal(
            expected: (Pipelines: 1, Disposed: 0),
            actual: (Pipelines: gpu.CreatedObjects.Count(predicate: static created => (created.Kind == "graphics pipeline")), Disposed: first.DisposeCount)
        );

        // A device loss releases the graph's lease, the entry's last, which disposes the pipeline; the rebuild after it
        // creates another, off the frame thread again.
        node.OnDeviceLost();
        Assert.Equal(
            expected: 1,
            actual: first.DisposeCount
        );
        node.BindImage(
            image: Input,
            name: "input"
        );
        ProduceUntilPublished(node: node);

        var second = gpu.CreatedObjects.Last(predicate: static created => (created.Kind == "graphics pipeline"));

        Assert.NotSame(
            actual: second,
            expected: first
        );
        Assert.NotEqual(
            expected: Environment.CurrentManagedThreadId,
            actual: second.ThreadId
        );
        node.Dispose();
        Assert.All(
            action: static created => Assert.True(
                condition: (created.DisposeCount == 1),
                userMessage: created.ToString()
            ),
            collection: gpu.CreatedObjects.Where(predicate: static created => (created.Kind is "graphics pipeline" or "Vertex module" or "Fragment module" or "render pass" or "framebuffer" or "sampler" or "Vertex buffer")).ToArray()
        );
    }
    [Fact]
    public void ASteadyPostPassFrameAllocatesNothing() {
        var gpu = new FakePipelineGpu();
        using var node = PackageNode(
            config: null,
            gpu: gpu
        );

        ProduceUntilPublished(node: node);

        for (var frame = 0; (frame < 4); frame++) {
            _ = node.ProduceFrame(context: default);
        }

        Assert.Equal(
            actual: AllocationWindow.Least(window: () => {
                for (var frame = 0; (frame < 64); frame++) {
                    _ = node.ProduceFrame(context: default);
                }
            }),
            expected: 0L
        );
    }
    /// <summary>Every creation a post pass's install makes, failed in turn through <see cref="GpuCreationFaults"/>, is
    /// refused by name without escaping a produced frame, releases exactly what was created before it, and the same graph
    /// swapped in again installs and publishes. The recorder's framebuffers are among the creations, made at install.</summary>
    [Fact]
    public void EveryCreationOfAPostPassFaultedInTurnIsRefusedByNameAndReleasesWhatWasCreated() {
        var expected = new Dictionary<GpuCreationKind, long>();
        var measuredFaults = new GpuCreationFaults();

        using (var measured = PackageNode(
            config: null,
            faults: measuredFaults,
            gpu: new FakePipelineGpu()
        )) {
            ProduceUntilPublished(node: measured);

            foreach (var kind in GpuCreationFaults.Kinds) {
                expected[kind] = measuredFaults.SeenOf(kind: kind);
            }
        }

        Assert.True(condition: (expected[GpuCreationKind.Framebuffer] > 0L));

        var faulted = 0;

        foreach (var kind in GpuCreationFaults.Kinds) {
            for (var nth = 1; (nth <= expected[kind]); nth++) {
                var gpu = new FakePipelineGpu();
                var faults = new GpuCreationFaults();
                using var node = PackageNode(
                    config: null,
                    faults: faults,
                    gpu: gpu
                );

                faults.Arm(
                    kind: kind,
                    nth: nth
                );
                Assert.True(
                    condition: SpinWait.SpinUntil(
                        condition: () => {
                            _ = node.ProduceFrame(context: default);

                            return (node.LastSwapError is not null);
                        },
                        timeout: TimeSpan.FromSeconds(value: 30)
                    ),
                    userMessage: $"The {GpuCreationFaults.NameOf(kind: kind)} creation {nth} fault was never reported."
                );

                var fault = Assert.IsType<GpuCreationFaultException>(@object: node.LastSwapError);

                Assert.Equal(
                    actual: (fault.Kind, fault.Creation),
                    expected: (kind, nth)
                );
                Assert.Equal(
                    actual: $"{kind} {nth}: unreleased [{string.Join(separator: ", ", values: gpu.CreatedObjects.Where(predicate: static created => (created.DisposeCount != 1)))}]",
                    expected: $"{kind} {nth}: unreleased []"
                );

                // The same graph swapped in again installs and publishes.
                node.Swap(pipeline: new CompiledShaderPipeline(
                    plan: new RenderGraphCompiler(packages: Catalog()).Compile(definition: Graph(config: null)).Pipeline,
                    shaders: new Dictionary<string, CompiledShader>(comparer: StringComparer.Ordinal)
                ));
                ProduceUntilPublished(node: node);
                Assert.Null(@object: node.LastSwapError);
                faulted++;
            }
        }

        Assert.Equal(
            actual: faulted,
            expected: expected.Values.Sum()
        );
    }

    // The fake as a device context whose services pass through creation faults, as a backend's do.
    private sealed class FaultingDevice(FakePipelineGpu gpu, GpuCreationFaults faults) : IGpuDeviceContext {
        public long AdapterLuid => gpu.AdapterLuid;
        public GpuDeviceCapabilities? Capabilities => gpu.Capabilities;
        public GpuDeviceIdentity? Identity => gpu.Identity;
        public GpuMemoryProfile MemoryProfile => gpu.MemoryProfile;
        public GpuDeviceServices Services { get; } = GpuCreationFaults.Wrap(
            faults: faults,
            services: gpu.Services
        );

        public void WaitIdle() => gpu.WaitIdle();
    }
    private sealed record Recorded(string RenderPass, string Pipeline, IReadOnlyList<string> Commands, IReadOnlyList<string> Writes, IReadOnlyList<string> Blocks, int RenderPasses) {
        // Every push-constant write, which a post pass never makes.
        public IReadOnlyList<string> Pushes { get; init; } = [];
    }
}
