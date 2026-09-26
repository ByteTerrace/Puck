using Puck.Abstractions.Counting;
using Puck.Abstractions.Gpu;
using Puck.Hosting;

namespace Puck.Shaders.Tests;

/// <summary>
/// Laws for versioned pipeline resources and the one resource tracker. A forward (<c>from</c>) is a consuming edge: the
/// successor's writer continues its predecessor's storage, so every reader of the predecessor is ordered before it, and
/// the planner refuses, by name, any document whose versions cannot share storage that way. The plan gives every access
/// its prior state and barrier, and <see cref="ShaderPipelineRenderNode"/> records exactly those on the device-free fake.
/// </summary>
public sealed class ShaderPipelineVersionLawTests {
    private const uint Extent = 16;
    private const uint InFlight = 3;
    private const int WarmFrames = ((int)(InFlight * 3));

    private static ShaderPipelineResource Image(string name, string? from = null, string format = "R8G8B8A8Unorm", ShaderPipelineDimensions? dimensions = null, uint samples = 1, bool history = false, ShaderPipelineInitialization initialization = ShaderPipelineInitialization.Undefined) => new(
        Dimensions: (dimensions ?? ShaderPipelineDimensions.Absolute(
            height: Extent,
            width: Extent
        )),
        Format: format,
        From: from,
        History: history,
        Initialization: initialization,
        Name: name,
        Samples: samples
    );
    private static ShaderPipelinePass Compute(string name, ResourceReference[] inputs, ResourceReference[] outputs) => new(
        EntryPoint: "main",
        Inputs: inputs,
        Kind: ShaderPipelineDocumentPassKind.Compute,
        Name: name,
        Outputs: outputs,
        Source: $"{name}.hlsl"
    );
    private static ShaderPipelinePass Fullscreen(string name, ResourceReference[] inputs, string output) => new(
        EntryPoint: "main",
        Inputs: inputs,
        Kind: ShaderPipelineDocumentPassKind.Fullscreen,
        Name: name,
        Outputs: [output],
        Source: $"{name}.hlsl"
    );
    // A chain c0 -> c1 -> c2 in one storage: seed writes c0, peek samples it, first and second each continue the
    // preserved contents, and a fullscreen pass samples c2. The passes are declared in reverse, so the order the plan
    // gives them is the planner's, not the author's.
    private static RenderGraphDefinition Chain(Func<ShaderPipelineResource[], ShaderPipelineResource[]>? resources = null, Func<ShaderPipelinePass[], ShaderPipelinePass[]>? passes = null, string[]? outputs = null) {
        ShaderPipelineResource[] declared = [
            Image(name: "c0"),
            Image(
                from: "c0",
                name: "c1"
            ),
            Image(
                from: "c1",
                name: "c2"
            ),
            Image(name: "peeked"),
            Image(name: "image"),
        ];
        ShaderPipelinePass[] written = [
            Fullscreen(
                inputs: [new ResourceReference(
                    Name: "c2"
                )],
                name: "sample",
                output: "image"
            ),
            Compute(
                inputs: [],
                name: "second",
                outputs: [new ResourceReference(
                    Name: "c2"
                )]
            ),
            Compute(
                inputs: [],
                name: "first",
                outputs: [new ResourceReference(
                    Name: "c1"
                )]
            ),
            Compute(
                inputs: [new ResourceReference(
                    Name: "c0"
                )],
                name: "peek",
                outputs: [new ResourceReference(
                    Name: "peeked"
                )]
            ),
            Compute(
                inputs: [],
                name: "seed",
                outputs: [new ResourceReference(
                    Name: "c0"
                )]
            ),
        ];

        return new RenderGraphDefinition(
            name: "chain",
            outputs: (outputs ?? ["image", "peeked"]),
            passes: (passes?.Invoke(arg: written) ?? written),
            resources: (resources?.Invoke(arg: declared) ?? declared)
        );
    }
    private static ShaderPipelinePlan Plan(RenderGraphDefinition definition) => new ShaderPipelineCompiler().Compile(definition: definition);
    private static IReadOnlyList<ShaderPipelineDiagnostic> Refusal(RenderGraphDefinition definition) =>
        Assert.Throws<ShaderPipelineCompilationException>(testCode: () => Plan(definition: definition)).Diagnostics;
    private static ShaderPipelineResource[] Replace(ShaderPipelineResource[] resources, string name, Func<ShaderPipelineResource, ShaderPipelineResource> change) =>
        [.. resources.Select(selector: resource => ((resource.Name == name)
            ? change(arg: resource)
            : resource))];
    private static CompiledShaderPipeline Compiled(ShaderPipelinePlan plan) {
        ReadOnlyMemory<byte> bytecode = new byte[] { 0x03, 0x02, 0x23, 0x07 };

        return new CompiledShaderPipeline(
            plan: plan,
            shaders: plan.Passes.ToDictionary(
                elementSelector: pass => new CompiledShader(
                    diagnostics: [],
                    dxil: ((pass.Declaration!.Kind == ShaderPipelineDocumentPassKind.Compute)
                        ? new Dictionary<ShaderStage, ReadOnlyMemory<byte>> { [ShaderStage.Compute] = bytecode }
                        : new Dictionary<ShaderStage, ReadOnlyMemory<byte>> { [ShaderStage.Vertex] = bytecode, [ShaderStage.Fragment] = bytecode }),
                    name: pass.Name,
                    sourceHash: pass.Name,
                    sourcePath: $"{pass.Name}.hlsl",
                    spirv: ((pass.Declaration.Kind == ShaderPipelineDocumentPassKind.Compute)
                        ? new Dictionary<ShaderStage, ReadOnlyMemory<byte>> { [ShaderStage.Compute] = bytecode }
                        : new Dictionary<ShaderStage, ReadOnlyMemory<byte>> { [ShaderStage.Vertex] = bytecode, [ShaderStage.Fragment] = bytecode })
                ),
                keySelector: static pass => pass.Name
            )
        );
    }
    // A memory barrier carries no layout to the recorder.
    private static ShaderPipelineBarrier AsRecorded(ShaderPipelineBarrier barrier) =>
        ((barrier.Kind == ShaderPipelineBarrierKind.Memory)
            ? (barrier with { NewLayout = GpuImageLayout.Undefined, OldLayout = GpuImageLayout.Undefined })
            : barrier);
    private static ShaderPipelineRenderNode InstalledNode(FakePipelineGpu gpu, ShaderPipelinePlan plan) {
        var node = new ShaderPipelineRenderNode(
            pipelines: new GpuPassPipelineCache(),
            deviceContext: gpu,
            height: Extent,
            hostsOnDirectX: false,
            inFlightFrames: InFlight,
            name: "chain",
            width: Extent
        );

        node.Swap(pipeline: Compiled(plan: plan));
        _ = node.ProduceUntilInstalled();
        for (var frame = 0; (frame < (WarmFrames - 1)); frame++) {
            _ = node.ProduceFrame(context: default);
        }
        Assert.True(condition: node.IsReady);

        return node;
    }

    [Fact]
    public void AReaderOfAForwardedVersionIsOrderedBeforeTheOverwrite() {
        var plan = Plan(definition: Chain());
        var order = plan.PassOrder.ToList();
        var c0 = plan.FindResource(name: "c0")!;
        var first = plan.Passes.Single(predicate: static pass => (pass.Name == "first"));

        // Without the consuming edge, first would run as soon as seed has, before peek samples what it overwrites.
        Assert.Equal(
            actual: order,
            expected: ["seed", "peek", "first", "second", "sample"]
        );
        Assert.Contains(
            collection: first.Dependencies,
            expected: order.IndexOf(item: "peek")
        );
        Assert.Equal(
            expected: (Writer: order.IndexOf(item: "seed"), ConsumedAt: order.IndexOf(item: "first"), Successor: "c1"),
            actual: (Writer: c0.WriterPassIndex, ConsumedAt: c0.ConsumedAtPassIndex, Successor: c0.Successor)
        );
        Assert.True(condition: (c0.LastUsePassIndex < c0.ConsumedAtPassIndex));
    }
    [Fact]
    public void AReaderThatMustAlsoFollowTheOverwriteIsRefusedAsACycleNamingThePasses() {
        // peek samples c0, which first overwrites, and also samples c1, which first writes: it can run neither before nor
        // after the overwrite.
        var diagnostics = Refusal(definition: Chain(passes: passes => [.. passes.Select(selector: static pass => ((pass.Name == "peek")
            ? (pass with { Inputs = [new ResourceReference(Name: "c0"), new ResourceReference(Name: "c1")] })
            : pass))]));
        var cycle = Assert.Single(collection: diagnostics, predicate: static diagnostic => (diagnostic.Code == "SHADERPIPE_CYCLE"));

        Assert.Contains(
            expectedSubstring: "first",
            actualString: cycle.Message
        );
        Assert.Contains(
            expectedSubstring: "peek",
            actualString: cycle.Message
        );
    }

    public static TheoryData<string, string> Refusals => new() {
        { "extent", "SHADERPIPE_FORWARD_EXTENT" },
        { "format", "SHADERPIPE_FORWARD_FORMAT" },
        { "samples", "SHADERPIPE_FORWARD_SAMPLES" },
        { "branch", "SHADERPIPE_FORWARD_BRANCH" },
        { "public", "SHADERPIPE_FORWARD_PUBLIC" },
        { "history", "SHADERPIPE_FORWARD_HISTORY" },
        { "previous-frame-read-of-a-consumed-version", "SHADERPIPE_DISCARDED_READ" },
        { "sampled-while-overwritten", "SHADERPIPE_DISCARDED_READ" },
        { "kind", "SHADERPIPE_FORWARD_KIND" },
        { "initialized-successor", "SHADERPIPE_FORWARD_INITIALIZATION" },
        { "unwritten", "SHADERPIPE_FORWARD_UNWRITTEN" },
        { "loop", "SHADERPIPE_FORWARD_CYCLE" },
    };

    [MemberData(memberName: nameof(Refusals))]
    [Theory]
    public void EachForwardThatCannotShareStorageIsRefusedByName(string refusal, string code) {
        var definition = refusal switch {
            "extent" => Chain(resources: resources => Replace(
                change: static resource => resource with { Dimensions = ShaderPipelineDimensions.Absolute(height: (Extent * 2), width: Extent) },
                name: "c1",
                resources: resources
            )),
            "format" => Chain(resources: resources => Replace(
                change: static resource => resource with { Format = "B8G8R8A8Unorm" },
                name: "c1",
                resources: resources
            )),
            "samples" => Chain(resources: resources => Replace(
                change: static resource => resource with { Samples = 4 },
                name: "c1",
                resources: resources
            )),
            "branch" => Chain(
                passes: static passes => [.. passes, Compute(inputs: [], name: "fork", outputs: [new ResourceReference(Name: "fork")])],
                resources: static resources => [.. resources, Image(from: "c0", name: "fork")],
                outputs: ["image", "peeked", "fork"]
            ),
            "public" => Chain(outputs: ["image", "c0"]),
            "history" => Chain(resources: resources => Replace(
                change: static resource => resource with { History = true, Initialization = ShaderPipelineInitialization.Zero },
                name: "c0",
                resources: resources
            )),
            "previous-frame-read-of-a-consumed-version" => Chain(passes: static passes => [.. passes.Select(selector: static pass => ((pass.Name == "peek")
                ? (pass with { Inputs = [new ResourceReference(Name: "c0"), new ResourceReference(Name: "c1", PreviousFrame: true)] })
                : pass))]),
            "sampled-while-overwritten" => Chain(passes: static passes => [.. passes.Select(selector: static pass => ((pass.Name == "second")
                ? (pass with { Inputs = [new ResourceReference(Name: "c1")] })
                : pass))]),
            "kind" => Chain(resources: resources => Replace(
                change: static resource => new ShaderPipelineResource(From: "c0", Kind: ShaderPipelineResourceKind.Buffer, Name: resource.Name, SizeBytes: 16),
                name: "c1",
                resources: resources
            )),
            "initialized-successor" => Chain(resources: resources => Replace(
                change: static resource => resource with { Initialization = ShaderPipelineInitialization.Zero },
                name: "c1",
                resources: resources
            )),
            "unwritten" => Chain(
                passes: static passes => [.. passes, Compute(inputs: [], name: "zed", outputs: [new ResourceReference(Name: "z1")])],
                resources: static resources => [.. resources, Image(initialization: ShaderPipelineInitialization.Zero, name: "z0"), Image(from: "z0", name: "z1")],
                outputs: ["image", "peeked", "z1"]
            ),
            "loop" => Chain(resources: resources => Replace(
                change: static resource => resource with { From = "c2" },
                name: "c0",
                resources: resources
            )),
            _ => throw new ArgumentOutOfRangeException(paramName: nameof(refusal)),
        };

        Assert.Contains(
            collection: Refusal(definition: definition),
            filter: diagnostic => (diagnostic.Code == code)
        );
    }
    [Fact]
    public void TwoUsesOfPreservedContentThenASamplingConsumerShareOneStorageAndCarryTheirBarriers() {
        var plan = Plan(definition: Chain());
        var storage = plan.Storages.Single(predicate: static storage => (storage.Name == "c0"));
        var accesses = plan.Passes.SelectMany(selector: static pass => pass.Accesses.Select(selector: access => (Pass: pass.Name, access.Version, access.Use.Access, access.Barrier.Kind, access.Barrier.OldLayout, access.Barrier.NewLayout))).Where(predicate: access => (access.Version is "c0" or "c1" or "c2")).ToArray();

        Assert.Equal(
            expected: ["c0", "c1", "c2"],
            actual: storage.Versions
        );
        Assert.Equal(
            expected: [ShaderPipelineContents.Discarded, ShaderPipelineContents.Preserved, ShaderPipelineContents.Preserved],
            actual: storage.Versions.Select(selector: version => plan.FindResource(name: version)!.Contents)
        );
        Assert.Equal(
            expected: [true, true, false],
            actual: storage.Versions.Select(selector: version => plan.FindResource(name: version)!.IsConsumed)
        );
        // The seed discards; each forward continues the contents (a read and a write in general layout, ordered after the
        // previous write by a memory barrier); the consumer samples the last version after a transition.
        Assert.Equal(
            actual: accesses,
            expected: [
                ("seed", "c0", GpuAccess.ShaderWrite, ShaderPipelineBarrierKind.Image, GpuImageLayout.ShaderReadOnly, GpuImageLayout.General),
                ("peek", "c0", GpuAccess.ShaderRead, ShaderPipelineBarrierKind.Image, GpuImageLayout.General, GpuImageLayout.ShaderReadOnly),
                ("first", "c1", GpuAccess.ShaderRead | GpuAccess.ShaderWrite, ShaderPipelineBarrierKind.Image, GpuImageLayout.ShaderReadOnly, GpuImageLayout.General),
                ("second", "c2", GpuAccess.ShaderRead | GpuAccess.ShaderWrite, ShaderPipelineBarrierKind.Memory, GpuImageLayout.General, GpuImageLayout.General),
                ("sample", "c2", GpuAccess.ShaderRead, ShaderPipelineBarrierKind.Image, GpuImageLayout.General, GpuImageLayout.ShaderReadOnly),
            ]
        );
    }
    [Fact]
    public void ForwardedStorageIsAllocatedOnceAndRewrittenOnlyAfterItsLastRead() {
        var plan = Plan(definition: Chain());
        var gpu = new FakePipelineGpu();
        using var node = InstalledNode(
            gpu: gpu,
            plan: plan
        );
        var order = plan.PassOrder.ToList();
        var chain = plan.Storages.Single(predicate: static storage => (storage.Name == "c0"));

        // One ring for the three versions of the chain, one for peeked, and one for the image the fullscreen pass draws
        // into.
        Assert.Equal(
            expected: (3 * ((int)InFlight)),
            actual: gpu.CreatedObjects.Count(predicate: static created => (created.Kind == "R8G8B8A8Unorm image"))
        );
        Assert.Equal(
            expected: [0, 0, 0],
            actual: chain.Versions.Select(selector: version => plan.FindResource(name: version)!.Storage)
        );
        // Every writer of a successor runs after every reader of its predecessor.
        foreach (var version in chain.Versions) {
            var planned = plan.FindResource(name: version)!;

            if (!planned.IsConsumed) {
                continue;
            }
            foreach (var pass in plan.Passes) {
                if (pass.Declaration!.InputReferences.Any(predicate: input => ((input.Name == version) && !input.PreviousFrame))) {
                    Assert.True(
                        condition: (pass.Index < planned.ConsumedAtPassIndex),
                        userMessage: $"{pass.Name} samples {version} at {pass.Index}, after it is overwritten at {planned.ConsumedAtPassIndex}"
                    );
                }
            }
        }
        Assert.Equal(
            expected: order.IndexOf(item: "first"),
            actual: plan.FindResource(name: "c0")!.ConsumedAtPassIndex
        );
    }
    [Fact]
    public void TheNodeRecordsExactlyThePlannedBarriers() {
        var plan = Plan(definition: Chain());
        var gpu = new FakePipelineGpu();
        using var node = InstalledNode(
            gpu: gpu,
            plan: plan
        );
        var imageStorage = plan.FindResource(name: "image")!.Storage;
        var published = new ShaderPipelineAccessState(
            Access: GpuAccess.ShaderRead,
            Layout: GpuImageLayout.General,
            Stage: GpuStage.ComputeShader | GpuStage.FragmentShader
        );
        var chainStorage = plan.FindResource(name: "c0")!.Storage;
        var expected = new List<ShaderPipelineBarrier>();
        var onChain = new List<int>();

        foreach (var pass in plan.Passes) {
            foreach (var access in pass.Accesses) {
                // The published output starts its next frame from the presentation, which the plan cannot place.
                var barrier = (((access.Storage == imageStorage) && (access.PriorKind == ShaderPipelinePriorKind.CrossFrame))
                    ? ShaderPipelineBarrier.Always(kind: ShaderPipelineResourceKind.Image, prior: published, use: access.Use)
                    : access.Barrier);

                if (barrier.Kind != ShaderPipelineBarrierKind.None) {
                    if (
                        (access.Storage == chainStorage) &&
                        (barrier.Kind == ShaderPipelineBarrierKind.Image)
                    ) {
                        onChain.Add(item: expected.Count);
                    }
                    expected.Add(item: AsRecorded(barrier: barrier));
                }
            }
        }
        expected.Add(item: AsRecorded(barrier: ShaderPipelineBarrier.Between(
            kind: ShaderPipelineResourceKind.Image,
            prior: plan.Storages[imageStorage].FrameEnd,
            use: published
        )));

        gpu.Recording = true;
        _ = node.ProduceFrame(context: default);

        Assert.Equal(
            expected: expected,
            actual: gpu.Barriers.Select(selector: static recorded => recorded.Barrier)
        );
        // The three versions of the chain are one image: every transition on them names the same handle.
        Assert.Equal(
            expected: 4,
            actual: onChain.Count
        );
        Assert.Single(collection: onChain.Select(selector: index => gpu.Barriers[index].Handle).Distinct());
    }
    [Fact]
    public void AVersionItsSuccessorOverwritesCannotBeSelectedForPublication() {
        var gpu = new FakePipelineGpu();
        using var node = InstalledNode(
            gpu: gpu,
            plan: Plan(definition: Chain())
        );

        Assert.Throws<ArgumentException>(testCode: () => node.SelectOutput(name: "c0"));
        node.SelectOutput(name: "c2");
    }
    [Fact]
    public void ASteadyStateFrameOverAForwardingChainAllocatesNothing() {
        const int MeasuredFrames = 64;
        var gpu = new FakePipelineGpu();
        using var node = InstalledNode(
            gpu: gpu,
            plan: Plan(definition: Chain())
        );
        var submissions = gpu.Submissions;
        var produced = 0;
        var allocated = AllocationWindow.Least(window: () => {
            for (var frame = 0; (frame < MeasuredFrames); frame++) {
                _ = node.ProduceFrame(context: default);
                produced++;
            }
        });

        Assert.Equal(
            actual: (Submissions: gpu.Submissions, AllocatedBytes: allocated),
            expected: (Submissions: (submissions + produced), AllocatedBytes: 0L)
        );
    }
}
