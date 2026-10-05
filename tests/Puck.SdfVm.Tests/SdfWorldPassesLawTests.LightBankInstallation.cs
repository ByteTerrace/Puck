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
    public void InstallingAFreshLightBankRepublishesEveryUnchangedRegion() {
        var gpu = new FakeGpuDevice();
        var pipelines = SdfTestPipelines.Cache();
        var builder = new SdfProgramBuilder();
        var material = builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One));
        builder.BeginInstance(boundCenter: Vector3.Zero, boundRadius: 2);
        builder.Sphere(material: material, radius: 1);
        builder.EndInstance();
        var lights = SdfLights.Default();
        lights.ShadowSlots.SetOwner(slot: 0, owner: "sun");
        var frame = Frame() with { Program = builder.Build(), Lights = lights,
            IndirectTier = SdfIndirectTier.Medium, FarDistance = 12 };
        using var residency = new SdfWorldResidency(brickPoolVoxelCapacity: 0,
            frameSource: new FixedFrameSource(frame: frame), height: Extent, width: Extent, name: "world",
            kernels: SdfTestPipelines.Kernels(), pipelines: pipelines);
        var views = new SdfWorldPasses(resolve: _ => new SdfWorldView(Residency: residency, View: 0));
        const string Light = "world.indirect-light";
        views.RegisterLightView(name: Light, residency: residency);
        var packages = new RenderGraphPackageRecorders(regionCopy: pipelines.RegionCopy);
        packages.Register(factory: views, package: RenderGraphPackageCatalog.SdfWorld);
        var context = new FrameContext(AccumulatorTicks: 0, DeltaTicks: 0, ElapsedTicks: 0, FrameDeltaTicks: 0,
            Host: new HostContext(capabilities: new Dictionary<Type, object> { [typeof(IGpuDeviceContext)] = gpu }),
            StepTicks: 0, TargetHeight: Extent, TargetWidth: Extent);
        residency.ProduceFirstFrame(context: context);
        Assert.True(RenderGraphInstanceSet.TryCreate(instances: [new RenderGraphInstance(Name: Light,
            Refresh: RenderGraphRefresh.EveryFrame, Passes: SdfWorldPackage.LightViewFragment(maps: 2).Passes.Count,
            Reads: [], Output: ShaderPipelineResourceKind.Buffer, ExternalPackage: RenderGraphPackageCatalog.SdfWorld)],
            set: out var set, refusal: out var setRefusal), setRefusal?.Message);
        RenderGraphRuntime Create() {
            Assert.True(RenderGraphRuntime.TryCreate(deviceContext: gpu, graphs: new RenderGraphRuntimeGraph?[1],
                hostsOnDirectX: false, packages: packages, pipelines: pipelines.Pipelines, root: Light, set: set,
                runtime: out var runtime, refusal: out var refusal), refusal?.Message);
            return runtime;
        }
        var number = 0L;
        void Complete(RenderGraphRuntime graph, ulong publications) {
            TestLiveness.Within(frames: 16, step: () => {
                var scheduled = new RenderGraphFrame(DisplayHeight: ((int)Extent), DisplayWidth: ((int)Extent), DisplayHertz: 60,
                    Footprints: [], Index: number, Tick: number++, Roots: [new RenderGraphRoot(Height: 1, Width: 1, Instance: Light)]);
                _ = graph.ProduceFrame(context: context, frame: scheduled);
                return residency.IndirectLightViews.Publications == publications;
            }, building: () => graph.Node(instance: 0).IsBuildingCandidate, reason: () => graph.Render.Reason);
        }
        using var original = Create();
        Complete(original, publications: 2);
        Assert.Equal((512u, 512u), views.RenderExtentOf(Light)!.FrameAt(width: Extent, height: Extent));
        Assert.Equal(1.0, views.RenderExtentOf(Light)!.Grid);
        var cache = residency.Tables!.Indirect!;
        var geometry = residency.Tables.LightGeometry;
        var regions = Enumerable.Range(0, 2).Select(residency.IndirectLightViews.Snapshot).ToArray();
        Assert.All(regions, region => Assert.True(region.Valid));
        const ulong BankBytes = 2UL * 512 * 512 * sizeof(float);
        Assert.Equal(BankBytes, cache.LightViewBytes);

        // Keep the completed graph and its bank alive and dormant: neither release nor a changed scene may
        // invalidate its metadata. Installing a fresh bank must independently schedule both unchanged regions.
        Assert.Equal(2UL, residency.IndirectLightViews.Publications);
        Assert.All(Enumerable.Range(0, 2), index => Assert.Equal(regions[index], residency.IndirectLightViews.Snapshot(index)));
        using var replacement = Create();
        Complete(replacement, publications: 4);
        Assert.Same(cache, residency.Tables.Indirect);
        Assert.Equal(geometry, residency.Tables.LightGeometry);
        Assert.Equal(2 * BankBytes, cache.LightViewBytes);
        Assert.All(Enumerable.Range(0, 2), index => Assert.Equal(regions[index], residency.IndirectLightViews.Snapshot(index)));
        Assert.True(residency.IndirectWork.TryRead(kind: SdfIndirectWork.LightRegions, value: out var recorded));
        Assert.Equal(4L, recorded);
    }
}
