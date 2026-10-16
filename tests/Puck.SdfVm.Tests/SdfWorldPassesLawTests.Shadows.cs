using Puck.Abstractions.Counting;
using Puck.Abstractions.Gpu;
using Puck.Hosting;
using Puck.SignedDistance;
using Puck.Testing;
using Xunit;

namespace Puck.SdfVm.Tests;

public sealed partial class SdfWorldPassesLawTests {
    // A shadow handoff is a pass value: the views kernel fades the outgoing light from its slot's cascade, so neither the
    // fade policy nor a live handoff plans, allocates or rebuilds anything.
    [Fact]
    public void ShadowFadePoliciesAndHandoffsNeverMoveThePlan() {
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
        var bytes = node.AllocationBytes;
        var plan = node.Plan;
        var revision = node.WorkRevision;
        var counts = ((IWorkCounterSource)node);

        Assert.True(condition: counts.TryRead(kind: GpuWork.ImagesCreated, value: out var images));
        foreach (var capacity in new[] { 1, 2, 0 }) {
            Settle(capacity: capacity);
            foreach (var (active, weight) in new[] { (true, 0.25f), (true, 0.75f), (false, 0f) }) {
                // Forced through the cadence gate: with no fade capacity a handoff changes nothing, and the view would stand.
                current = (ShadowFrame(active: active, capacity: capacity, weight: weight) with { EnableCadenceGate = false });
                _ = view.Produce(context: in context);
                Assert.Equal(expected: RenderGraphInstanceStatus.Rendered, actual: view.Runtime.Latest!.Instances[0].Status);
                Assert.Equal(expected: revision, actual: node.WorkRevision);
                Assert.Same(expected: plan, actual: node.Plan);
                Assert.Equal(expected: bytes, actual: node.AllocationBytes);
                Assert.True(condition: counts.TryRead(kind: GpuWork.ImagesCreated, value: out var afterImages));
                Assert.Equal(actual: afterImages, expected: images);
            }
        }
        Assert.Empty(collection: gpu.StateConflicts);

        // The kernel counters grow their named detail rows as each frame slot of a graph records a frame, so every
        // measurement follows frames forced through the cadence gate into every slot.
        void Settle(int capacity) {
            current = (ShadowFrame(active: false, capacity: capacity, weight: 0f) with { EnableCadenceGate = false });
            for (var frame = 0; (frame <= SdfWorldTables.FrameRingSize); frame++) {
                _ = view.Produce(context: in context);
            }
            current = ShadowFrame(active: false, capacity: capacity, weight: 0f);
            _ = view.Produce(context: in context);
        }
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
