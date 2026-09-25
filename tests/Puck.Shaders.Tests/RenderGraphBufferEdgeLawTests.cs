using Puck.Abstractions.Gpu;
using Puck.Hosting;

namespace Puck.Shaders.Tests;

/// <summary>
/// Laws for buffer edges: <c>sdf.bricks</c> writes the world's brick pool as a counted buffer output; a package pass
/// reading that buffer is planned with a buffer barrier into its read state, both within one graph and across instances,
/// where the buffer is the consumer's external version and each plan states the kind of the instance edge it binds; and
/// a package port bound to a version of the other kind, or to a buffer of another stride or count, is refused naming the
/// pass, the version and the port.
/// </summary>
public sealed class RenderGraphBufferEdgeLawTests {
    // A package reading the brick pool, as a view will once the SDF engine reads the pool it is joined to.
    private const string Reader = "probe.bricks";

    private static RenderGraphPackageCatalog Catalog { get; } = new(packages: [
        .. RenderGraphPackageCatalog.Engine.Packages,
        new RenderGraphPackage(
            Id: Reader,
            Inputs: [RenderGraphPackageCatalog.BrickPool with { Access = RenderGraphPortAccess.ComputeRead }],
            Outputs: [RenderGraphPackagePort.Image(access: RenderGraphPortAccess.ComputeWrite)],
            Summary: "A view reading the brick pool."
        ),
    ]);

    private static ShaderPipelineResource Bricks(ShaderPipelineInitialization initialization = ShaderPipelineInitialization.Undefined, uint? strideBytes = 4) => new(
        Count: [new ShaderPipelineCountTerm(Per: [ShaderPipelineCountBasis.BrickPoolVoxels])],
        Initialization: initialization,
        Kind: ShaderPipelineResourceKind.Buffer,
        Name: "bricks",
        StrideBytes: strideBytes
    );
    private static ShaderPipelineResource Image(string name) => new(
        Dimensions: ShaderPipelineDimensions.Relative(),
        Format: nameof(GpuPixelFormat.R8G8B8A8Unorm),
        Name: name
    );
    private static RenderGraphPackagePass Bake(string output = "bricks") => new(
        Name: "bake",
        Outputs: [output],
        Package: RenderGraphPackageCatalog.SdfBricks
    );
    private static RenderGraphPackagePass View(string input = "bricks") => new(
        Inputs: [input],
        Name: "view",
        Outputs: ["scene"],
        Package: Reader
    );
    private static RenderGraphDefinition Graph(IReadOnlyList<ShaderPipelineResource> resources, IReadOnlyList<string> outputs, params RenderGraphPackagePass[] packages) => new(
        Name: "bricks-edge",
        Outputs: outputs,
        Packages: packages,
        Resources: resources,
        Schema: RenderGraphSchemas.Graph
    );
    private static RenderGraphPlan Plan(RenderGraphDefinition definition) => new RenderGraphCompiler(packages: Catalog).Compile(definition: definition);
    private static IReadOnlyList<ShaderPipelineDiagnostic> Refusals(RenderGraphDefinition definition) => Assert.Throws<ShaderPipelineCompilationException>(testCode: () => Plan(definition: definition)).Diagnostics;
    private static ShaderPipelineAccess AccessOf(RenderGraphPlan plan, string pass, string version) => plan.Pipeline.Passes.Single(predicate: planned => (planned.Name == pass)).Accesses.Single(predicate: access => (access.Version == version));
    private static void AssertBufferRead(ShaderPipelineAccess access) {
        Assert.Equal(expected: ShaderPipelineBarrierKind.Buffer, actual: access.Barrier.Kind);
        Assert.Equal(expected: GpuAccess.ShaderRead, actual: access.Use.Access);
        Assert.Equal(expected: GpuAccess.ShaderRead, actual: access.Barrier.DestinationAccess);
        Assert.Equal(expected: GpuStage.ComputeShader, actual: access.Barrier.DestinationStage);
        Assert.Equal(expected: GpuImageLayout.Undefined, actual: access.Use.Layout);
    }

    [Fact]
    public void SdfBricksWritesTheBrickPoolAsACountedBufferOutput() {
        var plan = Plan(definition: Graph(
            outputs: ["bricks"],
            packages: Bake(),
            resources: [Bricks()]
        ));
        var write = AccessOf(
            pass: "bake",
            plan: plan,
            version: "bricks"
        );

        Assert.Equal(expected: RenderGraphPackageCatalog.SdfBricks, actual: Assert.Single(collection: plan.Steps).Package?.Id);
        Assert.Equal(expected: ShaderPipelineResourceKind.Buffer, actual: plan.KindOf(version: "bricks"));
        Assert.True(condition: write.Use.Writes);
        Assert.Equal(
            actual: plan.Pipeline.Storages[write.Storage].Declaration.ResolveSizeBytes(counts: new ShaderPipelineStorageCounts(Height: 1, Width: 1) { BrickPoolVoxels = 1024 }),
            expected: (1024UL * sizeof(float))
        );
    }
    [Fact]
    public void ABufferEdgeWithinAGraphIsPlannedWithItsBarrier() {
        var plan = Plan(definition: Graph(
            outputs: ["scene"],
            packages: [View(), Bake()],
            resources: [Bricks(), Image(name: "scene")]
        ));
        var read = AccessOf(
            pass: "view",
            plan: plan,
            version: "bricks"
        );

        Assert.Equal(expected: ["bake", "view"], actual: plan.Steps.Select(selector: static step => step.Name));
        Assert.Equal(expected: ShaderPipelinePriorKind.Pass, actual: read.PriorKind);
        Assert.Equal(expected: 0, actual: read.PriorPass);
        Assert.Equal(expected: GpuAccess.ShaderWrite, actual: read.Barrier.SourceAccess);
        AssertBufferRead(access: read);
    }
    [Fact]
    public void ABufferEdgeBetweenInstancesIsPlannedWithItsBarrierAndKind() {
        var producer = Plan(definition: Graph(
            outputs: ["bricks"],
            packages: Bake(),
            resources: [Bricks()]
        ));
        var consumer = Plan(definition: Graph(
            outputs: ["scene"],
            packages: View(),
            resources: [Bricks(initialization: ShaderPipelineInitialization.External), Image(name: "scene")]
        ));
        var read = AccessOf(
            pass: "view",
            plan: consumer,
            version: "bricks"
        );

        Assert.Equal(expected: ["bricks"], actual: consumer.Inputs);
        Assert.Equal(expected: ShaderPipelinePriorKind.Host, actual: read.PriorKind);
        Assert.Equal(expected: -1, actual: read.PriorPass);
        AssertBufferRead(access: read);

        // The edge the two instances declare takes its kind from both plans, and the set orders the producer first.
        Assert.True(condition: RenderGraphInstanceSet.TryCreate(
            instances: [
                new RenderGraphInstance(
                    Name: "view",
                    Passes: consumer.Steps.Count,
                    Reads: [new RenderGraphRead(Kind: consumer.KindOf(version: "bricks"), Producer: "bricks")],
                    Refresh: RenderGraphRefresh.EveryFrame
                ),
                new RenderGraphInstance(
                    Name: "bricks",
                    Output: producer.KindOf(version: producer.Outputs[0]),
                    Passes: producer.Steps.Count,
                    Reads: [],
                    Refresh: RenderGraphRefresh.EveryFrame
                ),
            ],
            refusal: out var refusal,
            set: out var set
        ), userMessage: refusal?.Message);
        Assert.Equal(expected: [1, 0], actual: set.Order);
        Assert.Equal(expected: ShaderPipelineResourceKind.Buffer, actual: Assert.Single(collection: set.Reads[0]).Kind);
    }
    [Fact]
    public void AnImagePortBoundToABufferIsRefusedByName() {
        var refusal = Assert.Single(collection: Refusals(definition: Graph(
            outputs: ["final"],
            packages: [
                Bake(),
                new RenderGraphPackagePass(
                    Inputs: ["bricks"],
                    Name: "hud",
                    Outputs: ["final"],
                    Package: RenderGraphPackageCatalog.Overlay
                ),
            ],
            resources: [Bricks(), Image(name: "final")]
        )));

        Assert.Equal(expected: "RENDERGRAPH_PACKAGE_INPUT", actual: refusal.Code);
        Assert.Equal(expected: "bricks", actual: refusal.Name);
        Assert.Contains(expectedSubstring: "'hud'", actualString: refusal.Message);
        Assert.Contains(expectedSubstring: "'bricks', carrying Buffer (stride 4, count 1 per BrickPoolVoxels), to input port 0 of package 'overlay', which carries Image.", actualString: refusal.Message);
    }
    [Fact]
    public void ABufferPortBoundToAnImageIsRefusedByName() {
        var refusal = Assert.Single(collection: Refusals(definition: Graph(
            outputs: ["scene"],
            packages: Bake(output: "scene"),
            resources: [Image(name: "scene")]
        )));

        Assert.Equal(expected: "RENDERGRAPH_PACKAGE_OUTPUT", actual: refusal.Code);
        Assert.Equal(expected: "scene", actual: refusal.Name);
        Assert.Contains(expectedSubstring: "'bake'", actualString: refusal.Message);
        Assert.Contains(expectedSubstring: "'scene', carrying Image, to output port 0 of package 'sdf.bricks', which carries Buffer (stride 4, count 1 per BrickPoolVoxels).", actualString: refusal.Message);
    }
    [Fact]
    public void ABufferPortBoundToABufferOfAnotherStrideIsRefusedByName() {
        var refusal = Assert.Single(collection: Refusals(definition: Graph(
            outputs: ["scene"],
            packages: View(),
            resources: [Bricks(initialization: ShaderPipelineInitialization.External, strideBytes: 8), Image(name: "scene")]
        )));

        Assert.Equal(expected: "RENDERGRAPH_PACKAGE_INPUT", actual: refusal.Code);
        Assert.Contains(expectedSubstring: "carrying Buffer (stride 8, count 1 per BrickPoolVoxels)", actualString: refusal.Message);
    }
    [Fact]
    public void AMalformedPortIsRefusedByTheCatalog() {
        Assert.Throws<ArgumentException>(testCode: static () => new RenderGraphPackageCatalog(packages: [
            new RenderGraphPackage(
                Id: "odd",
                Inputs: [new RenderGraphPackagePort(Access: RenderGraphPortAccess.ComputeRead, Kind: ShaderPipelineResourceKind.Image, StrideBytes: 4)],
                Outputs: [RenderGraphPackagePort.Image(access: RenderGraphPortAccess.ComputeWrite)],
                Summary: "An image port with a stride."
            ),
        ]));
        Assert.Throws<ArgumentException>(testCode: static () => new RenderGraphPackageCatalog(packages: [
            new RenderGraphPackage(
                Id: "odd",
                Inputs: [],
                Outputs: [RenderGraphPackagePort.Buffer(access: RenderGraphPortAccess.ComputeWrite, count: null, strideBytes: 6)],
                Summary: "A buffer port whose stride is not a multiple of four."
            ),
        ]));
        Assert.Throws<ArgumentException>(testCode: static () => new RenderGraphPackageCatalog(packages: [
            new RenderGraphPackage(
                Id: "odd",
                Inputs: [],
                Outputs: [RenderGraphPackagePort.Buffer(access: RenderGraphPortAccess.ColorAttachmentWrite, count: null, strideBytes: 4)],
                Summary: "A buffer port drawn into as a color attachment."
            ),
        ]));
        Assert.Throws<ArgumentException>(testCode: static () => new RenderGraphPackageCatalog(packages: [
            new RenderGraphPackage(
                Id: "odd",
                Inputs: [RenderGraphPackagePort.Image(access: RenderGraphPortAccess.ColorAttachmentWrite)],
                Outputs: [RenderGraphPackagePort.Image(access: RenderGraphPortAccess.FragmentSampled)],
                Summary: "An input port that writes and an output port that reads."
            ),
        ]));
    }
}
