using Puck.Abstractions.Counting;
using Puck.Abstractions.Presentation;
using Puck.Hosting;

namespace Puck.Shaders.Tests;

/// <summary>
/// Allocation and replacement laws for <see cref="ShaderPipelineRenderNode"/>, driven through its real factory seams by
/// <see cref="FakePipelineGpu"/>: no device, no shader compiler. The graph is the feedback canary's shape — a zero-initialized
/// float history accumulated and converted by two compute passes, then copied by a fullscreen pass — so compute passes,
/// a fullscreen pass with its render targets, history, and zero initialization are all allocated.
/// </summary>
public sealed partial class ShaderPipelineRenderNodeLawTests {
    private const uint Extent = 32;
    private const uint InFlight = 3;
    // Enough frames for every frame slot to have allocated its lazily created command pools and descriptor sets.
    private const int WarmFrames = ((int)(InFlight * 3));

    private static ShaderPipelineResource Image(string name, string format, bool history = false, ShaderPipelineDimensions? dimensions = null) => new(
        Name: name,
        Dimensions: (dimensions ?? ShaderPipelineDimensions.Absolute(
            height: Extent,
            width: Extent
        )),
        Format: format,
        History: history,
        Initialization: (history
            ? ShaderPipelineInitialization.Zero
            : ShaderPipelineInitialization.Undefined)
    );
    private static ShaderPipelinePass Pass(string name, ShaderPipelineDocumentPassKind kind, ResourceReference[] inputs, ResourceReference[] outputs) => new(
        EntryPoint: "main",
        Inputs: inputs,
        Kind: kind,
        Name: name,
        Outputs: outputs,
        Source: $"{name}.hlsl"
    );
    // Every stage's stand-in bytecode is the SPIR-V magic word; a nonzero revision appends a second word, so a candidate
    // built with it is different bytecode, whose pass pipelines are new pass-pipeline cache entries rather than the ones
    // an installed graph of revision zero already leases.
    private static CompiledShader Shader(string name, ShaderPipelineDocumentPassKind kind, byte revision = 0) {
        ReadOnlyMemory<byte> bytecode = ((revision == 0)
            ? new byte[] { 0x03, 0x02, 0x23, 0x07 }
            : new byte[] { 0x03, 0x02, 0x23, 0x07, revision, 0x00, 0x00, 0x00 });
        var stages = ((kind == ShaderPipelineDocumentPassKind.Compute)
            ? new Dictionary<ShaderStage, ReadOnlyMemory<byte>> { [ShaderStage.Compute] = bytecode }
            : new Dictionary<ShaderStage, ReadOnlyMemory<byte>> { [ShaderStage.Vertex] = bytecode, [ShaderStage.Fragment] = bytecode });

        return new CompiledShader(
            diagnostics: [],
            dxil: stages,
            name: name,
            sourceHash: name,
            sourcePath: $"{name}.hlsl",
            spirv: stages
        );
    }
    /// <summary>The feedback graph; <paramref name="historyFormat"/> distinguishes a replacement whose history cannot
    /// be carried over, so every resource of the graph it replaces must retire. <paramref name="historyDimensions"/>
    /// replaces the history's fixed 32x32 extent, for a history whose extent follows the frame or differs.
    /// <paramref name="convertConfig"/> gives the convert pass a config, for a law that sets it live.
    /// <paramref name="revision"/> changes every pass's bytecode, for a candidate whose pass pipelines the pass-pipeline
    /// cache must create rather than share with the graph it replaces.</summary>
    private static CompiledShaderPipeline Feedback(string historyFormat = "R16G16B16A16Float", ShaderPipelineDimensions? historyDimensions = null, IReadOnlyDictionary<string, ShaderConfigField>? convertConfig = null, byte revision = 0) {
        var definition = new RenderGraphDefinition(
            name: "feedback",
            outputs: ["image"],
            passes: [
                Pass(
                    inputs: [new ResourceReference(
                        Name: "history",
                        PreviousFrame: true
                    )],
                    kind: ShaderPipelineDocumentPassKind.Compute,
                    name: "accumulate",
                    outputs: [new ResourceReference(
                        Name: "history"
                    )]
                ),
                Pass(
                    inputs: [new ResourceReference(
                        Name: "history"
                    )],
                    kind: ShaderPipelineDocumentPassKind.Compute,
                    name: "convert",
                    outputs: [new ResourceReference(
                        Name: "gray"
                    )]
                ) with {
                    Config = convertConfig,
                },
                Pass(
                    inputs: [new ResourceReference(
                        Name: "gray"
                    )],
                    kind: ShaderPipelineDocumentPassKind.Fullscreen,
                    name: "copy",
                    outputs: ["image"]
                ),
            ],
            resources: [
                Image(
                    dimensions: historyDimensions,
                    format: historyFormat,
                    history: true,
                    name: "history"
                ),
                Image(
                    format: "R8G8B8A8Unorm",
                    name: "gray"
                ),
                Image(
                    format: "R8G8B8A8Unorm",
                    name: "image"
                ),
            ]
        );
        var plan = new ShaderPipelineCompiler().Compile(definition: definition);

        return new CompiledShaderPipeline(
            plan: plan,
            shaders: plan.Passes.ToDictionary(
                elementSelector: pass => Shader(
                    kind: pass.Declaration!.Kind,
                    name: pass.Name,
                    revision: revision
                ),
                keySelector: static pass => pass.Name
            )
        );
    }
    /// <summary>A storage buffer one compute pass writes and the next reads, then an RGBA8 image that pass writes.</summary>
    private static CompiledShaderPipeline BufferHandoff() {
        var definition = new RenderGraphDefinition(
            name: "handoff",
            outputs: ["image"],
            passes: [
                Pass(
                    inputs: [],
                    kind: ShaderPipelineDocumentPassKind.Compute,
                    name: "produce",
                    outputs: [new ResourceReference(
                        Name: "data"
                    )]
                ),
                Pass(
                    inputs: [new ResourceReference(
                        Name: "data"
                    )],
                    kind: ShaderPipelineDocumentPassKind.Compute,
                    name: "consume",
                    outputs: [new ResourceReference(
                        Name: "image"
                    )]
                ),
            ],
            resources: [
                new ShaderPipelineResource(
                    Kind: ShaderPipelineResourceKind.Buffer,
                    Name: "data",
                    SizeBytes: 256UL
                ),
                Image(
                    format: "R8G8B8A8Unorm",
                    name: "image"
                ),
            ]
        );
        var plan = new ShaderPipelineCompiler().Compile(definition: definition);

        return new CompiledShaderPipeline(
            plan: plan,
            shaders: plan.Passes.ToDictionary(
                elementSelector: static pass => Shader(
                    kind: pass.Declaration!.Kind,
                    name: pass.Name
                ),
                keySelector: static pass => pass.Name
            )
        );
    }
    // A node over its own pass-pipeline cache, or over one a law shares between nodes.
    private static ShaderPipelineRenderNode Node(FakePipelineGpu gpu, GpuPassPipelineCache? pipelines = null) => new(
        pipelines: (pipelines ?? new GpuPassPipelineCache()),
        deviceContext: gpu,
        height: Extent,
        hostsOnDirectX: false,
        inFlightFrames: InFlight,
        name: "feedback",
        width: Extent
    );
    private static Surface Produce(ShaderPipelineRenderNode node) => node.ProduceFrame(context: default);
    private static void Produce(ShaderPipelineRenderNode node, int frames) {
        for (var frame = 0; (frame < frames); frame++) {
            _ = node.ProduceFrame(context: default);
        }
    }
    // A node with the feedback graph installed and every frame slot warm; with floatOutput, it publishes the float
    // history through the float preview, selected once the graph has installed.
    private static ShaderPipelineRenderNode InstalledNode(FakePipelineGpu gpu, bool floatOutput = false, CompiledShaderPipeline? pipeline = null, GpuPassPipelineCache? pipelines = null) {
        var node = Node(
            gpu: gpu,
            pipelines: pipelines
        );

        node.Swap(pipeline: (pipeline ?? Feedback()));
        _ = node.ProduceUntilInstalled();
        if (floatOutput) {
            node.SelectOutputBuilt(name: "history");
        }
        Produce(
            frames: (WarmFrames - 1),
            node: node
        );
        Assert.True(condition: node.IsReady);

        return node;
    }
    // Queues a candidate, produces the frame that starts its build (which still presents the installed graph), and
    // returns the next frame, which installs it: two submissions.
    private static Surface SwapAndProduce(FakePipelineGpu gpu, ShaderPipelineRenderNode node, CompiledShaderPipeline pipeline) {
        node.Swap(pipeline: pipeline);
        _ = node.ProduceBuildStart(gpu: gpu);

        return Produce(node: node);
    }
    // How many objects a successful replacement of the warm graph with changed shaders creates, measured on a separate
    // node.
    private static int ReplacementCreationCount() {
        var gpu = new FakePipelineGpu();
        using var node = InstalledNode(gpu: gpu);
        var before = gpu.CreationCount;

        _ = SwapAndProduce(
            gpu: gpu,
            node: node,
            pipeline: Feedback(
                historyFormat: "R32G32B32A32Float",
                revision: 1
            )
        );
        Assert.Null(@object: node.LastSwapError);

        return (gpu.CreationCount - before);
    }

    [Fact]
    public void ACandidateWhoseAllocationFailsPartwayIsRefusedAndDisposesExactlyWhatItCreated() {
        var candidateCreations = ReplacementCreationCount();

        // Three images per slot (history, gray, and the image the fullscreen pass draws into); per compute pass a module,
        // a pipeline, and per slot a sampler and command pool; for the fullscreen pass two modules, a render pass and a
        // graphics pipeline, and per slot a framebuffer, a sampler, the barrier command pool and the draw command pool;
        // per slot a constant buffer for the frame group's block and one for each of the three passes' pass blocks; and the
        // graph's one descriptor pool. The candidate's shaders differ from the installed graph's, so the pass-pipeline
        // cache creates its modules, render pass and pipelines as new entries, each a creation the fault can land on.
        Assert.Equal(
            actual: candidateCreations,
            expected: (((((3 * ((int)InFlight)) + (2 * (2 + (2 * ((int)InFlight))))) + (4 + (4 * ((int)InFlight)))) + (4 * ((int)InFlight))) + 1)
        );

        for (var failAt = 1; (failAt <= candidateCreations); failAt++) {
            var gpu = new FakePipelineGpu();
            using var node = InstalledNode(gpu: gpu);
            var installedPlan = node.Plan;
            var installed = gpu.CreatedObjects.ToArray();
            var downstream = Produce(node: node);
            var submissions = gpu.Submissions;

            gpu.FailAtCreation = (gpu.CreationCount + failAt);

            var afterRefusal = SwapAndProduce(
                gpu: gpu,
                node: node,
                pipeline: Feedback(
                    historyFormat: "R32G32B32A32Float",
                    revision: 1
                )
            );

            // Refused: the injected failure is the reason, and the installed graph is still the one that runs.
            Assert.IsType<InvalidOperationException>(@object: node.LastSwapError);
            Assert.StartsWith(
                actualString: node.LastSwapError!.Message,
                expectedStartString: "Injected allocation failure"
            );
            Assert.Same(
                expected: installedPlan,
                actual: node.Plan
            );
            Assert.True(condition: node.IsReady);

            // Ownership: everything the candidate created before the failure was disposed exactly once, and nothing the
            // installed graph owns was touched.
            var candidate = gpu.CreatedObjects.Skip(count: installed.Length).ToArray();

            Assert.Equal(
                expected: (failAt - 1),
                actual: candidate.Length
            );
            Assert.All(
                action: static created => Assert.Equal(
                    expected: 1,
                    actual: created.DisposeCount
                ),
                collection: candidate
            );
            Assert.All(
                action: static created => Assert.Equal(
                    expected: 0,
                    actual: created.DisposeCount
                ),
                collection: installed
            );

            // The installed graph keeps producing, into the same images a downstream reader already holds.
            Produce(
                frames: WarmFrames,
                node: node
            );
            Assert.Equal(
                expected: ((submissions + 2) + WarmFrames),
                actual: gpu.Submissions
            );
            Assert.Contains(
                collection: installed,
                filter: created => (created.Handle == downstream.ImageHandle)
            );
            Assert.Contains(
                collection: installed,
                filter: created => (created.Handle == afterRefusal.ImageHandle)
            );
            Assert.Equal(
                expected: (installed.Length + candidate.Length),
                actual: gpu.CreationCount
            );
        }
    }
    [Fact]
    public async Task ANodeThatNeverInstalledDrainsNothingAndReleasesWhatItsBuildCreatedOnce() {
        var idle = new FakePipelineGpu();
        var queued = new FakePipelineGpu();
        var never = Node(gpu: idle);
        var swapped = Node(gpu: queued);

        // The queued candidate's build is held inside the driver when the device is lost: the loss waits it out.
        using var opener = new PipelineGateOpener();
        var gate = opener.Gate;

        queued.PipelineGate = gate;
        swapped.Swap(pipeline: Feedback());
        Assert.True(condition: swapped.ProduceFrame(context: default).IsEmpty);
        never.Dispose();
        Assert.True(condition: queued.PipelineGateEntered.Wait(
            cancellationToken: TestContext.Current.CancellationToken,
            timeout: TimeSpan.FromSeconds(value: 30)
        ));

        // Held in the driver, the build cannot return, so the loss cannot either.
        var loss = Task.Run(action: swapped.OnDeviceLost, cancellationToken: TestContext.Current.CancellationToken);

        Assert.NotSame(
            actual: await Task.WhenAny(
                task1: loss,
                task2: Task.Delay(cancellationToken: TestContext.Current.CancellationToken, millisecondsDelay: 50)
            ),
            expected: loss
        );
        gate.Set();
        await loss;
        swapped.Dispose();

        Assert.Equal(
            expected: 0,
            actual: idle.CreationCount
        );
        foreach (var gpu in ((FakePipelineGpu[])[idle, queued])) {
            Assert.Equal(
                expected: 0,
                actual: gpu.WaitIdleCount
            );
            Assert.All(
                action: static created => Assert.Equal(
                    expected: 1,
                    actual: created.DisposeCount
                ),
                collection: gpu.CreatedObjects
            );
        }
        Assert.DoesNotContain(
            collection: queued.CreatedObjects,
            filter: static created => (created.Kind == "fence")
        );
    }
    [Fact]
    public void ASteadyStateFrameAllocatesNoManagedMemory() {
        const int MeasuredFrames = 64;
        var gpu = new FakePipelineGpu();
        using var node = InstalledNode(gpu: gpu);
        var submissions = gpu.Submissions;
        var creations = gpu.CreationCount;
        var produced = 0;
        var allocated = AllocationWindow.Least(window: () => {
            for (var frame = 0; (frame < MeasuredFrames); frame++) {
                _ = node.ProduceFrame(context: default);
                produced++;
            }
        });

        Assert.Equal(
            expected: (submissions + produced),
            actual: gpu.Submissions
        );
        Assert.Equal(
            expected: creations,
            actual: gpu.CreationCount
        );
        Assert.Equal(
            actual: allocated,
            expected: 0L
        );
    }
    [Fact]
    public void ASteadyStateFrameOverABufferHandoffAllocatesNothing() {
        const int MeasuredFrames = 64;
        var gpu = new FakePipelineGpu();
        using var node = Node(gpu: gpu);

        node.Swap(pipeline: BufferHandoff());
        _ = node.ProduceUntilInstalled();
        Produce(
            frames: (WarmFrames - 1),
            node: node
        );
        Assert.True(condition: node.IsReady);

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
    [Fact]
    public void AReplacementRetiresTheOldGraphWhenTheQueueHasFinishedWithItAndNeverDrainsTheDevice() {
        var gpu = new FakePipelineGpu();
        using var node = InstalledNode(gpu: gpu);
        var installed = gpu.CreatedObjects.ToArray();
        var drains = gpu.WaitIdleCount;

        Assert.Equal(
            expected: ((int)InFlight),
            actual: installed.Count(predicate: static created => (created.Kind == "fence"))
        );

        // While the queue has finished nothing, the replaced graph is still what earlier submissions read: nothing of it
        // retires, however many frames the replacement renders.
        gpu.QueueHeld = true;

        var replaced = SwapAndProduce(
            gpu: gpu,
            node: node,
            pipeline: Feedback(historyFormat: "R32G32B32A32Float")
        );

        Produce(
            frames: WarmFrames,
            node: node
        );
        Assert.Null(@object: node.LastSwapError);
        Assert.True(condition: node.IsReady);
        Assert.All(
            action: static created => Assert.Equal(
                expected: 0,
                actual: created.DisposeCount
            ),
            collection: installed
        );

        // Once the queue finishes, the next frame retires every object of the replaced graph exactly once. What stays was
        // never the replaced graph's alone: one fence and one output-finalizing command pool per slot, which belong to the
        // node's frame slots and are still in use by the replacement; and the feedback graph's pass pipelines, which the
        // replacement, whose shaders are the same, leased from the same pass-pipeline cache entries, so the replaced
        // graph's retirement released its leases and disposed none of them.
        gpu.QueueHeld = false;

        var kept = installed.Where(predicate: static created => (created.Kind is "fence")).ToArray();
        var usesBefore = kept.Select(selector: static created => created.UseCount).ToArray();

        Produce(
            frames: ((int)InFlight),
            node: node
        );
        Assert.All(
            action: static created => Assert.InRange(
                actual: created.DisposeCount,
                high: 1,
                low: 0
            ),
            collection: installed
        );
        Assert.Equal(
            expected: ((string[])[.. Enumerable.Repeat(count: ((int)InFlight), element: "fence"), .. Enumerable.Repeat(count: ((int)InFlight), element: "command pool"), .. FeedbackPassPipelineKinds]).Order(comparer: StringComparer.Ordinal),
            actual: installed.Where(predicate: static created => (created.DisposeCount == 0)).Select(selector: static created => created.Kind).Order(comparer: StringComparer.Ordinal)
        );
        Assert.DoesNotContain(
            collection: gpu.CreatedObjects.Skip(count: installed.Length),
            filter: IsPassPipelineObject
        );
        Assert.All(
            action: pair => Assert.True(condition: (pair.Created.UseCount > pair.Before)),
            collection: kept.Zip(
                resultSelector: static (created, before) => (Created: created, Before: before),
                second: usesBefore
            )
        );
        // No drain: the install and the retirement read the queue's own fences.
        Assert.Equal(
            expected: drains,
            actual: gpu.WaitIdleCount
        );

        // The frames since the install come from the replacement's own images.
        Assert.DoesNotContain(
            collection: installed,
            filter: created => (created.Handle == replaced.ImageHandle)
        );
    }
    [Fact]
    public void TheReplacedGraphRetiresAtInstallExceptThePublishedImagesWhichRetireOnceUnread() {
        var gpu = new FakePipelineGpu();
        using var node = InstalledNode(gpu: gpu);
        var installed = gpu.CreatedObjects.ToArray();
        var remaining = new List<nint[]>();

        // The frame slots' fences and command pools are the node's, and the pass pipelines are shared with the replacement,
        // whose shaders are the same: neither is the replaced graph's to retire.
        void Remaining() => remaining.Add(item: [.. installed.Where(predicate: static created => ((created.DisposeCount == 0) && (created.Kind is not ("fence" or "command pool")) && !IsPassPipelineObject(created: created))).Select(selector: static created => created.Handle)]);

        // The fake queue finishes each submission as it is made, so at the install the node's latest submission has
        // completed and the replaced graph retires at once, except the images behind the two surfaces it published last.
        var beforeLast = Produce(node: node);

        node.Swap(pipeline: Feedback(historyFormat: "R32G32B32A32Float"));

        var last = node.ProduceBuildStart(gpu: gpu);

        _ = Produce(node: node);
        Remaining();
        // Each held image retires once the second submission after a newer publication displaced it has completed.
        for (var frame = 0; (frame < 4); frame++) {
            _ = Produce(node: node);
            Remaining();
        }

        Assert.Equal(
            actual: remaining.Select(selector: static handles => handles.Length),
            expected: [2, 2, 2, 1, 0]
        );
        Assert.Equal(
            actual: remaining[0].Order(),
            expected: new[] { beforeLast.ImageHandle, last.ImageHandle }.Order()
        );
        Assert.Equal(
            actual: remaining[3],
            expected: [last.ImageHandle]
        );
        // The replacement still draws with every pass pipeline the replaced graph was installed with.
        Assert.Equal(
            actual: installed.Where(predicate: IsPassPipelineObject).Select(selector: static created => (created.Kind, created.DisposeCount)).Order(),
            expected: FeedbackPassPipelineKinds.Select(selector: static kind => (kind, 0)).Order()
        );
    }
}
