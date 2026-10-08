using Puck.Abstractions.Counting;
using Puck.Abstractions.Gpu;
using Puck.Hosting;
using Puck.Shaders;
using Puck.SignedDistance;
using Puck.Testing;
using Xunit;

namespace Puck.SdfVm.Tests;

public sealed partial class SdfWorldPassesLawTests {
    [Fact]
    public void ShadowPolicyAllocatesItsChannelsOnceAndHandoffsReuseThem() {
        var gpu = new UploadModelGpu();
        var pipelines = SdfTestPipelines.Cache(regionCopy: UploadModelGpu.RegionCopyBytecode);
        var current = ShadowFrame(active: false, capacity: 0, weight: 0f);
        using var view = new SdfTestView(device: gpu, extent: Extent, hostsOnDirectX: false, pipelines: pipelines,
            residency: new SdfWorldResidency(brickPoolVoxelCapacity: 0,
                frameSource: new CapturingFrameSource(capture: () => current), height: Extent,
                kernels: SdfTestPipelines.Kernels(), name: SdfTestView.Instance, pipelines: pipelines, width: Extent));
        var context = new FrameContext(AccumulatorTicks: 0, DeltaTicks: 0, ElapsedTicks: 0, FrameDeltaTicks: 0,
            Host: new HostContext(capabilities: new Dictionary<Type, object> { [typeof(IGpuDeviceContext)] = gpu }),
            StepTicks: 0, TargetHeight: Extent, TargetWidth: Extent);

        TestLiveness.Until(step: () => view.Produce(context: in context), reason: () => view.NotReadyReason,
            wait: view.Residency.WaitPipelineBuilds);
        var node = view.Runtime.Node(instance: 0);

        Settle(capacity: 0);
        var zeroBytes = node.AllocationBytes;

        Assert.DoesNotContain(collection: node.Plan!.Storages, filter: IsIncoming);

        var previousCapacity = 0;

        foreach (var capacity in new[] { 1, 2, 0 }) {
            var previousRevision = node.WorkRevision;
            var previousPlan = node.Plan;

            current = ShadowFrame(active: false, capacity: capacity, weight: 0f);
            _ = view.Produce(context: in context);
            _ = view.Residency.WaitPipelineBuilds(cancellationToken: TestContext.Current.CancellationToken);
            // Only whether the policy allows fades moves the plan: every nonzero capacity plans one two-channel image.
            if ((capacity > 0) != (previousCapacity > 0)) {
                TestLiveness.Within(frames: 12, building: () => node.IsBuildingCandidate, step: () => {
                    _ = view.Produce(context: in context);
                    return ((node.WorkRevision > previousRevision) && !node.IsBuildingCandidate && view.Passes.HasRenderedResolvedView(instance: SdfTestView.Instance));
                }, reason: () => $"F={capacity}: revision {node.WorkRevision}, prior {previousRevision}, building={node.IsBuildingCandidate}, status={view.Runtime.Latest!.Instances[0].Status}, error={node.LastSwapError}, residency={view.NotReadyReason}");
            }
            Settle(capacity: capacity);
            if ((capacity > 0) == (previousCapacity > 0)) {
                Assert.Same(expected: previousPlan, actual: node.Plan);
            }
            previousCapacity = capacity;
            var policyBytes = (zeroBytes + ((capacity > 0) ? ((2UL * Extent) * Extent) : 0UL));

            Assert.Equal(expected: policyBytes, actual: node.AllocationBytes);
            Assert.Equal(expected: policyBytes, actual: node.InstalledAccount.SteadyBytes);
            if (capacity == 0) {
                Assert.DoesNotContain(collection: node.Plan!.Storages, filter: IsIncoming);
                continue;
            }
            var storage = Assert.Single(collection: node.Plan!.Storages, predicate: IsIncoming);

            Assert.True(condition: storage.Declaration.Retained);
            Assert.Equal(expected: GpuPixelFormat.R8G8Unorm, actual: ShaderPipelineRenderNode.ParseFormat(format: storage.Declaration.Format));
            var shadow = node.Plan.Passes.Single(predicate: pass => (pass.Package?.Part == SdfWorldPackage.Parts.Shadow));
            var views = node.Plan.Passes.Single(predicate: pass => (pass.Package?.Part == SdfWorldPackage.Parts.Views));
            var read = Assert.Single(collection: views.Accesses, predicate: access => (access.Storage == storage.Index));

            Assert.Equal(expected: shadow.Index, actual: read.PriorPass);
            Assert.NotEqual(expected: ShaderPipelineBarrierKind.None, actual: read.Barrier.Kind);
            Assert.NotEqual(expected: GpuAccess.None, actual: read.Barrier.SourceAccess & GpuAccess.ShaderWrite);
            Assert.NotEqual(expected: GpuAccess.None, actual: read.Barrier.DestinationAccess & GpuAccess.ShaderRead);
            var counts = ((IWorkCounterSource)node);

            Assert.True(condition: counts.TryRead(kind: GpuWork.ImagesCreated, value: out var images));
            var pools = gpu.PoolsCreated.Count;
            var revision = node.WorkRevision;
            var plan = node.Plan;

            foreach (var (active, weight) in new[] { (true, 0.25f), (true, 0.75f), (false, 0f) }) {
                current = ShadowFrame(active: active, capacity: capacity, weight: weight);
                _ = view.Produce(context: in context);
                Assert.Equal(expected: RenderGraphInstanceStatus.Rendered, actual: view.Runtime.Latest!.Instances[0].Status);
                Assert.Equal(expected: revision, actual: node.WorkRevision);
                Assert.Same(expected: plan, actual: node.Plan);
                Assert.Equal(expected: policyBytes, actual: node.AllocationBytes);
                Assert.Equal(expected: pools, actual: gpu.PoolsCreated.Count);
                Assert.True(condition: counts.TryRead(kind: GpuWork.ImagesCreated, value: out var afterImages));
                Assert.Equal(actual: afterImages, expected: images);
            }
            _ = view.Produce(context: in context);
            Assert.Equal(expected: RenderGraphInstanceStatus.Waiting, actual: view.Runtime.Latest!.Instances[0].Status);
        }
        Assert.Empty(collection: gpu.StateConflicts);

        // The kernel counters grow their named detail rows as each frame slot of a graph records a frame, so every
        // measurement follows frames forced through the cadence gate into every slot: what remains between policies is
        // the incoming channels alone, and a handoff then allocates nothing.
        void Settle(int capacity) {
            current = (ShadowFrame(active: false, capacity: capacity, weight: 0f) with { EnableCadenceGate = false });
            for (var frame = 0; (frame <= SdfWorldTables.FrameRingSize); frame++) {
                _ = view.Produce(context: in context);
            }
            current = ShadowFrame(active: false, capacity: capacity, weight: 0f);
            _ = view.Produce(context: in context);
        }
        static bool IsIncoming(ShaderPipelinePlannedStorage storage) => storage.Versions.Any(predicate: static name => name.EndsWith(comparisonType: StringComparison.Ordinal, value: $"${SdfWorldPackage.IncomingVisibility}"));
        static SdfFrame ShadowFrame(int capacity, bool active, float weight) {
            var lights = SdfLights.Default();

            for (var index = 1; (index < 4); index++) {
                lights.Set(index: index, light: lights[0]);
            }
            lights.Count = 4;
            lights.ShadowSlots.Configure(fadeCapacity: capacity, slots: 2);
            lights.ShadowSlots.SetSlot(light: 0, slot: 0);
            lights.ShadowSlots.SetSlot(light: 1, slot: 1);
            if (active) {
                SdfShadowHandoff[] controls = [new(Incoming: 2, Outgoing: 0, Slot: 0, Weight: weight), new(Incoming: 3, Outgoing: 1, Slot: 1, Weight: weight)];

                lights.ShadowSlots.SetHandoffs(handoffs: controls.AsSpan(length: capacity, start: 0));
            }
            return Frame() with { EnableCadenceGate = true, Lights = lights };
        }
    }
}
