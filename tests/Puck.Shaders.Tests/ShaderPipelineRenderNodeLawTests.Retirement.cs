using Puck.Abstractions.Presentation;

namespace Puck.Shaders.Tests;

/// <summary>
/// Retirement laws for <see cref="ShaderPipelineRenderNode"/>: what a replacement leaves behind, and how disposal
/// releases it. Disposal runs on a pool thread under a liveness bound, so a disposal that waits on something that can
/// never finish fails the law instead of hanging the suite.
/// </summary>
public sealed partial class ShaderPipelineRenderNodeLawTests {
    private static void DisposeWithin(ShaderPipelineRenderNode node) {
        var disposal = Task.Run(action: node.Dispose);

        Assert.True(
            condition: disposal.Wait(
                cancellationToken: TestContext.Current.CancellationToken,
                timeout: TimeSpan.FromSeconds(value: 30)
            ),
            userMessage: "Disposing the node waited on something that never finished."
        );
    }
    // Arms a capture, produces one frame to serve it, and returns its outcome. Nothing is written: the fake's readback is
    // unsupported, so a capture that reaches the published image fails there.
    private static FrameCaptureResult Capture(ShaderPipelineRenderNode node) {
        var request = new FrameCaptureRequest(path: Path.Combine(
            path1: Path.GetTempPath(),
            path2: $"{Guid.NewGuid():N}.png"
        ));

        node.RequestCapture(request: request);
        _ = Produce(node: node);
        Assert.True(condition: request.Completion.IsCompleted);

        return request.Completion.Result;
    }

    [Fact]
    public void ACaptureArmedAfterAFloatSelectionWaitsForTheFrameThatPublishesTheSelection() {
        var gpu = new FakePipelineGpu();
        using var node = InstalledNode(gpu: gpu);

        node.Paused = true;

        var previous = Produce(node: node);
        var request = new FrameCaptureRequest(path: Path.Combine(
            path1: Path.GetTempPath(),
            path2: $"{Guid.NewGuid():N}.png"
        ));

        // The selection's float preview is held in the driver, so the frame after the capture is armed still publishes
        // the previous selection, and the capture must not read it.
        using (var opener = new PipelineGateOpener()) {
            gpu.PipelineGate = opener.Gate;

            try {
                node.SelectOutput(name: "history");
                node.RequestCapture(request: request);

                var held = Produce(node: node);

                Assert.True(condition: node.IsBuildingPreview);
                Assert.False(condition: request.Completion.IsCompleted);
                Assert.Equal(
                    actual: held.ImageHandle,
                    expected: previous.ImageHandle
                );
            } finally {
                gpu.PipelineGate = null;
            }
        }

        Assert.True(
            condition: SpinWait.SpinUntil(
                condition: () => !node.IsBuildingPreview,
                timeout: TimeSpan.FromSeconds(value: 30)
            ),
            userMessage: "The float preview never finished building."
        );

        // The next frame installs the preview and publishes the selection, and that is the frame the capture reads.
        var shown = Produce(node: node);

        Assert.NotEqual(
            actual: shown.ImageHandle,
            expected: previous.ImageHandle
        );
        Assert.True(condition: request.Completion.IsCompleted);
    }
    [Fact]
    public void APausedCaptureAfterADeviceLossReportsTheImageUnavailableUntilAStepRenders() {
        var gpu = new FakePipelineGpu();
        using var node = InstalledNode(gpu: gpu);

        node.Paused = true;

        var held = Produce(node: node);

        // Control: before the loss, a paused capture reads the published image.
        Assert.IsType<NotSupportedException>(@object: Capture(node: node).Error);

        // The loss destroys the published image; the paused node rebuilds its pipeline, renders nothing and publishes
        // nothing, and a capture reports that no completed output exists instead of reading the destroyed image.
        node.OnDeviceLost();
        Assert.True(condition: node.ProduceBuildStart(gpu: gpu).IsEmpty);

        var frames = node.FrameCounter;
        var submissions = gpu.Submissions;

        Assert.True(condition: Produce(node: node).IsEmpty);
        Assert.True(condition: node.IsReady);
        Assert.Equal(
            actual: Capture(node: node).Error?.Message,
            expected: "A completed same-device output is required for capture."
        );
        Assert.Equal(
            actual: (Frames: node.FrameCounter, Submissions: gpu.Submissions, Destroyed: gpu.CreatedObjects.Single(predicate: created => (created.Handle == held.ImageHandle)).DisposeCount),
            expected: (Frames: frames, Submissions: submissions, Destroyed: 1)
        );

        // A step renders the rebuilt graph and publishes its image again.
        node.Step();

        var stepped = Produce(node: node);

        Assert.Equal(
            actual: (Empty: stepped.IsEmpty, Frames: node.FrameCounter, Submissions: gpu.Submissions),
            expected: (Empty: false, Frames: (frames + 1), Submissions: (submissions + 1))
        );
    }
    [InlineData(false)]
    [InlineData(true)]
    [Theory]
    public void ReplacementsWhilePausedOwnOneGraphAndThePublishedImagesHoweverManyThereAre(bool floatOutput) {
        const int Replacements = 8;
        // The two surfaces published last are RGBA8 targets at the frame extent: the fullscreen pass's output image, or
        // the float preview of the history.
        const ulong HeldBytes = ((2UL * (Extent * Extent)) * 4UL);
        var gpu = new FakePipelineGpu();
        using var node = InstalledNode(
            floatOutput: floatOutput,
            gpu: gpu
        );

        node.Paused = true;

        var held = Produce(node: node);
        var owned = new List<(ulong Owned, ulong Allocated)>();
        var live = new List<int>();

        // The paused node's last frame has finished, so every replacement retires the graph it replaces at once; only the
        // images behind its two most recently published surfaces outlive it, and they are the same two every time.
        for (var replacement = 0; (replacement < Replacements); replacement++) {
            _ = SwapAndProduce(
                gpu: gpu,
                node: node,
                pipeline: Feedback(historyFormat: "R32G32B32A32Float")
            );
            Assert.Equal(
                actual: Produce(node: node).ImageHandle,
                expected: held.ImageHandle
            );
            owned.Add(item: (node.OwnedBytes, node.AllocationBytes));
            live.Add(item: gpu.CreatedObjects.Count(predicate: static created => (created.DisposeCount == 0)));
        }

        Assert.All(
            action: static bytes => Assert.Equal(
                actual: bytes.Owned,
                expected: (bytes.Allocated + HeldBytes)
            ),
            collection: owned
        );
        _ = Assert.Single(collection: live.Distinct());
    }
    [Fact]
    public void DisposingAPausedNodeAfterARefusedCandidateAndASupersedingInstallWaitsOnNothing() {
        var gpu = new FakePipelineGpu();
        var node = InstalledNode(gpu: gpu);

        // The pipeline-edit canary's shape: paused after two frames, a candidate refused while it builds, one more
        // step, then a superseding candidate that installs while paused and leaves the replaced graph waiting for the
        // queue, which has not finished the last frame.
        node.Paused = true;
        node.Step();
        _ = Produce(node: node);
        gpu.FailAtCreation = (gpu.CreationCount + 1);
        _ = SwapAndProduce(
            gpu: gpu,
            node: node,
            pipeline: Feedback(historyFormat: "R32G32B32A32Float")
        );
        Assert.NotNull(@object: node.LastSwapError);
        node.Step();
        _ = Produce(node: node);
        gpu.QueueHeld = true;

        var candidate = Feedback(historyFormat: "R32G32B32A32Float");

        _ = SwapAndProduce(
            gpu: gpu,
            node: node,
            pipeline: candidate
        );
        Assert.Equal(
            actual: (Plan: node.Plan, Stepping: node.StepRequested),
            expected: (Plan: candidate.Plan, Stepping: false)
        );

        DisposeWithin(node: node);

        // Every object the node or a candidate created is released exactly once.
        Assert.All(
            action: static created => Assert.Equal(
                expected: 1,
                actual: created.DisposeCount
            ),
            collection: gpu.CreatedObjects
        );
    }
}
