using Puck.Abstractions.Counting;
using Puck.Abstractions.Gpu;
using Puck.Hosting;
using Puck.Shaders;
using Puck.SignedDistance;
using Puck.Testing;
using Xunit;

namespace Puck.SdfVm.Tests;

public sealed partial class SdfWorldPassesLawTests {
    // Each shadow resolution halves the cascades' texels a side, and a restriction keeps the coarser of two.
    [Fact]
    public void AShadowResolutionHalvesTheTexelsAndARestrictionKeepsTheCoarser() {
        Assert.Equal(
            expected: [2048, 1024, 512],
            actual: Enum.GetValues<SdfShadowResolution>().Select(selector: static resolution => SdfShadowResolutions.Texels(resolution: resolution))
        );
        _ = Assert.Throws<ArgumentOutOfRangeException>(testCode: static () => SdfShadowResolutions.Texels(resolution: ((SdfShadowResolution)3)));

        var half = new SdfViewQuality { ShadowResolution = SdfShadowResolution.Half };

        Assert.Equal(expected: SdfShadowResolution.Half, actual: half.Restrict(other: default).ShadowResolution);
        Assert.Equal(expected: SdfShadowResolution.Quarter, actual: half.Restrict(other: new SdfViewQuality { ShadowResolution = SdfShadowResolution.Quarter }).ShadowResolution);
    }
    // A view allocates the cascades its quality and its world's shadow slots draw: every cascade's depth at the view's
    // shadow resolution, and the second slot's cascade only while the frame holds a second stable slot. A change of either
    // rebuilds the view's graph once at the new shape.
    [Fact]
    public void AViewSizesItsCascadesByItsShadowResolutionAndHoldsTheSecondOnlyForASecondSlot() {
        var gpu = new UploadModelGpu();
        var pipelines = SdfTestPipelines.Cache(regionCopy: UploadModelGpu.RegionCopyBytecode);
        var current = Shaped(resolution: SdfShadowResolution.Full, slots: 2);
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

        foreach (var (resolution, slots, texels) in new[] {
            (SdfShadowResolution.Full, 2, 2048u), (SdfShadowResolution.Half, 2, 1024u), (SdfShadowResolution.Quarter, 1, 512u), (SdfShadowResolution.Full, 1, 2048u),
        }) {
            current = Shaped(resolution: resolution, slots: slots);

            var depths = slots + 1;

            TestLiveness.Until(
                step: () => (view.Produce(context: in context) && !node.IsBuildingCandidate &&
                    (Cascades().Length == depths) && Cascades().All(predicate: status => (status.Width == texels))),
                reason: () => node.LastSwapError?.ToString(),
                wait: view.Residency.WaitPipelineBuilds
            );
            Assert.Equal(expected: (slots > 1), actual: node.Plan!.Passes.Any(predicate: static pass => pass.Name.EndsWith(comparisonType: StringComparison.Ordinal, value: $"${SdfWorldPackage.Parts.ShadowSecond}")));
            Assert.All(collection: Cascades(), action: status => Assert.Equal(expected: (((ulong)texels) * texels * sizeof(float)), actual: status.AllocationBytes));
        }
        Assert.Empty(collection: gpu.StateConflicts);

        ShaderPipelineResourceStatus[] Cascades() => [.. node.ResourceStatus.Where(predicate: static status => status.Name.Contains(value: "shadow", comparisonType: StringComparison.Ordinal))];
        static SdfFrame Shaped(SdfShadowResolution resolution, int slots) {
            var lights = SdfLights.Default();

            lights.Set(index: 1, light: lights[0]);
            lights.Count = 2;
            lights.ShadowSlots.Configure(fadeCapacity: 0, slots: slots);
            lights.ShadowSlots.SetSlot(light: 0, slot: 0);
            if (slots > 1) {
                lights.ShadowSlots.SetSlot(light: 1, slot: 1);
            }
            var frame = Frame();

            return frame with { Lights = lights, Views = [frame.Views[0] with { Quality = new SdfViewQuality { ShadowResolution = resolution } }] };
        }
    }
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
