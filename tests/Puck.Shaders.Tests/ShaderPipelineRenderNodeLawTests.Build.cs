namespace Puck.Shaders.Tests;

/// <summary>
/// Where a candidate is built: the frame after a swap or resize starts building its shader modules and pipelines on the
/// thread pool, never on the thread that produces frames, and until the build finishes that thread keeps presenting the
/// installed graph. A replacement is not a step, so a paused node builds and installs it too, rendering nothing until
/// its next step. Counted, not timed: every module and pipeline the fake creates records its creating thread, and the
/// laws count the frames, submissions and creations each thread made.
/// </summary>
public sealed partial class ShaderPipelineRenderNodeLawTests {
    private static bool IsPipelineOrModule(FakePipelineGpu.Created created) =>
        (created.Kind.EndsWith(
            comparisonType: StringComparison.Ordinal,
            value: " pipeline"
        ) || created.Kind.EndsWith(
            comparisonType: StringComparison.Ordinal,
            value: " module"
        ));

    [Fact]
    public void TheFrameThreadCreatesNoPipelineOrShaderModuleOnAnyInstallOrSelection() {
        var gpu = new FakePipelineGpu();
        var frameThread = Environment.CurrentManagedThreadId;
        using var node = Node(gpu: gpu);

        // A first install, selecting the float output on the installed graph, a reload whose float output builds a
        // preview with it, a resize, and the rebuild of the installed pipeline after a device loss.
        node.Swap(pipeline: Feedback(historyDimensions: FrameRelative));
        _ = node.ProduceUntilInstalled();
        node.SelectOutputBuilt(name: "history");

        _ = SwapAndProduce(
            gpu: gpu,
            node: node,
            pipeline: Feedback(
                historyDimensions: FrameRelative,
                historyFormat: "R32G32B32A32Float"
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

        // Per install four modules and three pipelines for the graph (two compute passes and a fullscreen pass, whose one
        // pipeline serves every frame slot); the selection's float preview, and every install after it, adds two modules
        // and one pipeline.
        Assert.Equal(
            actual: compiled.Length,
            expected: (((4 + 3) + (2 + 1)) + (3 * ((4 + 3) + (2 + 1))))
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
        var candidate = Feedback(historyFormat: "R32G32B32A32Float");

        // The driver is still compiling: the frame that starts the build, and every frame after it, renders and
        // publishes the installed graph's own image.
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
            historyFormat: "R32G32B32A32Float"
        );

        // A host compiles a candidate and then resizes the slot showing it before the next frame, the order the World
        // presenter makes them in: one build, at the new extent.
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
}
