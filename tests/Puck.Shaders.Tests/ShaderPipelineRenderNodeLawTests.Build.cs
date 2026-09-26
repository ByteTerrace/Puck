using Puck.Abstractions.Counting;
using Puck.Abstractions.Gpu;

namespace Puck.Shaders.Tests;

/// <summary>
/// Where a candidate is built: the frame after a swap or resize starts building its shader modules and pipelines on the
/// thread pool, never on the thread that produces frames, and until the build finishes that thread keeps presenting the
/// installed graph. A replacement is not a step, so a paused node builds and installs it too, rendering nothing until
/// its next step. Counted, not timed: every module and pipeline the fake creates records its creating thread, and the
/// laws count the frames, submissions and creations each thread made.
/// <para>
/// A pass's pipeline is leased from the node's <see cref="GpuPassPipelineCache"/>, keyed by its bytecode, description
/// and render pass: a reinstall of the same shaders, or a second node installing them over the same cache, joins the
/// entry and creates nothing, and an entry is disposed only when the last graph leasing it retires.
/// </para>
/// </summary>
public sealed partial class ShaderPipelineRenderNodeLawTests {
    // The kinds of what the pass-pipeline cache creates for the feedback graph: per compute pass a module and a pipeline,
    // and for the fullscreen pass two modules, the render pass it draws in and a graphics pipeline.
    private static readonly string[] FeedbackPassPipelineKinds = [
        "Compute module",
        "compute pipeline",
        "Compute module",
        "compute pipeline",
        "Vertex module",
        "Fragment module",
        "render pass",
        "graphics pipeline",
    ];

    private static bool IsPipelineOrModule(FakePipelineGpu.Created created) =>
        (created.Kind.EndsWith(
            comparisonType: StringComparison.Ordinal,
            value: " pipeline"
        ) || created.Kind.EndsWith(
            comparisonType: StringComparison.Ordinal,
            value: " module"
        ));
    // A pass pipeline's object: a module, a pipeline, or the render pass a graphics pipeline is created for, every one of
    // which the pass-pipeline cache creates.
    private static bool IsPassPipelineObject(FakePipelineGpu.Created created) =>
        (IsPipelineOrModule(created: created) || (created.Kind == "render pass"));
    private static long Created(GpuPassPipelineCache cache, WorkKind kind) {
        Assert.True(condition: cache.Work.TryRead(
            kind: kind,
            value: out var count
        ));

        return count;
    }

    [Fact]
    public void TheFrameThreadCreatesNoPipelineOrShaderModuleOnAnyInstallOrSelection() {
        var gpu = new FakePipelineGpu();
        var frameThread = Environment.CurrentManagedThreadId;
        using var node = Node(gpu: gpu);

        // A first install, selecting the float output on the installed graph, a reload of changed shaders whose float
        // output builds a preview with it, a resize, and the rebuild of the installed pipeline after a device loss.
        node.Swap(pipeline: Feedback(historyDimensions: FrameRelative));
        _ = node.ProduceUntilInstalled();
        node.SelectOutputBuilt(name: "history");

        _ = SwapAndProduce(
            gpu: gpu,
            node: node,
            pipeline: Feedback(
                historyDimensions: FrameRelative,
                historyFormat: "R32G32B32A32Float",
                revision: 1
            )
        );
        node.Resize(
            height: (Extent / 2),
            width: (Extent / 2)
        );
        _ = node.ProduceBuildStart(gpu: gpu);
        _ = Produce(node: node);
        node.OnDeviceLost();
        Assert.True(condition: node.ProduceBuildStart(gpu: gpu).IsEmpty);
        _ = Produce(node: node);

        Assert.Null(@object: node.LastSwapError);
        Assert.True(condition: node.IsReady);

        var compiled = gpu.CreatedObjects.Where(predicate: IsPipelineOrModule).ToArray();

        // A graph's pipelines are four modules and three pipelines (two compute passes and a fullscreen pass, whose one
        // pipeline serves every frame slot), and the float preview's two modules and one pipeline; each is created once per
        // cache entry. The first install and the selection create both; the reload's changed shaders create the graph's
        // anew while its preview joins the installed one's entry; the resize joins every entry; and the device loss
        // released every lease, so the rebuild creates both again.
        Assert.Equal(
            actual: compiled.Length,
            expected: ((((4 + 3) + (2 + 1)) + (4 + 3)) + ((4 + 3) + (2 + 1)))
        );
        Assert.Equal(
            actual: compiled.Count(predicate: created => (created.ThreadId == frameThread)),
            expected: 0
        );
        // No install drained the device; the device loss released without one.
        Assert.Equal(
            actual: gpu.WaitIdleCount,
            expected: 0
        );
    }
    [Fact]
    public void WhileACandidateBuildsEveryFrameKeepsPresentingTheInstalledGraph() {
        const int HeldFrames = 16;
        var gpu = new FakePipelineGpu();
        using var node = InstalledNode(gpu: gpu);
        using var opener = new PipelineGateOpener();
        var installedPlan = node.Plan;
        var installed = gpu.CreatedObjects.ToArray();
        var submissions = gpu.Submissions;
        var candidate = Feedback(
            historyFormat: "R32G32B32A32Float",
            revision: 1
        );

        // The driver is still compiling the candidate's changed shaders: the frame that starts the build, and every frame
        // after it, renders and publishes the installed graph's own image.
        gpu.PipelineGate = opener.Gate;
        node.Swap(pipeline: candidate);
        for (var frame = 0; (frame < HeldFrames); frame++) {
            var surface = Produce(node: node);

            Assert.Contains(
                collection: installed,
                filter: created => (created.Handle == surface.ImageHandle)
            );
        }

        Assert.True(condition: gpu.PipelineGateEntered.Wait(
            cancellationToken: TestContext.Current.CancellationToken,
            timeout: TimeSpan.FromSeconds(value: 30)
        ));
        Assert.Equal(
            actual: (Submissions: gpu.Submissions, Building: node.IsBuildingCandidate, Pending: node.HasPendingCandidate, Plan: node.Plan),
            expected: (Submissions: (submissions + HeldFrames), Building: true, Pending: true, Plan: installedPlan)
        );

        opener.Gate.Set();
        node.WaitForBuild();
        _ = Produce(node: node);

        Assert.Equal(
            actual: (Pending: node.HasPendingCandidate, Plan: node.Plan),
            expected: (Pending: false, Plan: candidate.Plan)
        );
    }
    [Fact]
    public void AStepTakenWhileItsCandidateBuildsRendersThatCandidateOnceItInstalls() {
        var gpu = new FakePipelineGpu();
        using var node = InstalledNode(gpu: gpu);
        using var opener = new PipelineGateOpener();
        var candidate = Feedback(historyFormat: "R32G32B32A32Float");

        node.Paused = true;
        _ = Produce(node: node);

        var held = Produce(node: node);
        var submissions = gpu.Submissions;

        // The driver is still compiling the candidate when the step is taken: the step waits for it, and the held frame
        // stays published.
        gpu.PipelineGate = opener.Gate;
        node.Swap(pipeline: candidate);
        node.Step();
        for (var frame = 0; (frame < WarmFrames); frame++) {
            Assert.Equal(
                actual: Produce(node: node).ImageHandle,
                expected: held.ImageHandle
            );
        }
        Assert.Equal(
            actual: (Submissions: gpu.Submissions, Stepping: node.StepRequested, Building: node.IsBuildingCandidate),
            expected: (Submissions: submissions, Stepping: true, Building: true)
        );

        opener.Gate.Set();
        node.WaitForBuild();
        Produce(
            frames: WarmFrames,
            node: node
        );

        Assert.Equal(
            actual: (Submissions: gpu.Submissions, Stepping: node.StepRequested, Plan: node.Plan),
            expected: (Submissions: (submissions + 1), Stepping: false, Plan: candidate.Plan)
        );
    }
    [Fact]
    public void AReloadOnAPausedNodeInstallsWithoutAStepAndKeepsTheReplacedImagePublished() {
        var gpu = new FakePipelineGpu();
        using var node = InstalledNode(gpu: gpu);
        var candidate = Feedback(historyFormat: "R32G32B32A32Float");

        node.Paused = true;

        var held = Produce(node: node);
        var replaced = gpu.CreatedObjects.Single(predicate: created => (created.Handle == held.ImageHandle));
        var submissions = gpu.Submissions;
        var frames = node.FrameCounter;
        var creations = gpu.CreationCount;

        // A reload is not a step: the paused frame after the swap starts the build, and the frame after the build
        // installs the candidate, rendering nothing and publishing the replaced graph's last image.
        node.Swap(pipeline: candidate);
        _ = node.ProduceBuildStart(gpu: gpu);

        var installed = Produce(node: node);

        Assert.Equal(
            actual: (Plan: node.Plan, Pending: node.HasPendingCandidate, Stepping: node.StepRequested, Frames: node.FrameCounter, Submissions: gpu.Submissions, Published: installed.ImageHandle),
            expected: (Plan: candidate.Plan, Pending: false, Stepping: false, Frames: frames, Submissions: submissions, Published: held.ImageHandle)
        );

        // Paused frames after the install stay held, and the published image outlives the graph that rendered it.
        for (var frame = 0; (frame < WarmFrames); frame++) {
            Assert.Equal(
                actual: Produce(node: node).ImageHandle,
                expected: held.ImageHandle
            );
        }
        Assert.Equal(
            actual: (Submissions: gpu.Submissions, Frames: node.FrameCounter, ReplacedDisposed: replaced.DisposeCount),
            expected: (Submissions: submissions, Frames: frames, ReplacedDisposed: 0)
        );

        // The next step renders once, through the installed candidate.
        node.Step();

        var stepped = Produce(node: node);

        Assert.Equal(
            actual: (Submissions: gpu.Submissions, Frames: node.FrameCounter, Stepping: node.StepRequested),
            expected: (Submissions: (submissions + 1), Frames: (frames + 1), Stepping: false)
        );
        Assert.Contains(
            collection: gpu.CreatedObjects.Skip(count: creations),
            filter: created => (created.Handle == stepped.ImageHandle)
        );
    }
    [Fact]
    public void ASelectionOnAGraphInstalledWhilePausedIsPublishedWithThatGraphsFirstFrame() {
        var gpu = new FakePipelineGpu();
        using var node = InstalledNode(gpu: gpu);

        node.Paused = true;

        var held = Produce(node: node);

        _ = SwapAndProduce(
            gpu: gpu,
            node: node,
            pipeline: Feedback(historyFormat: "R32G32B32A32Float")
        );

        var submissions = gpu.Submissions;
        var creations = gpu.CreationCount;

        // The installed graph has rendered no frame, so it has none to publish through the new selection: the replaced
        // graph's image stays published and nothing is submitted.
        node.SelectOutputBuilt(name: "history");
        for (var frame = 0; (frame < WarmFrames); frame++) {
            Assert.Equal(
                actual: Produce(node: node).ImageHandle,
                expected: held.ImageHandle
            );
        }
        Assert.Equal(
            actual: gpu.Submissions,
            expected: submissions
        );

        // The step's frame is the graph's first, published through the selection's float preview.
        node.Step();

        var stepped = Produce(node: node);

        Assert.Equal(
            actual: gpu.Submissions,
            expected: (submissions + 1)
        );
        Assert.Contains(
            collection: gpu.CreatedObjects.Skip(count: creations),
            filter: created => (created.Handle == stepped.ImageHandle)
        );
    }
    [Fact]
    public void EveryRequestMadeBetweenTwoFramesIsBuiltOnce() {
        var gpu = new FakePipelineGpu();
        using var node = InstalledNode(
            gpu: gpu,
            pipeline: Feedback(historyDimensions: FrameRelative)
        );
        var creations = gpu.CreationCount;
        var candidate = Feedback(
            historyDimensions: FrameRelative,
            historyFormat: "R32G32B32A32Float",
            revision: 1
        );

        // A host compiles a candidate of changed shaders and then resizes the slot showing it before the next frame, the
        // order the World presenter makes them in: one build, at the new extent, which creates the candidate's pipelines
        // once.
        node.Swap(pipeline: candidate);
        node.Resize(
            height: (Extent / 2),
            width: (Extent / 2)
        );
        _ = node.ProduceBuildStart(gpu: gpu);
        _ = Produce(node: node);

        Assert.Equal(
            actual: (Plan: node.Plan, Extent: node.Extent, Error: node.LastSwapError),
            expected: (Plan: candidate.Plan, Extent: ((Extent / 2), (Extent / 2)), Error: ((Exception?)null))
        );
        Assert.Equal(
            actual: gpu.CreatedObjects.Skip(count: creations).Count(predicate: IsPipelineOrModule),
            expected: (4 + 3)
        );
    }
    [Fact]
    public void AReinstallOfTheSameShadersJoinsEveryPassPipelineAndCreatesNone() {
        var gpu = new FakePipelineGpu();
        var cache = new GpuPassPipelineCache();
        using var node = InstalledNode(
            gpu: gpu,
            pipelines: cache
        );
        var installed = gpu.CreatedObjects.Where(predicate: IsPassPipelineObject).ToArray();
        var replacedHistory = HistoryImages(gpu: gpu);
        var candidate = Feedback(historyFormat: "R32G32B32A32Float");

        // The install created each pass pipeline once, into the cache's own count.
        Assert.Equal(
            actual: (Pipelines: Created(cache: cache, kind: GpuWork.PipelinesCreated), Modules: Created(cache: cache, kind: GpuWork.ShaderModulesCreated), Shared: cache.SharedPipelines),
            expected: (Pipelines: 3L, Modules: 4L, Shared: 3)
        );

        // A reload of the same shaders with a different history format installs with every pass a cache hit. The replaced
        // graph retires, its history with it, and releases its leases while the replacement holds every entry.
        _ = SwapAndProduce(
            gpu: gpu,
            node: node,
            pipeline: candidate
        );
        Produce(
            frames: WarmFrames,
            node: node
        );

        Assert.Equal(
            actual: (Plan: node.Plan, Error: node.LastSwapError, Pipelines: Created(cache: cache, kind: GpuWork.PipelinesCreated), Modules: Created(cache: cache, kind: GpuWork.ShaderModulesCreated), Shared: cache.SharedPipelines),
            expected: (Plan: candidate.Plan, Error: ((Exception?)null), Pipelines: 3L, Modules: 4L, Shared: 3)
        );
        Assert.All(
            action: static created => Assert.Equal(
                actual: created.DisposeCount,
                expected: 1
            ),
            collection: replacedHistory
        );
        Assert.Equal(
            actual: gpu.CreatedObjects.Where(predicate: IsPassPipelineObject).Select(selector: static created => (created.Handle, created.DisposeCount)),
            expected: installed.Select(selector: static created => (created.Handle, 0))
        );
    }
    [Fact]
    public void TwoNodesOverOneCacheInstallingTheSameGraphShareEachPassPipeline() {
        var gpu = new FakePipelineGpu();
        var cache = new GpuPassPipelineCache();
        var pipeline = Feedback();
        var first = InstalledNode(
            gpu: gpu,
            pipeline: pipeline,
            pipelines: cache
        );
        var second = InstalledNode(
            gpu: gpu,
            pipeline: pipeline,
            pipelines: cache
        );
        var shared = gpu.CreatedObjects.Where(predicate: IsPassPipelineObject).ToArray();

        // Each pass pipeline was created once, by the first node's build, and the second node's build joined it: one
        // graphics pipeline draws both nodes' copy pass.
        Assert.Equal(
            actual: shared.Select(selector: static created => created.Kind).Order(comparer: StringComparer.Ordinal),
            expected: FeedbackPassPipelineKinds.Order(comparer: StringComparer.Ordinal)
        );
        Assert.Equal(
            actual: (GraphicsPipelines: gpu.GraphicsPipelines.Count, Pipelines: Created(cache: cache, kind: GpuWork.PipelinesCreated), Modules: Created(cache: cache, kind: GpuWork.ShaderModulesCreated), Shared: cache.SharedPipelines),
            expected: (GraphicsPipelines: 1, Pipelines: 3L, Modules: 4L, Shared: 3)
        );

        // Disposing one node releases its leases and disposes nothing the other still draws with; the other keeps
        // producing, and its disposal is the last release of each entry, which disposes every object once.
        first.Dispose();
        Assert.All(
            action: static created => Assert.Equal(
                actual: created.DisposeCount,
                expected: 0
            ),
            collection: shared
        );
        Produce(
            frames: WarmFrames,
            node: second
        );
        Assert.Equal(
            actual: (Ready: second.IsReady, Shared: cache.SharedPipelines, Creations: gpu.CreatedObjects.Count(predicate: IsPassPipelineObject)),
            expected: (Ready: true, Shared: 3, Creations: shared.Length)
        );

        second.Dispose();
        Assert.All(
            action: static created => Assert.Equal(
                actual: created.DisposeCount,
                expected: 1
            ),
            collection: shared
        );
        Assert.Equal(
            actual: cache.SharedPipelines,
            expected: 0
        );
    }
    [Fact]
    public void AReplacementOfChangedShadersCreatesNewEntriesAndDisposesTheReplacedOnesOnlyOnceTheirGraphRetires() {
        var gpu = new FakePipelineGpu();
        var cache = new GpuPassPipelineCache();
        using var node = InstalledNode(
            gpu: gpu,
            pipelines: cache
        );
        var replaced = gpu.CreatedObjects.Where(predicate: IsPassPipelineObject).ToArray();
        var creations = gpu.CreationCount;

        // While the queue has finished nothing, the replaced graph's submissions may still read its pipelines: the
        // replacement's shaders differ, so its entries are new and sit beside the replaced ones, which keep their leases
        // however many frames the replacement renders.
        gpu.QueueHeld = true;
        _ = SwapAndProduce(
            gpu: gpu,
            node: node,
            pipeline: Feedback(revision: 1)
        );
        Produce(
            frames: WarmFrames,
            node: node
        );

        var replacement = gpu.CreatedObjects.Skip(count: creations).Where(predicate: IsPassPipelineObject).ToArray();

        Assert.Null(@object: node.LastSwapError);
        Assert.Equal(
            actual: replacement.Select(selector: static created => created.Kind).Order(comparer: StringComparer.Ordinal),
            expected: FeedbackPassPipelineKinds.Order(comparer: StringComparer.Ordinal)
        );
        Assert.Equal(
            actual: (Pipelines: Created(cache: cache, kind: GpuWork.PipelinesCreated), Modules: Created(cache: cache, kind: GpuWork.ShaderModulesCreated), Shared: cache.SharedPipelines),
            expected: (Pipelines: 6L, Modules: 8L, Shared: 6)
        );
        Assert.All(
            action: static created => Assert.Equal(
                actual: created.DisposeCount,
                expected: 0
            ),
            collection: replaced.Concat(second: replacement)
        );

        // Once the queue finishes, the replaced graph retires, and its leases were the last on the replaced entries: each
        // of their objects is disposed once, and the replacement's stay.
        gpu.QueueHeld = false;
        Produce(
            frames: ((int)InFlight),
            node: node
        );

        Assert.All(
            action: static created => Assert.Equal(
                actual: created.DisposeCount,
                expected: 1
            ),
            collection: replaced
        );
        Assert.All(
            action: static created => Assert.Equal(
                actual: created.DisposeCount,
                expected: 0
            ),
            collection: replacement
        );
        Assert.Equal(
            actual: cache.SharedPipelines,
            expected: 3
        );
    }
}
