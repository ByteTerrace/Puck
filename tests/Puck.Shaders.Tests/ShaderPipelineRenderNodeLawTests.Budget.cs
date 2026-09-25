using System.Text;
using Puck.Abstractions.Gpu;

namespace Puck.Shaders.Tests;

/// <summary>
/// Memory-budget laws for <see cref="ShaderPipelineRenderNode"/>. The node counts a graph's bytes from its plan before
/// anything is allocated; the fake counts, independently, the bytes of every image, buffer, render target and vertex
/// buffer the node actually creates. The two agree on every graph shape: the planned steady state is what an installed
/// graph holds, and the planned peak is the most the device ever holds while a candidate replaces it.
/// </summary>
public sealed partial class ShaderPipelineRenderNodeLawTests {
    private const ulong Mebibyte = (1024UL * 1024UL);

    private static CompiledShaderPipeline Compile(RenderGraphDefinition definition) {
        var plan = new ShaderPipelineCompiler().Compile(definition: definition);

        return new CompiledShaderPipeline(
            plan: plan,
            shaders: plan.Passes.ToDictionary(
                elementSelector: static pass => Shader(
                    kind: pass.Declaration.Kind,
                    name: pass.Name
                ),
                keySelector: static pass => pass.Name
            )
        );
    }
    /// <summary>A compute fill into a 16x16 image, copied into a frame-sized image by a fullscreen pass that reads the
    /// Position vertex input, which gives the pass a vertex buffer.</summary>
    private static CompiledShaderPipeline PositionCopy() {

        return Compile(definition: new RenderGraphDefinition(
            name: "fill",
            outputs: ["image"],
            passes: [
                Pass(
                    inputs: [],
                    kind: ShaderPipelineDocumentPassKind.Compute,
                    name: "fill",
                    outputs: [new ResourceReference(
                        Binding: 0,
                        Name: "gray"
                    )]
                ),
                (Pass(
                    inputs: [new ResourceReference(
                        Binding: 0,
                        Name: "gray"
                    )],
                    kind: ShaderPipelineDocumentPassKind.Fullscreen,
                    name: "copy",
                    outputs: ["image"]
                ) with { Vertex = ShaderPipelineVertexInput.Position }),
            ],
            resources: [
                Image(
                    dimensions: ShaderPipelineDimensions.Absolute(
                        height: (Extent / 2),
                        width: (Extent / 2)
                    ),
                    format: "R8G8B8A8Unorm",
                    name: "gray"
                ),
                Image(
                    dimensions: FrameRelative,
                    format: "R8G8B8A8Unorm",
                    name: "image"
                ),
            ]
        ));
    }
    // What a shape installs first, what replaces it (null for a resize of the installed pipeline to half the extent),
    // and whether it publishes the float history through the float preview.
    private static (CompiledShaderPipeline Installed, CompiledShaderPipeline? Replacement, bool FloatOutput) BudgetShape(string shape) => shape switch {
        "feedback" => (Feedback(), Feedback(historyFormat: "R32G32B32A32Float"), false),
        "feedback-carrying-history" => (Feedback(), Feedback(), false),
        "float-output" => (Feedback(), Feedback(historyFormat: "R32G32B32A32Float"), true),
        "buffer-handoff" => (BufferHandoff(), BufferHandoff(), false),
        "position-vertex" => (PositionCopy(), PositionCopy(), false),
        "resize" => (Feedback(historyDimensions: FrameRelative), null, false),
        _ => throw new ArgumentOutOfRangeException(nameof(shape), shape, "No such graph shape."),
    };

    [InlineData("feedback")]
    [InlineData("feedback-carrying-history")]
    [InlineData("float-output")]
    [InlineData("buffer-handoff")]
    [InlineData("position-vertex")]
    [InlineData("resize")]
    [Theory]
    public void ThePlannedAccountIsExactlyWhatTheDeviceAllocates(string shape) {
        var (installed, replacement, floatOutput) = BudgetShape(shape: shape);
        var gpu = new FakePipelineGpu();
        using var node = Node(gpu: gpu);

        // Nothing is owned before the first graph, so its peak is its steady state, and both are what it allocates.
        var first = node.Account(pipeline: installed);

        node.Swap(pipeline: installed);
        _ = node.ProduceUntilInstalled();
        Assert.Equal(
            actual: (Steady: first.SteadyBytes, Peak: first.PeakBytes, Allocated: node.AllocationBytes),
            expected: (Steady: gpu.LiveBytes, Peak: gpu.PeakLiveBytes, Allocated: gpu.LiveBytes)
        );
        if (floatOutput) {
            node.SelectOutputBuilt(name: "history");
        }
        Produce(
            frames: WarmFrames,
            node: node
        );
        Assert.Equal(
            actual: (Owned: node.OwnedBytes, Steady: node.InstalledAccount.SteadyBytes),
            expected: (Owned: gpu.LiveBytes, Steady: gpu.LiveBytes)
        );

        // While the queue finishes nothing, nothing the replacement replaces can retire, so the device holds the
        // installed graph and the whole candidate together: exactly the planned peak.
        gpu.QueueHeld = true;

        ShaderPipelineMemoryAccount predicted;

        if (replacement is null) {
            node.Resize(
                height: (Extent / 2),
                width: (Extent / 2)
            );
            predicted = node.Account(pipeline: installed);
        } else {
            predicted = node.Account(pipeline: replacement);
            node.Swap(pipeline: replacement);
        }
        gpu.ResetPeakBytes();
        _ = node.ProduceBuildStart(gpu: gpu);
        _ = Produce(node: node);
        Assert.Null(@object: node.LastSwapError);
        Assert.False(condition: (node.HasPendingCandidate || (node.RequestedExtent != node.Extent)));
        Assert.Equal(
            actual: (Peak: gpu.PeakLiveBytes, Steady: node.AllocationBytes, Owned: node.OwnedBytes),
            expected: (Peak: predicted.PeakBytes, Steady: predicted.SteadyBytes, Owned: gpu.LiveBytes)
        );

        // As the queue finishes, the replaced graph and then the held images retire, and what the node says it owns is
        // what the device holds on every frame, down to the candidate's steady state.
        gpu.QueueHeld = false;

        for (var frame = 0; (frame < WarmFrames); frame++) {
            _ = Produce(node: node);
            Assert.Equal(
                actual: node.OwnedBytes,
                expected: gpu.LiveBytes
            );
        }
        Assert.Equal(
            actual: (Owned: node.OwnedBytes, Steady: node.InstalledAccount.SteadyBytes),
            expected: (Owned: predicted.SteadyBytes, Steady: predicted.SteadyBytes)
        );
    }
    [Fact]
    public void CarriedHistoryIsMovedNotAllocatedSoTheReplacementPeakDropsByExactlyItsBytes() {
        const string HistoryKind = "R16G16B16A16Float image";
        var gpu = new FakePipelineGpu();
        using var node = InstalledNode(gpu: gpu);
        var history = gpu.CreatedObjects.Where(predicate: static created => ((created.Kind == HistoryKind) && (created.DisposeCount == 0))).ToArray();
        // The fake's own count of the history instances the replacement can carry: every slot's, at its texel size.
        var carriedBytes = history.Aggregate(
            func: static (total, created) => (total + created.Bytes),
            seed: 0UL
        );
        var candidate = Feedback();
        var account = node.Account(pipeline: candidate);

        Assert.Equal(
            actual: (Instances: history.Length, Bytes: carriedBytes),
            expected: (Instances: ((int)InFlight), Bytes: (((Extent * Extent) * 8UL) * InFlight))
        );
        Assert.Equal(
            actual: account.PeakBytes,
            expected: ((node.OwnedBytes + account.SteadyBytes) - carriedBytes)
        );

        // While the queue finishes nothing, the replaced graph and the whole candidate coexist, except the history,
        // which exists once: the device's peak is exactly the planned one.
        gpu.QueueHeld = true;
        gpu.ResetPeakBytes();

        var creations = gpu.CreationCount;

        _ = SwapAndProduce(
            gpu: gpu,
            node: node,
            pipeline: candidate
        );
        Assert.Null(@object: node.LastSwapError);
        Assert.Same(
            actual: node.Plan,
            expected: candidate.Plan
        );
        Assert.Equal(
            actual: gpu.PeakLiveBytes,
            expected: account.PeakBytes
        );
        // The candidate created no history instance of its own; the replaced graph's instances live on in it.
        Assert.Equal(
            actual: (Created: gpu.CreatedObjects.Skip(count: creations).Count(predicate: static created => (created.Kind == HistoryKind)), Disposed: history.Sum(selector: static created => created.DisposeCount)),
            expected: (Created: 0, Disposed: 0)
        );
        gpu.QueueHeld = false;
        Produce(
            frames: WarmFrames,
            node: node
        );
        Assert.Equal(
            actual: (Owned: node.OwnedBytes, Live: gpu.LiveBytes, Disposed: history.Sum(selector: static created => created.DisposeCount)),
            expected: (Owned: account.SteadyBytes, Live: account.SteadyBytes, Disposed: 0)
        );
    }
    [Fact]
    public void TheCaptureReadbacksStagingBufferIsCountedInWhatTheNodeOwnsAndInEveryLaterPeak() {
        const ulong StagingBytes = ((Extent * Extent) * 4UL);
        var gpu = new FakePipelineGpu { ReadbackSupported = true };
        using var node = InstalledNode(gpu: gpu);
        var installed = node.AllocationBytes;

        // Before any capture the node owns only its graph, and no readback exists.
        Assert.Equal(
            actual: (Owned: node.OwnedBytes, Live: gpu.LiveBytes),
            expected: (Owned: installed, Live: installed)
        );
        Assert.Null(@object: Capture(node: node).Error);

        // The first capture creates the readback's staging buffer, sized to the published RGBA8 surface; the node owns it
        // beside its graph, and what it says it owns is what the device holds.
        Assert.Equal(
            actual: (Staging: gpu.CreatedObjects.Single(predicate: static created => (created.Kind == "readback staging")).Bytes, Owned: node.OwnedBytes, Live: gpu.LiveBytes),
            expected: (Staging: StagingBytes, Owned: (installed + StagingBytes), Live: (installed + StagingBytes))
        );

        // A replacement peaks with the staging buffer beside the installed graph and the whole candidate.
        var candidate = Feedback(historyFormat: "R32G32B32A32Float");
        var account = node.Account(pipeline: candidate);

        Assert.Equal(
            actual: account.PeakBytes,
            expected: ((installed + StagingBytes) + account.SteadyBytes)
        );
        gpu.QueueHeld = true;
        gpu.ResetPeakBytes();
        _ = SwapAndProduce(
            gpu: gpu,
            node: node,
            pipeline: candidate
        );
        Assert.Equal(
            actual: (Error: node.LastSwapError, Peak: gpu.PeakLiveBytes),
            expected: (Error: ((Exception?)null), Peak: account.PeakBytes)
        );
        gpu.QueueHeld = false;
    }
    [Fact]
    public void ACandidateOneByteOverTheBudgetIsRefusedBeforeItAllocatesAndTheSameCandidateInstallsAtExactlyItsPeak() {
        var gpu = new FakePipelineGpu();
        using var node = InstalledNode(gpu: gpu);
        var candidate = Feedback(historyFormat: "R32G32B32A32Float");
        var account = node.Account(pipeline: candidate);
        var installedPlan = node.Plan;
        var creations = gpu.CreationCount;
        var submissions = gpu.Submissions;

        // The peak is the installed graph beside the whole candidate.
        Assert.Equal(
            actual: account.PeakBytes,
            expected: (node.OwnedBytes + account.SteadyBytes)
        );

        node.BudgetCapBytes = (account.PeakBytes - 1UL);
        node.Swap(pipeline: candidate);
        _ = Produce(node: node);

        // Refused by name, with both counts and the budget, before anything was created for it.
        Assert.Equal(
            actual: (Type: node.LastSwapError?.GetType(), node.LastSwapError?.Message),
            expected: (Type: typeof(InvalidDataException), (account with { BudgetBytes = (account.PeakBytes - 1UL) }).Refusal().Message)
        );
        Assert.StartsWith(
            actualString: node.LastSwapError!.Message,
            expectedStartString: $"[{ShaderPipelineMemoryAccount.RefusalCode}] "
        );
        Assert.False(condition: (node.HasPendingCandidate || node.IsBuildingCandidate));

        // The installed graph is never freed to make room: it keeps producing, and nothing is created or disposed.
        Produce(
            frames: WarmFrames,
            node: node
        );
        Assert.Same(
            actual: node.Plan,
            expected: installedPlan
        );
        Assert.Equal(
            actual: (Submissions: gpu.Submissions, Creations: gpu.CreationCount),
            expected: (Submissions: ((submissions + 1) + WarmFrames), Creations: creations)
        );
        Assert.All(
            action: static created => Assert.Equal(
                expected: 0,
                actual: created.DisposeCount
            ),
            collection: gpu.CreatedObjects
        );

        // At a budget of exactly its peak the same candidate installs, and the device holds exactly the budget at the
        // peak, never more.
        node.BudgetCapBytes = account.PeakBytes;
        gpu.QueueHeld = true;
        gpu.ResetPeakBytes();
        _ = SwapAndProduce(
            gpu: gpu,
            node: node,
            pipeline: candidate
        );
        Assert.Null(@object: node.LastSwapError);
        Assert.Same(
            actual: node.Plan,
            expected: candidate.Plan
        );
        Assert.Equal(
            actual: (Peak: gpu.PeakLiveBytes, Budget: node.BudgetBytes),
            expected: (Peak: account.PeakBytes, Budget: account.PeakBytes)
        );
        gpu.QueueHeld = false;
    }
    [Fact]
    public void ASelectionWhosePreviewWouldExceedTheBudgetIsRefusedBeforeItCreatesAnything() {
        const ulong PreviewBytes = (((Extent * Extent) * 4UL) * InFlight);
        var gpu = new FakePipelineGpu();
        using var node = InstalledNode(gpu: gpu);
        var creations = gpu.CreationCount;

        node.BudgetCapBytes = ((node.OwnedBytes + PreviewBytes) - 1UL);

        var refusal = Assert.Throws<InvalidOperationException>(testCode: () => node.SelectOutput(name: "history"));

        Assert.IsType<InvalidDataException>(@object: refusal.InnerException);
        Assert.StartsWith(
            actualString: refusal.InnerException!.Message,
            expectedStartString: $"[{ShaderPipelineMemoryAccount.RefusalCode}] "
        );
        Assert.Equal(
            actual: gpu.CreationCount,
            expected: creations
        );

        // One byte more and the preview is created beside the graph, which then owns exactly the budget.
        node.BudgetCapBytes = (node.OwnedBytes + PreviewBytes);
        node.SelectOutputBuilt(name: "history");
        _ = node.ProduceFrame(context: default);
        Assert.Equal(
            actual: (Owned: node.OwnedBytes, Live: gpu.LiveBytes),
            expected: (Owned: node.BudgetBytes, Live: node.BudgetBytes)
        );
    }
    [Fact]
    public void TheBudgetIsAQuarterOfDeviceLocalMemoryAndACapOnlyLowersIt() {
        static GpuMemoryProfile DeviceLocal(ulong bytes) => new(
            CoherentUnifiedMemory: false,
            DeviceLocalBytes: bytes,
            HostVisibleDeviceLocalBytes: 0UL,
            LargestDeviceLocalHeapBytes: bytes
        );

        Assert.Equal(
            actual: (
                Unreported: ShaderPipelineMemoryBudget.For(profile: default),
                Small: ShaderPipelineMemoryBudget.For(profile: DeviceLocal(bytes: (1024UL * Mebibyte))),
                Large: ShaderPipelineMemoryBudget.For(profile: DeviceLocal(bytes: (12288UL * Mebibyte))),
                Degenerate: ShaderPipelineMemoryBudget.For(profile: DeviceLocal(bytes: 3UL))
            ),
            expected: (
                Unreported: (512UL * Mebibyte),
                Small: (256UL * Mebibyte),
                Large: (3072UL * Mebibyte),
                Degenerate: (512UL * Mebibyte)
            )
        );

        var gpu = new FakePipelineGpu { MemoryProfile = DeviceLocal(bytes: (1024UL * Mebibyte)) };
        using var node = Node(gpu: gpu);
        var budgets = new List<ulong> { node.BudgetBytes };

        node.BudgetCapBytes = (1024UL * Mebibyte);
        budgets.Add(item: node.BudgetBytes);
        node.BudgetCapBytes = Mebibyte;
        budgets.Add(item: node.BudgetBytes);
        node.BudgetCapBytes = null;
        budgets.Add(item: node.BudgetBytes);

        Assert.Equal(
            actual: budgets,
            expected: [(256UL * Mebibyte), (256UL * Mebibyte), Mebibyte, (256UL * Mebibyte)]
        );
        _ = Assert.Throws<ArgumentOutOfRangeException>(testCode: () => node.BudgetCapBytes = 0UL);
    }
    [Fact]
    public void TheInstalledPipelineRebuiltAfterADeviceLossIsNotRefusedByTheBudget() {
        var gpu = new FakePipelineGpu();
        using var node = InstalledNode(gpu: gpu);
        var plan = node.Plan;

        // A cap below what the installed graph already holds refuses every replacement, but the graph a device loss
        // destroyed is restored, not replaced.
        node.BudgetCapBytes = 1UL;
        node.OnDeviceLost();
        _ = node.ProduceUntilInstalled();

        Assert.Equal(
            actual: (Ready: node.IsReady, Error: node.LastSwapError, Plan: node.Plan),
            expected: (Ready: true, Error: ((Exception?)null), Plan: plan)
        );
    }
    [Fact]
    public void TheInspectionPrintsTheInstalledAccountBesideTheOwnedBytes() {
        var gpu = new FakePipelineGpu();
        using var node = InstalledNode(gpu: gpu);

        // Held images from a replacement make the owned bytes larger than the installed graph's.
        _ = SwapAndProduce(
            gpu: gpu,
            node: node,
            pipeline: Feedback(historyFormat: "R32G32B32A32Float")
        );

        var account = node.InstalledAccount;
        var inspection = new StringBuilder();

        Assert.True(condition: node.TryAppendInspection(
            builder: inspection,
            name: "feedback"
        ));
        Assert.True(condition: (node.OwnedBytes > account.SteadyBytes));
        // A reload of the installed graph carries its own float history, so the peak holds those instances once.
        Assert.Equal(
            actual: (Steady: account.SteadyBytes, Peak: account.PeakBytes),
            expected: (Steady: node.AllocationBytes, Peak: ((node.OwnedBytes + node.AllocationBytes) - (((Extent * Extent) * 16UL) * InFlight)))
        );
        Assert.StartsWith(
            actualString: inspection.ToString(),
            expectedStartString: $"[pipeline.inspect: feedback; owned={node.OwnedBytes} bytes; steady={account.SteadyBytes} bytes; peak={account.PeakBytes} bytes; budget={node.BudgetBytes} bytes; "
        );
    }
}
