using System.Text;
using Puck.Abstractions.Gpu;
using Puck.Testing;

namespace Puck.Shaders.Tests;

/// <summary>
/// Memory-budget laws for <see cref="ShaderPipelineRenderNode"/>. The node counts a graph's bytes from its plan before
/// anything is allocated; the fake counts, independently, the bytes of every image, buffer, render target and vertex
/// buffer the node actually creates. The two agree on every graph shape: the planned steady state is what an installed
/// graph holds, and the planned peak is the most the device ever holds while a candidate replaces it.
/// </summary>
public sealed partial class ShaderPipelineRenderNodeLawTests {
    private const ulong Mebibyte = (1024UL * 1024UL);
    // The feedback graph's gray scratch, one RGBA8 image a frame slot, which a replacement of the same shape carries.
    private const ulong FeedbackScratchBytes = (((Extent * Extent) * 4UL) * InFlight);

    private static CompiledShaderPipeline Compile(RenderGraphDefinition definition) {
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
                        Name: "gray"
                    )]
                ),
                (Pass(
                    inputs: [new ResourceReference(
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
    // and whether it publishes the float history, which it publishes itself.
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
    public void CarriedStorageIsMovedNotAllocatedSoTheReplacementPeakDropsByExactlyItsBytes() {
        const string HistoryKind = "R16G16B16A16Float image";
        var gpu = new FakePipelineGpu();
        using var node = InstalledNode(gpu: gpu);
        var history = gpu.CreatedObjects.Where(predicate: static created => ((created.Kind == HistoryKind) && (created.DisposeCount == 0))).ToArray();
        var installed = gpu.CreatedObjects.Where(predicate: static created => (created.DisposeCount == 0)).ToArray();
        var candidate = Feedback();
        var account = node.Account(pipeline: candidate);
        var owned = node.OwnedBytes;

        // History only a compute pass touches, which the graph does not publish, holds the instance a frame reads and the
        // one it writes, at its texel size.
        Assert.Equal(
            actual: (Instances: history.Length, Bytes: history.Sum(selector: static created => ((long)created.Bytes))),
            expected: (Instances: 2, Bytes: ((long)((Extent * Extent) * 8UL) * 2L))
        );

        // While the queue finishes nothing, the replaced graph and the whole candidate coexist, except what it carries,
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

        // Once the replaced graph retires, what survives of it is what the candidate carried: its history and its own
        // scratch, never the published image. The peak is lower by exactly those bytes.
        var carried = installed.Where(predicate: static created => (created.DisposeCount == 0)).ToArray();
        var carriedBytes = carried.Aggregate(func: static (total, created) => (total + created.Bytes), seed: 0UL);

        Assert.Equal(
            actual: (Owned: node.OwnedBytes, Live: gpu.LiveBytes, Disposed: history.Sum(selector: static created => created.DisposeCount), Peak: account.PeakBytes),
            expected: (Owned: account.SteadyBytes, Live: account.SteadyBytes, Disposed: 0, Peak: ((owned + account.SteadyBytes) - carriedBytes))
        );
        Assert.True(condition: (carriedBytes > history.Aggregate(func: static (total, created) => (total + created.Bytes), seed: 0UL)));
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

        // A replacement peaks with the staging buffer beside the installed graph and the candidate less the scratch it
        // carries.
        var candidate = Feedback(historyFormat: "R32G32B32A32Float");
        var account = node.Account(pipeline: candidate);

        Assert.Equal(
            actual: account.PeakBytes,
            expected: (((installed + StagingBytes) + account.SteadyBytes) - FeedbackScratchBytes)
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

        // The peak is the installed graph beside the candidate less the scratch it carries; its float history is new.
        Assert.Equal(
            actual: account.PeakBytes,
            expected: ((node.OwnedBytes + account.SteadyBytes) - FeedbackScratchBytes)
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
        // One RGBA8 target per frame slot and the one encode block.
        const ulong PreviewBytes = ((((Extent * Extent) * 4UL) * InFlight) + IGpuBindings.ConstantBufferAlignment);
        var gpu = new FakePipelineGpu();
        using var node = InstalledNode(
            gpu: gpu,
            pipeline: Feedback(backdrop: true)
        );
        var creations = gpu.CreationCount;

        node.BudgetCapBytes = ((node.OwnedBytes + PreviewBytes) - 1UL);

        var refusal = Assert.Throws<InvalidOperationException>(testCode: () => node.SelectOutput(name: Backdrop));

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
        node.SelectOutputBuilt(name: Backdrop);
        _ = node.ProduceFrame(context: default);
        Assert.Equal(
            actual: (Owned: node.OwnedBytes, Live: gpu.LiveBytes),
            expected: (Owned: node.BudgetBytes, Live: node.BudgetBytes)
        );
    }

    // A node on a fake device whose allocations a ledger admits and counts into an account of its own, with the feedback
    // graph installed and every frame slot warm: what the account holds for the node is what the device allocated.
    private static (ShaderPipelineRenderNode Node, FakePipelineGpu Gpu, Puck.Hosting.Jobs.MemoryAccount Account) AccountedNode() {
        var account = new Puck.Hosting.Jobs.MemoryAccount(availablePhysicalBytes: static () => null, installedPhysicalBytes: (64L << 30));
        var ledger = new GpuDeviceMemoryWork(backend: "law", owners: account.DeviceOwners(backend: "law"));

        ledger.BeginDevice(device: FakePipelineGpu.MemoryDevice, segments: GpuMemorySegments.FromDirectX(profile: default));

        var gpu = new FakePipelineGpu { Memory = ledger };
        var node = new ShaderPipelineRenderNode(
            pipelines: new GpuPassPipelineCache(),
            deviceContext: gpu,
            height: Extent,
            hostsOnDirectX: false,
            inFlightFrames: InFlight,
            name: "feedback",
            width: Extent
        ) { MemoryAccount = account };

        node.Swap(pipeline: Feedback());
        _ = node.ProduceUntilInstalled();
        Produce(frames: WarmFrames, node: node);

        return (node, gpu, account);
    }
    private static Puck.Hosting.Jobs.MemoryAdmissionReading Device(Puck.Hosting.Jobs.MemoryAccount account) => account.ReadAdmission(pool: Puck.Abstractions.Memory.PhysicalMemory.Device);

    [Fact]
    public void AReplacementTheAccountHasNoRoomForIsRefusedByNameAndInstallsOnceItHas() {
        var (node, gpu, account) = AccountedNode();

        using (node) {
            var installedPlan = node.Plan;
            var candidate = Feedback(historyFormat: "R32G32B32A32Float");
            var more = (node.Account(pipeline: candidate).PeakBytes - node.OwnedBytes);
            var held = Device(account: account).Held;

            // The first graph installed with no limit read, and the account holds what the device allocated for it,
            // with nothing promised.
            Assert.Equal(expected: (((long)gpu.LiveBytes), 0L), actual: (held, Device(account: account).Promised));
            // The pool has one byte less than the candidate adds: its reservation is refused, so the candidate is refused
            // by name before anything is created for it, and nothing stays promised.
            account.SetBudget(bytes: ((held + ((long)more)) - 1L), pool: Puck.Abstractions.Memory.PhysicalMemory.Device);

            var creations = gpu.CreationCount;

            node.Swap(pipeline: candidate);
            _ = Produce(node: node);
            Assert.StartsWith(actualString: node.LastSwapError!.Message, expectedStartString: $"[{ShaderPipelineMemoryAccount.RefusalCode}] ");
            Assert.Equal(expected: (creations, 0L, installedPlan), actual: (gpu.CreationCount, Device(account: account).Promised, node.Plan));
            // With room for it the same candidate installs, and once the graph it replaced is released nothing is
            // promised and nothing stands past the limit.
            account.SetBudget(bytes: (held + ((long)more)), pool: Puck.Abstractions.Memory.PhysicalMemory.Device);
            _ = SwapAndProduce(gpu: gpu, node: node, pipeline: candidate);
            Produce(frames: (2 * WarmFrames), node: node);
            Assert.Null(@object: node.LastSwapError);
            Assert.Equal(expected: (candidate.Plan, 0L, 0L, ((long)gpu.LiveBytes)), actual: (node.Plan, Device(account: account).Promised, Device(account: account).OverLimit, Device(account: account).Held));
        }
    }
    [Fact]
    public void ACandidateThatFailsAfterItWasAdmittedGivesItsPromiseBack() {
        var (node, gpu, account) = AccountedNode();

        using (node) {
            var installedPlan = node.Plan;

            // The candidate is admitted, and a creation fails while it builds or allocates: only the installed graph is
            // kept, and the promise made for the candidate ends with it.
            gpu.FailAtCreation = (gpu.CreationCount + 1);
            _ = SwapAndProduce(gpu: gpu, node: node, pipeline: Feedback(historyFormat: "R32G32B32A32Float", revision: 1));
            Produce(frames: (2 * WarmFrames), node: node);
            Assert.NotNull(@object: node.LastSwapError);
            Assert.Equal(expected: (installedPlan, 0L, ((long)gpu.LiveBytes)), actual: (node.Plan, Device(account: account).Promised, Device(account: account).Held));
        }
    }
    [Fact]
    public void ReleasingAnEarlierGraphKeepsTheRunningBuildsPromise() {
        var (node, gpu, account) = AccountedNode();

        using (node)
        using (var opener = new PipelineGateOpener()) {
            // B replaces A while the queue is held, so A waits in retirement on its readers.
            gpu.QueueHeld = true;

            var replacement = Feedback(historyFormat: "R32G32B32A32Float", revision: 1);

            _ = SwapAndProduce(gpu: gpu, node: node, pipeline: replacement);
            Assert.Equal(expected: replacement.Plan, actual: node.Plan);
            // C is admitted and its build is held in the driver.
            gpu.PipelineGate = opener.Gate;
            node.Swap(pipeline: Feedback(revision: 2));
            _ = Produce(node: node);
            Assert.True(condition: gpu.PipelineGateEntered.Wait(cancellationToken: TestContext.Current.CancellationToken, timeout: TestLiveness.Bound));

            var promised = Device(account: account).Promised;
            var held = Device(account: account).Held;

            Assert.True(condition: (promised > 0L));
            // A's readers finish and A is released while C still builds: what the account holds falls by A, and what it
            // promised C stands.
            gpu.QueueHeld = false;
            Produce(frames: WarmFrames, node: node);
            Assert.True(condition: node.IsBuildingCandidate);
            Assert.True(condition: (Device(account: account).Held < held));
            Assert.Equal(expected: promised, actual: Device(account: account).Promised);
            // C installs, and its promise is spent.
            opener.Gate.Set();
            node.WaitForBuild();
            Produce(frames: (2 * WarmFrames), node: node);
            Assert.Equal(expected: (0L, ((long)gpu.LiveBytes)), actual: (Device(account: account).Promised, Device(account: account).Held));
        }
    }
    [Fact]
    public void ACandidateWhoseAllocationsThePoolCannotHoldIsRefusedByNameAndTheInstalledGraphKeepsRunning() {
        var (node, gpu, account) = AccountedNode();

        using (node) {
            var installedPlan = node.Plan;
            var candidate = Feedback(historyFormat: "R32G32B32A32Float");
            var more = (node.Account(pipeline: candidate).PeakBytes - node.OwnedBytes);
            var held = Device(account: account).Held;

            // The pool has room for exactly what the candidate's objects hold, which is what its reservation counts, and
            // the device allocates a page more for each. The reservation is admitted; the allocation that passes it is
            // refused, where the installed graph's own storage would be admitted past the limit.
            account.SetBudget(bytes: (held + ((long)more)), pool: Puck.Abstractions.Memory.PhysicalMemory.Device);
            gpu.PaddingBytes = 4096UL;
            _ = SwapAndProduce(gpu: gpu, node: node, pipeline: candidate);
            Produce(frames: (2 * WarmFrames), node: node);
            Assert.StartsWith(actualString: node.LastSwapError!.Message, expectedStartString: $"[{ShaderPipelineMemoryAccount.RefusalCode}] The candidate was refused while it allocated");
            Assert.Equal(expected: (installedPlan, 0L, 0L, held), actual: (node.Plan, Device(account: account).Promised, Device(account: account).OverLimitGrants, Device(account: account).Held));
        }
    }
    [Fact]
    public void TheBudgetIsTheAccountsRoomAndACapOnlyLowersIt() {
        // A discrete device no reading names leaves its pool unbounded, so the account's figure is the most a budget
        // reads and only the cap lowers it.
        var gpu = new FakePipelineGpu {
            MemoryProfile = new GpuMemoryProfile(
                CoherentUnifiedMemory: false,
                DeviceLocalBytes: (1024UL * Mebibyte),
                HostVisibleDeviceLocalBytes: 0UL,
                LargestDeviceLocalHeapBytes: (1024UL * Mebibyte),
                UnifiedMemory: false
            ),
        };
        using var node = Node(gpu: gpu);

        Assert.Equal(expected: ulong.MaxValue, actual: node.DeviceBudgetBytes);

        var budgets = new List<ulong> { node.BudgetBytes };

        node.BudgetCapBytes = (1024UL * Mebibyte);
        budgets.Add(item: node.BudgetBytes);
        node.BudgetCapBytes = Mebibyte;
        budgets.Add(item: node.BudgetBytes);
        node.BudgetCapBytes = null;
        budgets.Add(item: node.BudgetBytes);

        Assert.Equal(
            actual: budgets,
            expected: [ulong.MaxValue, (1024UL * Mebibyte), Mebibyte, ulong.MaxValue]
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
        // A reload of the installed graph carries its own float history, both instances, and its scratch, so the peak holds
        // those instances once.
        Assert.Equal(
            actual: (Steady: account.SteadyBytes, Peak: account.PeakBytes),
            expected: (Steady: node.AllocationBytes, Peak: (((node.OwnedBytes + node.AllocationBytes) - (((Extent * Extent) * 16UL) * 2UL)) - FeedbackScratchBytes))
        );
        Assert.StartsWith(
            actualString: inspection.ToString(),
            expectedStartString: $"[pipeline.inspect: feedback; owned={node.OwnedBytes} bytes; steady={account.SteadyBytes} bytes; peak={account.PeakBytes} bytes; budget={node.BudgetBytes} bytes; "
        );
    }
}
