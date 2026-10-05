using System.Numerics;
using Puck.Abstractions.Gpu;
using Puck.Hosting;
using Puck.Shaders;
using Puck.SignedDistance;
using Puck.Testing;
using Xunit;

namespace Puck.SdfVm.Tests;

public sealed partial class SdfWorldPassesLawTests {
    [Fact]
    public void TheResidencyLightCameraPublishesAtNativeExtentAndStandsAfterItsRegionsFinish() {
        var naming = new RecordingGpuObjectNaming(isEnabled: true);
        var gpu = new FakeGpuDevice(trackObjects: true, naming: naming) { DistinctBuffers = true };
        var pipelines = SdfTestPipelines.Cache();
        var builder = new SdfProgramBuilder();
        var material = builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One));
        builder.BeginInstance(boundCenter: Vector3.Zero, boundRadius: 2);
        builder.Sphere(material: material, radius: 1);
        builder.EndInstance();
        var lights = SdfLights.Default();
        lights.ShadowSlots.SetOwner(slot: 0, owner: "sun");
        var frame = Frame() with { Program = builder.Build(), Lights = lights, IndirectTier = SdfIndirectTier.Medium, FarDistance = 12 };
        frame = frame with { Views = [frame.Views[0] with { RenderScale = 0.25f, Quality = new SdfViewQuality { Temporal = true } }] };
        using var residency = new SdfWorldResidency(brickPoolVoxelCapacity: 0, frameSource: new CapturingFrameSource(capture: () => frame),
            height: Extent, width: Extent, name: "world", kernels: SdfTestPipelines.Kernels(), pipelines: pipelines);
        var views = new SdfWorldPasses(resolve: _ => new SdfWorldView(Residency: residency, View: 0));
        const string Light = "world.indirect-light";
        const string Reader = "light-bank-reader";
        const ulong BankBytes = 2UL * 512 * 512 * sizeof(float);
        views.RegisterLightView(name: Light, residency: residency);
        var packages = new RenderGraphPackageRecorders(regionCopy: pipelines.RegionCopy);
        packages.Register(factory: views, package: RenderGraphPackageCatalog.SdfWorld);
        var context = new FrameContext(AccumulatorTicks: 0, DeltaTicks: 0, ElapsedTicks: 0, FrameDeltaTicks: 0,
            Host: new HostContext(capabilities: new Dictionary<Type, object> { [typeof(IGpuDeviceContext)] = gpu }),
            StepTicks: 0, TargetHeight: Extent, TargetWidth: Extent);
        residency.ProduceFirstFrame(context: context);
        Assert.True(condition: RenderGraphInstanceSet.TryCreate(instances: [new RenderGraphInstance(Name: Light,
            Refresh: RenderGraphRefresh.EveryFrame, Passes: SdfWorldPackage.LightViewFragment(maps: 2).Passes.Count,
            Reads: [], Output: ShaderPipelineResourceKind.Buffer, ExternalPackage: RenderGraphPackageCatalog.SdfWorld),
            new RenderGraphInstance(Name: Reader, Refresh: RenderGraphRefresh.EveryFrame, Passes: 1,
                Reads: [new(Light, Kind: ShaderPipelineResourceKind.Buffer)])],
            set: out var set, refusal: out var setRefusal), userMessage: setRefusal?.Message);
        Assert.True(condition: RenderGraphRuntime.TryCreate(deviceContext: gpu, graphs: [null, LightBankReaderGraph(Light, packages, BankBytes)],
            hostsOnDirectX: false, packages: packages, pipelines: pipelines.Pipelines, root: Reader, set: set,
            runtime: out var runtime, refusal: out var refusal), userMessage: refusal?.Message);
        using var graph = runtime;
        var number = 0L;
        void Produce() {
            var scheduled = new RenderGraphFrame(DisplayHeight: ((int)Extent), DisplayWidth: ((int)Extent), DisplayHertz: 60,
                Footprints: [], Index: number, Tick: number++, Roots: [new RenderGraphRoot(Height: 1, Width: 1, Instance: Reader)]);
            _ = graph.ProduceFrame(context: context, frame: scheduled);
        }
        TestLiveness.Within(frames: 16, step: () => {
            Produce();
            return residency.IndirectLightViews.Publications == 2 && graph.Render.Completion == FrameCompletion.Rendered;
        }, building: () => graph.Node(instance: 0).IsBuildingCandidate || graph.Node(instance: 1).IsBuildingCandidate, reason: () => graph.Render.Reason);
        Assert.Equal(expected: (512u, 512u), actual: views.RenderExtentOf(instance: Light)!.FrameAt(width: 32, height: 32));
        Assert.Equal(expected: 1.0, actual: views.RenderExtentOf(instance: Light)!.Grid);
        Assert.True(condition: residency.IndirectWork.TryRead(kind: SdfIndirectWork.LightRegions, value: out var before));
        Assert.Equal(expected: 2L, actual: before);
        var cache = residency.Tables!.Indirect!;
        var cacheBuffer = Assert.Single(gpu.Created, item => item.Handle == cache.Buffer.BufferHandle);
        Assert.Equal(BankBytes, cache.LightViewBytes);
        var bankName = Assert.Single(naming.Applied, item => item.Name == Light + "/" + SdfWorldPackage.IndirectLightDepth);
        var bank = Assert.Single(gpu.Created, item => item.Handle == bankName.Handle);
        Assert.Equal(0, bank.DisposeCount);
        Assert.Equal(0UL, Assert.Single(graph.Node(0).ResourceStatus,
            item => item.Name == SdfWorldPackage.IndirectLightDepth).AllocationBytes);
        // Visibility96 + mesh16 + hardware depth4, plus less than one MiB for the remaining traversal and constants.
        // The cache's two-MiB bank is separate: counting even one copy in this graph breaches the upper bound.
        Assert.InRange(graph.Node(0).AllocationBytes, 30_408_704UL, 30_408_704UL + 1_048_576UL);
        var counter = views.CounterOf(instance: Light)!;
        var revision = counter.Revision;
        var capacity = residency.CapacityRevision;
        for (var index = 0; (index < 4); index++) { Produce(); }
        Assert.True(condition: residency.IndirectWork.TryRead(kind: SdfIndirectWork.LightRegions, value: out var after));
        Assert.Equal(expected: before, actual: after);
        Assert.All(collection: Enumerable.Range(start: 0, count: 2), action: index => Assert.True(condition: residency.IndirectLightViews.Snapshot(index: index).Valid));
        Assert.Equal(revision, counter.Revision);
        Assert.Same(cache, residency.Tables.Indirect);
        Assert.Equal(0, bank.DisposeCount);
        Assert.Equal(0, cacheBuffer.DisposeCount);
        Assert.Equal(cache.Bytes, residency.Tables.IndirectBytes);
        frame = frame with { FarDistance = 24f };
        Produce();
        var replacement = residency.Tables.Indirect!;
        Assert.NotSame(cache, replacement);
        Assert.Equal(capacity, residency.CapacityRevision);
        Assert.True(counter.Revision > revision, "An equal-capacity cache replacement must rebuild its light-bank owner.");
        TestLiveness.Within(frames: 16, step: () => {
            Produce();
            return replacement.LightViewBytes == BankBytes && graph.Render.Completion == FrameCompletion.Rendered
                && residency.IndirectWork.TryRead(kind: SdfIndirectWork.LightRegions, value: out var recorded)
                && recorded == before + 2;
        }, building: () => graph.Node(instance: 0).IsBuildingCandidate, reason: () => {
            _ = residency.IndirectWork.TryRead(kind: SdfIndirectWork.LightRegions, value: out var recorded);
            return $"Replacement bank bytes={replacement.LightViewBytes}/{BankBytes}, regions={recorded}/{before + 2}, " +
                $"publications={residency.IndirectLightViews.Publications}, pending={residency.IndirectLightViews.Pending}, " +
                $"completion={graph.Render.Completion}, light-building={graph.Node(0).IsBuildingCandidate}, " +
                $"reader-building={graph.Node(1).IsBuildingCandidate}: {graph.Render.Reason}";
        });
        var replacementName = Assert.Single(naming.Applied, item => item.Name == Light + "/" + SdfWorldPackage.IndirectLightDepth && item.Handle != bankName.Handle);
        var replacementBank = Assert.Single(gpu.Created, item => item.Handle == replacementName.Handle);
        Assert.True(residency.IndirectWork.TryRead(kind: SdfIndirectWork.LightRegions, value: out var completed));
        Assert.Equal(before + 2, completed);
        // Ordinary consumer frames must release the obsolete owner while unchanged light regions stand.
        TestLiveness.Within(frames: 16, step: () => {
            Produce();
            return bank.DisposeCount == 1;
        }, building: () => graph.Node(instance: 0).IsBuildingCandidate, reason: () => "The replaced light bank did not retire behind its reader fence.");
        Assert.True(residency.IndirectWork.TryRead(kind: SdfIndirectWork.LightRegions, value: out var standing));
        Assert.Equal(completed, standing);
        Assert.All(Enumerable.Range(0, 2), index => Assert.True(residency.IndirectLightViews.Snapshot(index).Valid));
        Assert.Equal(1, cacheBuffer.DisposeCount);
        Assert.Equal(0UL, cache.LightViewBytes);
        Assert.Equal(replacement.Bytes, residency.Tables.IndirectBytes);
        Assert.Equal(0, residency.Tables.RetiringIndirectCacheCount);
        Assert.Equal(0, replacementBank.DisposeCount);
        Assert.Equal(BankBytes, replacement.LightViewBytes);
        revision = counter.Revision;
        Produce();
        Assert.Equal(revision, counter.Revision);
        Assert.Same(replacement, residency.Tables.Indirect);
        Assert.True(condition: residency.IndirectWork.TryRead(kind: SdfIndirectWork.LightRegions, value: out before));
        frame = frame with { MeshDraws = [new SdfMeshDraw(Mesh: SdfMeshCard.Mesh, ObjectToWorld: Matrix4x4.Identity, Material: 0, Identity: "independent-triangle")] };
        Produce();
        Assert.All(collection: Enumerable.Range(start: 0, count: 2), action: index => Assert.Null(@object: residency.IndirectLightViews.Snapshot(index: index).Projection));
        Assert.True(condition: residency.IndirectWork.TryRead(kind: SdfIndirectWork.LightRegions, value: out after));
        Assert.Equal(expected: before, actual: after);
        var allocated = replacement.Bytes;
        graph.Dispose();
        Assert.Equal(1, bank.DisposeCount);
        Assert.Equal(1, replacementBank.DisposeCount);
        Assert.Equal(0UL, replacement.LightViewBytes);
        Assert.Equal(allocated, replacement.Bytes + new GpuMemoryBytes(DeviceLocal: BankBytes, HostVisible: 0));
    }
}
