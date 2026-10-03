using Puck.Abstractions.Counting;

namespace Puck.Shaders.Tests;

/// <summary>
/// Preview, float-output, resize and buffer-binding laws for <see cref="ShaderPipelineRenderNode"/>, on the same
/// device-free fake and feedback graph as the allocation and replacement laws. A float output publishes itself. An
/// external output publishes through the preview, which belongs to the graph: it is built with the candidate and
/// allocated when the candidate installs, before the installed graph retires, and a frame never creates it. A resize is a
/// candidate replacement of the installed pipeline at the new extent.
/// </summary>
public sealed partial class ShaderPipelineRenderNodeLawTests {
    // The preview: two shader modules, a render pass, a graphics pipeline, its encode block and one descriptor pool, and
    // per slot an image, a framebuffer and a sampler; it records into the frame slot's one command list.
    private const int PreviewCreations = (6 + (3 * ((int)InFlight)));
    // The preview's pass pipeline: its two shader modules, render pass and graphics pipeline, a pass-pipeline cache entry
    // keyed by the deployed display encode's bytecode, which every graph's preview on the node shares.
    private const int PreviewPipelineCreations = 4;
    // The feedback graph's own objects, as the replacement law counts them, with or without the backdrop, whose image is
    // the host's and whose copy pass samples it through the pass's one sampler.
    private const int GraphCreations = (((((3 * ((int)InFlight)) + (2 * (2 + ((int)InFlight)))) + (4 + (2 * ((int)InFlight)))) + (4 * ((int)InFlight))) + 1);

    private static ShaderPipelineDimensions FrameRelative => ShaderPipelineDimensions.Relative();

    private static FakePipelineGpu.Created[] HistoryImages(FakePipelineGpu gpu, int skip = 0) => [.. gpu.CreatedObjects.Skip(count: skip).Where(predicate: static created => (created.Kind == "R16G16B16A16Float image"))];
    private static (uint Width, uint Height) HistoryExtent(ShaderPipelineRenderNode node) {
        var history = node.ResourceStatus.Single(predicate: static resource => (resource.Name == "history"));

        return (history.Width, history.Height);
    }
    // How many objects one rebuild of the warm relative-history graph at a new extent creates, measured on a separate node.
    private static int ResizeCreationCount() {
        var gpu = new FakePipelineGpu();
        using var node = InstalledNode(
            gpu: gpu,
            pipeline: Feedback(historyDimensions: FrameRelative)
        );
        var before = gpu.CreationCount;

        node.Resize(
            height: (Extent / 2),
            width: (Extent / 2)
        );
        _ = node.ProduceBuildStart(gpu: gpu);
        _ = Produce(node: node);
        Assert.Null(@object: node.LastSwapError);

        return (gpu.CreationCount - before);
    }

    [Fact]
    public void APreviewCandidateAllocatesItsPreviewAndIsRefusedAtEveryAllocationWithTheInstalledPreviewIntact() {
        var measured = new FakePipelineGpu();
        int candidateCreations;

        using (var probe = InstalledNode(
            gpu: measured,
            previewOutput: true
        )) {
            var before = measured.CreationCount;

            _ = SwapAndProduce(
                gpu: measured,
                node: probe,
                pipeline: Feedback(
                    backdrop: true,
                    historyFormat: "R32G32B32A32Float",
                    revision: 1
                )
            );
            Assert.Null(@object: probe.LastSwapError);
            candidateCreations = (measured.CreationCount - before);
        }

        // The candidate's changed shaders make its graph's pass pipelines new cache entries, while its preview joins the
        // installed preview's pipeline and creates only its targets, framebuffers, samplers, pools and descriptor pool.
        Assert.Equal(
            actual: candidateCreations,
            expected: (GraphCreations + (PreviewCreations - PreviewPipelineCreations))
        );

        for (var failAt = 1; (failAt <= candidateCreations); failAt++) {
            var gpu = new FakePipelineGpu();
            using var node = InstalledNode(
                gpu: gpu,
                previewOutput: true
            );
            var installedPlan = node.Plan;
            var installed = gpu.CreatedObjects.ToArray();
            var downstream = Produce(node: node);

            gpu.FailAtCreation = (gpu.CreationCount + failAt);

            var afterRefusal = SwapAndProduce(
                gpu: gpu,
                node: node,
                pipeline: Feedback(
                    backdrop: true,
                    historyFormat: "R32G32B32A32Float",
                    revision: 1
                )
            );
            var candidate = gpu.CreatedObjects.Skip(count: installed.Length).ToArray();

            Assert.StartsWith(
                actualString: node.LastSwapError!.Message,
                expectedStartString: "Injected allocation failure"
            );
            Assert.Same(
                expected: installedPlan,
                actual: node.Plan
            );
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

            // The published surface is still the installed preview's target, before and after the refusal. The selection
            // made after the graph installed created the preview, so its targets are the last RGBA8 images created.
            var previewTargets = installed.Where(predicate: static created => (created.Kind == "R8G8B8A8Unorm image")).TakeLast(count: ((int)InFlight)).Select(selector: static created => created.Handle).ToHashSet();

            Assert.Contains(
                collection: previewTargets,
                expected: downstream.ImageHandle
            );
            Assert.Contains(
                collection: previewTargets,
                expected: afterRefusal.ImageHandle
            );
            Produce(
                frames: WarmFrames,
                node: node
            );
            Assert.Equal(
                expected: (installed.Length + candidate.Length),
                actual: gpu.CreationCount
            );
        }
    }
    [Fact]
    public void APreviewReplacementCreatesEverythingBeforeTheInstalledGraphRetiresAndNothingAfterward() {
        var gpu = new FakePipelineGpu();
        using var node = InstalledNode(
            gpu: gpu,
            previewOutput: true
        );

        gpu.Recording = true;
        // The replacement brings a preview of its own, which joins the installed preview's pipeline; its changed shaders
        // make its graph's pass pipelines new cache entries.
        node.Swap(pipeline: Feedback(
            backdrop: true,
            historyDimensions: ShaderPipelineDimensions.Absolute(
                height: (Extent / 2),
                width: (Extent / 2)
            ),
            revision: 1
        ));
        _ = node.ProduceBuildStart(gpu: gpu);
        Produce(
            frames: (1 + WarmFrames),
            node: node
        );

        Assert.Null(@object: node.LastSwapError);
        Assert.Equal(
            expected: ((Extent / 2), (Extent / 2)),
            actual: HistoryExtent(node: node)
        );

        var lastCreation = gpu.Events.FindLastIndex(match: static entry => entry.StartsWith(
            comparisonType: StringComparison.Ordinal,
            value: "create "
        ));
        var firstRetirement = gpu.Events.FindIndex(match: static entry => entry.StartsWith(
            comparisonType: StringComparison.Ordinal,
            value: "dispose "
        ));

        Assert.Equal(
            expected: (GraphCreations + (PreviewCreations - PreviewPipelineCreations)),
            actual: gpu.Events.Count(predicate: static entry => entry.StartsWith(
                comparisonType: StringComparison.Ordinal,
                value: "create "
            ))
        );
        Assert.InRange(
            actual: lastCreation,
            high: (firstRetirement - 1),
            low: 0
        );
        // The 16x16 half-float history ring, the two 32x32 RGBA8 rings, the backdrop's 32x32 RGBA8 preview ring and its
        // 256-byte encode block, and the ring of the four 256-byte constant buffers (the frame group's block and each
        // pass's); the backdrop is the host's.
        const ulong Half = (Extent / 2);

        Assert.Equal(
            expected: (((((((Half * Half) * 8UL) * InFlight) + ((((2UL * Extent) * Extent) * 4UL) * InFlight)) + (((Extent * Extent) * 4UL) * InFlight)) + 256UL) + ((4UL * 256UL) * InFlight)),
            actual: node.AllocationBytes
        );
    }
    [Fact]
    public void ASteadyStateFloatOutputFrameAllocatesNoManagedMemoryAndCreatesNothing() {
        const int MeasuredFrames = 64;
        var gpu = new FakePipelineGpu();
        using var node = InstalledNode(
            floatOutput: true,
            gpu: gpu
        );
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
            actual: (Creations: gpu.CreationCount, AllocatedBytes: allocated),
            expected: (Creations: creations, AllocatedBytes: 0L)
        );
    }
    [Fact]
    public void ASelectionWhosePreviewCannotBeAllocatedIsRefusedAndThePublishedOutputStays() {
        for (var failAt = 1; (failAt <= PreviewCreations); failAt++) {
            var gpu = new FakePipelineGpu();
            using var node = InstalledNode(
                gpu: gpu,
                pipeline: Feedback(backdrop: true)
            );
            var installed = gpu.CreatedObjects.ToArray();
            var allocation = node.AllocationBytes;

            gpu.FailAtCreation = (gpu.CreationCount + failAt);

            // The preview builds on the thread pool, so its failure is taken at the next frame boundary and reported
            // where a refused swap reports its own.
            node.SelectOutputBuilt(name: Backdrop);
            _ = Produce(node: node);

            var refusal = Assert.IsType<InvalidOperationException>(@object: node.LastSwapError);
            var candidate = gpu.CreatedObjects.Skip(count: installed.Length).ToArray();

            Assert.StartsWith(
                actualString: refusal.InnerException!.Message,
                expectedStartString: "Injected allocation failure"
            );
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
            // The graph keeps publishing its RGBA8 image, and no preview was left behind.
            var published = Produce(node: node);

            Assert.Contains(
                collection: installed,
                filter: created => (created.Handle == published.ImageHandle)
            );
            Assert.Equal(
                expected: allocation,
                actual: node.AllocationBytes
            );
        }
    }
    [Fact]
    public void AResizeRebuildsBesideTheInstalledGraphAndZeroInitializesTheHistoryTheNextFrameReads() {
        var gpu = new FakePipelineGpu { Recording = true };
        using var node = InstalledNode(
            gpu: gpu,
            pipeline: Feedback(historyDimensions: FrameRelative)
        );
        var installed = gpu.CreatedObjects.Count;
        var oldHistory = HistoryImages(gpu: gpu);

        gpu.ClearedImages.Clear();
        node.Resize(
            height: (Extent / 2),
            width: (Extent / 2)
        );

        // Requested, not yet built: the installed graph is still the one at the old extent, and the frame that starts
        // the build still renders it.
        Assert.Equal(
            expected: (Extent, Extent),
            actual: node.Extent
        );
        Assert.Equal(
            expected: ((Extent / 2), (Extent / 2)),
            actual: node.RequestedExtent
        );
        _ = node.ProduceBuildStart(gpu: gpu);
        Assert.Equal(
            expected: (Extent, Extent),
            actual: node.Extent
        );

        var submissions = gpu.Submissions;

        gpu.DescriptorWrites.Clear();

        _ = Produce(node: node);

        var newHistory = HistoryImages(
            gpu: gpu,
            skip: installed
        );

        Assert.Null(@object: node.LastSwapError);
        Assert.Equal(
            expected: ((Extent / 2), (Extent / 2)),
            actual: node.Extent
        );
        Assert.Equal(
            expected: ((Extent / 2), (Extent / 2)),
            actual: HistoryExtent(node: node)
        );
        Assert.Equal(
            expected: (submissions + 1),
            actual: gpu.Submissions
        );
        // No history survives the changed extent. The first read binds the one cleared instance of the new ring,
        // independently of the submission phase. Every other instance is written before it is read.
        Assert.Equal(
            expected: ((int)InFlight),
            actual: newHistory.Length
        );
        var cleared = Assert.Single(collection: gpu.ClearedImages);

        Assert.Contains(collection: newHistory, filter: image => (image.Handle == cleared));
        Assert.Contains(collection: gpu.DescriptorWrites, filter: write => (write.Handle == (cleared + 1)));
        Produce(
            frames: 2,
            node: node
        );
        Assert.All(
            action: static created => Assert.Equal(
                expected: 1,
                actual: created.DisposeCount
            ),
            collection: oldHistory
        );
    }
    // The carried slots are the ones the replaced graph's passes wrote, so whichever slot the first new frame reads as the
    // previous one already holds history, at every phase of the frame ring the resize can land on.
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void AResizeCarriesHistoryWhoseExtentIsUnchanged(int ringPhase) {
        var gpu = new FakePipelineGpu();
        using var node = InstalledNode(gpu: gpu);
        var history = HistoryImages(gpu: gpu);

        Produce(
            frames: ringPhase,
            node: node
        );
        gpu.ClearedImages.Clear();
        node.Resize(
            height: (Extent / 2),
            width: (Extent / 2)
        );
        _ = node.ProduceBuildStart(gpu: gpu);
        Produce(
            frames: WarmFrames,
            node: node
        );

        Assert.Null(@object: node.LastSwapError);
        Assert.Equal(
            expected: ((Extent / 2), (Extent / 2)),
            actual: node.Extent
        );
        Assert.Equal(
            expected: (Extent, Extent),
            actual: HistoryExtent(node: node)
        );
        Assert.All(
            action: static created => Assert.Equal(
                expected: 0,
                actual: created.DisposeCount
            ),
            collection: history
        );
        Assert.Empty(collection: gpu.ClearedImages);
    }
    [Fact]
    public void APausedInstanceInstallsAResizeWithoutAStepAndRendersAtItOnTheNextStep() {
        var gpu = new FakePipelineGpu();
        using var node = InstalledNode(
            gpu: gpu,
            pipeline: Feedback(historyDimensions: FrameRelative)
        );

        node.Paused = true;

        var held = Produce(node: node);
        var submissions = gpu.Submissions;
        var frames = node.FrameCounter;

        // A resize replaces the graph and is not a step: the paused node builds and installs it, renders nothing, and
        // keeps publishing the last image it rendered at the old extent.
        node.Resize(
            height: (Extent / 2),
            width: (Extent / 2)
        );
        _ = node.ProduceBuildStart(gpu: gpu);
        Produce(
            frames: WarmFrames,
            node: node
        );

        Assert.Equal(
            expected: (Submissions: submissions, Frames: frames, Extent: ((Extent / 2), (Extent / 2)), History: ((Extent / 2), (Extent / 2)), Published: held.ImageHandle),
            actual: (Submissions: gpu.Submissions, Frames: node.FrameCounter, Extent: node.Extent, History: HistoryExtent(node: node), Published: Produce(node: node).ImageHandle)
        );

        node.Step();
        _ = Produce(node: node);

        Assert.Equal(
            expected: (Submissions: (submissions + 1), Frames: (frames + 1)),
            actual: (Submissions: gpu.Submissions, Frames: node.FrameCounter)
        );
    }
    [Fact]
    public void ARefusedResizeKeepsTheInstalledGraphAtItsExtentAndIsNotRetriedUntilADifferentExtent() {
        var resizeCreations = ResizeCreationCount();

        for (var failAt = 1; (failAt <= resizeCreations); failAt++) {
            var gpu = new FakePipelineGpu();
            using var node = InstalledNode(
                gpu: gpu,
                pipeline: Feedback(historyDimensions: FrameRelative)
            );
            var installed = gpu.CreatedObjects.ToArray();
            var submissions = gpu.Submissions;

            gpu.FailAtCreation = (gpu.CreationCount + failAt);
            node.Resize(
                height: (Extent / 2),
                width: (Extent / 2)
            );
            _ = node.ProduceBuildStart(gpu: gpu);
            _ = Produce(node: node);

            Assert.StartsWith(
                actualString: node.LastSwapError!.Message,
                expectedStartString: "Injected allocation failure"
            );
            Assert.Equal(
                expected: (Extent, Extent),
                actual: node.Extent
            );
            Assert.All(
                action: static created => Assert.Equal(
                    expected: 0,
                    actual: created.DisposeCount
                ),
                collection: installed
            );

            // The host keeps asking for the refused extent every frame; nothing is rebuilt, and the graph keeps producing.
            var creations = gpu.CreationCount;

            for (var frame = 0; (frame < WarmFrames); frame++) {
                node.Resize(
                    height: (Extent / 2),
                    width: (Extent / 2)
                );
                _ = Produce(node: node);
            }

            Assert.Equal(
                expected: (Creations: creations, Submissions: ((submissions + 2) + WarmFrames)),
                actual: (Creations: gpu.CreationCount, Submissions: gpu.Submissions)
            );

            node.Resize(
                height: (Extent / 4),
                width: (Extent / 4)
            );
            _ = node.ProduceBuildStart(gpu: gpu);
            _ = Produce(node: node);

            Assert.Equal(
                expected: ((Extent / 4), (Extent / 4)),
                actual: node.Extent
            );
        }
    }
    [Fact]
    public void APipelineBufferIsBoundAsARawBuffer() {
        var gpu = new FakePipelineGpu();
        using var node = InstalledNode(
            gpu: gpu,
            pipeline: BufferHandoff()
        );

        Assert.True(condition: (gpu.RawBufferWrites > 0));
        Assert.Equal(
            expected: 0,
            actual: gpu.StructuredBufferWrites
        );
    }
}
