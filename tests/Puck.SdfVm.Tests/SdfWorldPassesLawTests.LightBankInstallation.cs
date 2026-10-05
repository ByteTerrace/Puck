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

        lights.ShadowSlots.SetOwner(owner: "sun", slot: 0);
        var frame = Frame() with {
            Program = builder.Build(),
            Lights = lights,
            IndirectTier = SdfIndirectTier.Medium,
            FarDistance = 12,
        };
        using var residency = new SdfWorldResidency(brickPoolVoxelCapacity: 0,
            frameSource: new FixedFrameSource(frame: frame), height: Extent, width: Extent, name: "world",
            kernels: SdfTestPipelines.Kernels(), pipelines: pipelines);
        var views = new SdfWorldPasses(resolve: _ => new SdfWorldView(Residency: residency, View: 0));
        const string Light = "world.indirect-light";
        const string Reader = "light-bank-reader";
        const ulong BankBytes = (((2UL * 512) * 512) * sizeof(float));

        views.RegisterLightView(name: Light, residency: residency);
        var packages = new RenderGraphPackageRecorders(regionCopy: pipelines.RegionCopy);

        packages.Register(factory: views, package: RenderGraphPackageCatalog.SdfWorld);
        var reader = LightBankReaderGraph(bytes: BankBytes, packages: packages, producer: Light);
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
        RenderGraphRuntime Create() {
            Assert.True(condition: RenderGraphRuntime.TryCreate(deviceContext: gpu, graphs: [null, reader],
                hostsOnDirectX: false, packages: packages, pipelines: pipelines.Pipelines, root: Reader, set: set,
                runtime: out var runtime, refusal: out var refusal), userMessage: refusal?.Message);
            return runtime;
        }
        var number = 0L;

        void Complete(RenderGraphRuntime graph, ulong publications) {
            TestLiveness.Within(frames: 16, step: () => {
                var scheduled = new RenderGraphFrame(DisplayHeight: ((int)Extent), DisplayWidth: ((int)Extent), DisplayHertz: 60,
                    Footprints: [], Index: number, Tick: number++, Roots: [new RenderGraphRoot(Height: 1, Instance: Reader, Width: 1)]);

                _ = graph.ProduceFrame(context: context, frame: scheduled);
                return (residency.IndirectLightViews.Publications == publications);
            }, building: () => graph.Node(instance: 0).IsBuildingCandidate, reason: () => graph.Render.Reason);
        }
        using var original = Create();

        Complete(original, publications: 2);
        Assert.Equal((512u, 512u), views.RenderExtentOf(instance: Light)!.FrameAt(height: Extent, width: Extent));
        Assert.Equal(1.0, views.RenderExtentOf(instance: Light)!.Grid);
        var cache = residency.Tables!.Indirect!;
        var geometry = residency.Tables.LightGeometry;
        var regions = Enumerable.Range(count: 2, start: 0).Select(selector: residency.IndirectLightViews.Snapshot).ToArray();

        Assert.All(regions, region => Assert.True(condition: region.Valid));
        Assert.Equal(BankBytes, cache.LightViewBytes);

        // Keep the completed graph and its bank alive and dormant: neither release nor a changed scene may
        // invalidate its metadata. Installing a fresh bank must independently schedule both unchanged regions.
        Assert.Equal(2UL, residency.IndirectLightViews.Publications);
        Assert.All(Enumerable.Range(count: 2, start: 0), index => Assert.Equal(regions[index], residency.IndirectLightViews.Snapshot(index: index)));
        using var replacement = Create();

        Complete(replacement, publications: 4);
        Assert.Same(cache, residency.Tables.Indirect);
        Assert.Equal(geometry, residency.Tables.LightGeometry);
        Assert.Equal((2 * BankBytes), cache.LightViewBytes);
        Assert.All(Enumerable.Range(count: 2, start: 0), index => Assert.Equal(regions[index], residency.IndirectLightViews.Snapshot(index: index)));
        Assert.True(condition: residency.IndirectWork.TryRead(kind: SdfIndirectWork.LightRegions, value: out var recorded));
        Assert.Equal(actual: recorded, expected: 4L);
    }

    // A buffer-producing light graph must be reached by an image root, as it is in the real World graph.
    // The reader consumes the actual published bank; no hidden root or synthetic light publication is needed.
    private static RenderGraphRuntimeGraph LightBankReaderGraph(string producer, RenderGraphPackageRecorders packages, ulong bytes) {
        const string Package = "test.light-bank-reader";

        packages.Register(factory: new LightBankReader(), package: Package);
        var catalog = new RenderGraphPackageCatalog(packages: [new(Id: Package, Members: [], Summary: "Reads the published light depth bank.",
            Inputs: [RenderGraphPackagePort.Buffer(access: RenderGraphPortAccess.ComputeRead, count: null, strideBytes: sizeof(float))],
            Outputs: [RenderGraphPackagePort.Image(access: RenderGraphPortAccess.ComputeWrite)])]);
        var compiled = new RenderGraphCompiler(catalog).Compile(definition: new RenderGraphDefinition(
            Name: "light-bank-reader", Schema: RenderGraphSchemas.Graph,
            Resources: [new(Name: "image", Kind: ShaderPipelineResourceKind.Image, Format: "R8G8B8A8Unorm",
                Dimensions: ShaderPipelineDimensions.Relative()),
                new(Name: "depth", Kind: ShaderPipelineResourceKind.Buffer, SizeBytes: bytes, StrideBytes: sizeof(float),
                    Initialization: ShaderPipelineInitialization.External)], Outputs: ["image"],
            Packages: [new(Name: "read", Package: Package, Inputs: ["depth"], Outputs: ["image"])]));

        return new(Pipeline: new CompiledShaderPipeline(plan: compiled.Pipeline, shaders: new Dictionary<string, CompiledShader>()),
            Inputs: [new(Version: "depth", Producer: producer)]);
    }

    private sealed class LightBankReader : IRenderGraphPackageFactory {
        public ValueTask<IDisposable?> BuildAsync(RenderGraphPackageRecorderContext context, CancellationToken cancellationToken) =>
            ValueTask.FromResult<IDisposable?>(result: null);
        public IRenderGraphPackageRecorder Create(RenderGraphPackageRecorderContext context, IDisposable? built, RenderGraphPackageGroups groups) => new Recorder();

        private sealed class Recorder : IRenderGraphPackageRecorder {
            public void Dispose() { }
            public RenderGraphPackageOutcome Record(in RenderGraphPackageRecording recording) {
                Assert.NotNull(@object: recording.Inputs[0].Buffer);
                return RenderGraphPackageOutcome.Drew;
            }
        }
    }
}
