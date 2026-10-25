using System.Numerics;
using Puck.Abstractions.Cameras;
using Puck.Abstractions.Gpu;
using Puck.Abstractions.Presentation;
using Puck.Abstractions.Sources;
using Puck.Commands;
using Puck.Hosting;
using Puck.SdfVm;
using Puck.Shaders;
using Puck.SignedDistance;
using Puck.Testing;
using Xunit;

namespace Puck.World.Tests;

public sealed partial class SdfSkyEnvironmentDeviceLawTests {
    [Fact]
    public void VulkanRefreshesALivePanoramaFromItsActualPublication() {
        using var device = HeadlessVulkanDevice.Create(nameof(SdfSkyEnvironmentDeviceLawTests));

        VerifyLivePanorama(device: device, extension: ".spv");
    }
    [Fact]
    public void DirectXRefreshesALivePanoramaFromItsActualPublication() {
        using var device = DirectXTestDevices.Hardware();

        VerifyLivePanorama(device: device, extension: ".dxil");
    }

    // The sky definition, mapping and dimensions are held while successful conversion publications change and the
    // source ring rotates. The production environment factory must refresh both retained buffers and their identity.
    private static void VerifyLivePanorama(IGpuDeviceContext device, string extension) {
        var cache = new GpuPassPipelineCache();
        var pipelines = new SdfWorldPipelineCatalog(new GpuRegionCopyPass(bytecodeExtension: extension, pipelines: cache),
            new SdfMeshRasterPass(bytecodeExtension: extension, pipelines: cache));
        var sky = new SdfSky();

        sky.ClearLayers();
        sky.Add(new SdfSkyPanorama { Intensity = 1, Screen = 0 }, "live", visibility: SdfSkyVisibility.Lighting);
        var frameSource = new PanoramaFrame(sky: sky);
        using var residency = new SdfWorldResidency(pipelines, frameSource, pipelines.LoadDeployed(bytecodeExtension: extension),
            "panorama", 4, 4, screenSources: new PanoramaScreen(), brickPoolVoxelCapacity: 0);
        var views = new SdfWorldPasses(_ => new SdfWorldView(Residency: residency, View: 0));
        using var environment = new SdfSkyEnvironmentPasses(views: views);

        environment.Register(name: "environment", residency: residency, view: 0);
        var source = new LivePixels(device: device) { Tainted = true };
        var packages = new RenderGraphPackageRecorders(regionCopy: pipelines.RegionCopy);

        SourceConversionPackage.RegisterAll(packages);
        packages.RegisterProducer(factory: _ => source, package: "test.live-pixels");
        packages.Register(factory: environment, package: RenderGraphPackageCatalog.SkyEnvironment);
        const string Observer = "test.panorama-observer";

        packages.Register(Observer, new PixelReaders());
        Assert.True(condition: RenderGraphInstanceSet.TryCreate([LiveInstance(), new(Name: "environment",
            ExternalPackage: RenderGraphPackageCatalog.SkyEnvironment, Passes: SdfSkyEnvironmentGraph.Fragment.Passes.Count,
            Output: ShaderPipelineResourceKind.Buffer, Reads: [new("feed")], Refresh: RenderGraphRefresh.EveryFrame),
            new(Name: "observer", Passes: 1, Reads: [new("feed"), new("environment", Kind: ShaderPipelineResourceKind.Buffer)],
                Refresh: RenderGraphRefresh.EveryFrame)],
            out var set, out var setRefusal), userMessage: setRefusal?.Message);
        Assert.True(condition: RenderGraphRuntime.TryCreate(set, [null, null, PixelGraph(Observer, environment: true)], "observer", packages,
            cache, device, (extension == ".dxil"), out var runtime, out var refusal), userMessage: refusal?.Message);
        using var graph = runtime;
        using var converter = graph.CreateConverter("panorama-live-pixels", LivePixels.Descriptor);

        source.Converter = converter;
        var context = LiveContext(device: device);
        var index = 0L;
        var stage = "residency pipelines";
        string? previousStatus = null;

        string Status() => (((((string)$"{extension} {stage}: frame={index}, residency={(residency.NotReadyReason ?? "ready")}; converter={(converter.Render.Reason ?? "rendered")}, source={converter.Publication.Sequence}; ") +
            $"environment={NodeStatus(node: graph.Node(instance: 1))}, observer={NodeStatus(node: graph.Node(instance: 2))}; ") +
            $"submitted={residency.Tables?.SubmittedSkyEnvironment?.Sequence}, completed={residency.Tables?.CompletedSkyEnvironment.Sequence}; ") +
            $"graph={(graph.Render.Reason ?? "rendered")}");
        static string NodeStatus(ShaderPipelineRenderNode node) => (node.LastSwapError?.Message ??
            $"ready={node.IsReady}, building={node.IsBuildingCandidate}, pending={node.HasPendingCandidate}");
        void Report() {
            var status = Status();

            if (status != previousStatus) { Console.Error.WriteLine(value: status); previousStatus = status; }
            Assert.Null(@object: residency.Refusal);
            Assert.NotEqual(FrameCompletion.Refused, graph.Render.Completion);
            Assert.Null(@object: graph.Node(instance: 1).LastSwapError);
            Assert.Null(@object: graph.Node(instance: 2).LastSwapError);
        }
        // Finish the actual pipeline work under the liveness watchdog before counting graph transitions. A cold
        // driver wait names the outstanding kernels; publication then has its own bounded frame count.
        TestLiveness.Until(step: () => {
            residency.BeginFrame();
            var ready = residency.Prepare(context: context);

            Report();
            return ready;
        }, reason: Status, wait: residency.WaitPipelineBuilds);
        void Produce() {
            views.BeginFrame(context: context);
            residency.BeginFrame();
            _ = residency.Prepare(context: context);
            _ = graph.ProduceFrame(new(DisplayHeight: 4, DisplayWidth: 4, DisplayHertz: 60, Index: index, Tick: index++,
                Roots: [new(Height: 1, Instance: "observer", Width: 1)], Footprints: [new(Consumer: "environment", Height: 1, Producer: "feed", Width: 1), new(Consumer: "observer", Height: 1, Producer: "feed", Width: 1)]), context);
            device.WaitIdle();
            Report();
        }
        bool Building() => (converter.IsBuilding || graph.Node(instance: 1).IsBuildingCandidate || graph.Node(instance: 2).IsBuildingCandidate);
        stage = "initial panorama publication";
        TestLiveness.Within(frames: 16, step: () => { Produce(); return (residency.Tables?.CompletedSkyEnvironment.IsKnown == true); },
            building: Building, reason: Status);
        var tables = residency.Tables!;
        var initial = tables.CompletedSkyEnvironment;

        Assert.True(condition: tables.SubmittedSkyEnvironment!.Value.Tainted);
        AssertPanorama(red: true);
        for (var held = 0; (held < 8); held++) { Produce(); }
        Assert.Equal(initial, tables.CompletedSkyEnvironment);
        var input = converter.Publication;

        source.Red = false;
        source.Tainted = false;
        source.Revision++;
        stage = "changed panorama publication";
        TestLiveness.Within(frames: 16, step: () => { Produce(); return (tables.CompletedSkyEnvironment != initial); },
            building: Building, reason: Status);
        Assert.NotEqual(input, converter.Publication);
        Assert.Same(initial.Owner, tables.CompletedSkyEnvironment.Owner);
        Assert.Equal((initial.Sequence + 1), tables.CompletedSkyEnvironment.Sequence);
        Assert.False(condition: tables.SubmittedSkyEnvironment!.Value.Tainted);
        AssertPanorama(red: false);
        var updated = tables.CompletedSkyEnvironment;

        for (var held = 0; (held < 8); held++) { Produce(); }
        Assert.Equal(updated, tables.CompletedSkyEnvironment);

        void AssertPanorama(bool red) {
            var map = ReadEnvironmentBuffer(services: device.Services, source: tables.SkyEnvironmentMap);
            var texels = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, Half>(span: map);

            for (var at = 0; (at < texels.Length); at += 4) {
                Assert.Equal(((Half)(red ? 1 : 0)), texels[at]);
                Assert.Equal(((Half)0), texels[(at + 1)]);
                Assert.Equal(((Half)(red ? 0 : 1)), texels[(at + 2)]);
            }
            var coefficients = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(span: ReadEnvironmentBuffer(services: device.Services, source: tables.SkyEnvironmentCoefficients));
            // A constant unit radiance has c00 = integral(Y00) = sqrt(4*pi), and no other bands.
            for (var band = 0; (band < 9); band++) {
                for (var channel = 0; (channel < 3); channel++) {
                    var expected = (((band == 0) && (channel == (red ? 0 : 2))) ? Math.Sqrt(d: (4 * Math.PI)) : 0);

                    Assert.InRange(coefficients[((band * 4) + channel)], (expected - 0.0001), (expected + 0.0001));
                }
            }
        }
    }
    private static RenderGraphRuntimeGraph PixelGraph(string package, bool environment = false) {
        var catalog = new RenderGraphPackageCatalog(packages: [new RenderGraphPackage(
            Id: package, Inputs: (environment ? [RenderGraphPackagePort.Buffer(RenderGraphPortAccess.ComputeRead,
                strideBytes: 16, count: null)] : []), Outputs: [RenderGraphPackagePort.Image(access: RenderGraphPortAccess.ComputeWrite)],
            Members: [], Summary: "Observes acquired pixels after any declared environment dependency.")]);
        List<ShaderPipelineResource> resources = [new(Name: "color", Format: "R8G8B8A8Unorm", Dimensions: ShaderPipelineDimensions.Relative())];

        if (environment) {
            resources.Add(item: new(Name: "environment", Kind: ShaderPipelineResourceKind.Buffer,
                Initialization: ShaderPipelineInitialization.External, SizeBytes: SdfSkyEnvironment.CoefficientBytes, StrideBytes: 16));
        }
        var plan = new RenderGraphCompiler(catalog).Compile(definition: new RenderGraphDefinition(
            Name: package, Schema: RenderGraphSchemas.Graph, Outputs: ["color"], Resources: resources,
            Packages: [new(Name: "observe", Package: package, Inputs: (environment ? [new("environment")] : []), Outputs: ["color"])]));

        return new(new CompiledShaderPipeline(plan: plan.Pipeline, shaders: new Dictionary<string, CompiledShader>()),
            (environment ? [new(Version: "environment", Producer: "environment")] : []));
    }
    private static byte[] ReadEnvironmentBuffer(GpuDeviceServices services, IGpuBuffer source) {
        using var readback = services.BufferFactory.CreateReadback(sizeBytes: source.SizeBytes, name: default);
        using var commands = services.CommandPoolFactory.Create(name: default);
        var recorder = services.Recorder;
        var command = commands.CommandBufferHandle;

        recorder.BeginCommandBuffer(commandBufferHandle: command);
        // The map's reduction and the observer's coefficient port leave both buffers in ComputeRead. Restore that
        // exact state so this diagnostic read does not change what the production graph's next barrier starts from.
        recorder.TransitionBuffer(command, source.BufferHandle, GpuAccess.ShaderRead,
            GpuAccess.TransferRead, GpuStage.ComputeShader, GpuStage.Transfer);
        recorder.CopyBuffer(command, source.BufferHandle, readback.BufferHandle, source.SizeBytes);
        recorder.TransitionBuffer(command, source.BufferHandle, GpuAccess.TransferRead,
            GpuAccess.ShaderRead, GpuStage.Transfer, GpuStage.ComputeShader);
        recorder.TransitionBuffer(command, readback.BufferHandle, GpuAccess.TransferWrite, GpuAccess.HostRead,
            GpuStage.Transfer, GpuStage.Host);
        recorder.EndCommandBuffer(commandBufferHandle: command);
        services.QueueSubmitter.SubmitAndWait(commandBufferHandles: [command]);
        var bytes = new byte[source.SizeBytes];

        readback.Read(destination: bytes);
        return bytes;
    }
    private static FrameContext LiveContext(IGpuDeviceContext device) => new(AccumulatorTicks: 0, DeltaTicks: 0,
        ElapsedTicks: 0, FrameDeltaTicks: 0, StepTicks: 0, TargetWidth: 4, TargetHeight: 4,
        Host: new HostContext(capabilities: new Dictionary<Type, object> { [typeof(IGpuDeviceContext)] = device }));
    private static RenderGraphInstance LiveInstance() => new(Name: "feed", ExternalPackage: "test.live-pixels", Passes: 1,
        Reads: [], Refresh: RenderGraphRefresh.EveryFrame) { OutputExtent = new(Height: 4, Width: 4) };

    private readonly record struct PixelRead(Surface Image, GpuImageLayout Layout, GpuImagePublication Publication, bool Tainted);
    private sealed class PixelReaders : IRenderGraphPackageFactory {
        public bool SamplesReads => true;
        public Dictionary<string, PixelRead> Seen { get; } = [];

        public ValueTask<IDisposable?> BuildAsync(RenderGraphPackageRecorderContext context, CancellationToken cancellationToken) => ValueTask.FromResult<IDisposable?>(result: null);
        public IRenderGraphPackageRecorder Create(RenderGraphPackageRecorderContext context, IDisposable? built, RenderGraphPackageGroups groups) => new Reader(this, context.Instance);

        private sealed class Reader(PixelReaders owner, string instance) : IRenderGraphPackageRecorder {
            public void Dispose() { }
            public RenderGraphPackageOutcome Record(in RenderGraphPackageRecording recording) {
                var reads = recording.Reads!;
                var input = reads[0];

                owner.Seen[instance] = new(Image: input.Image, Layout: input.Layout, Publication: input.Publication, Tainted: input.Tainted);
                recording.Leases.Hold(lease: reads.Take(index: 0));
                var image = recording.Outputs[0].Image.ImageHandle;
                // The package boundary is ComputeWrite. Its test clear is a transfer on Vulkan and a UAV write on
                // DirectX; restore that declared boundary before the graph records the consumer's next access.
                recording.Recorder.TransitionImageLayout(recording.CommandBuffer, image,
                    oldLayout: GpuImageLayout.General, newLayout: GpuImageLayout.General,
                    sourceAccessMask: GpuAccess.ShaderWrite, destinationAccessMask: GpuAccess.TransferWrite,
                    sourceStageMask: GpuStage.ComputeShader, destinationStageMask: GpuStage.Transfer);
                recording.Recorder.ClearStorageImage(recording.CommandBuffer, image, GpuPixelFormat.R8G8B8A8Unorm);
                recording.Recorder.TransitionImageLayout(recording.CommandBuffer, image,
                    oldLayout: GpuImageLayout.General, newLayout: GpuImageLayout.General,
                    sourceAccessMask: GpuAccess.TransferWrite, destinationAccessMask: GpuAccess.ShaderWrite,
                    sourceStageMask: GpuStage.Transfer, destinationStageMask: GpuStage.ComputeShader);
                return RenderGraphPackageOutcome.Drew;
            }
        }
    }
    // The normal source converter supplies actual GPU pixels, ring reuse and submission identity. This fixture's
    // writer waits between updates so it never overwrites an in-flight reader.
    private sealed class LivePixels(IGpuDeviceContext device) : IRenderGraphExternalProducer {
        private long m_revision = -1;
        private readonly GpuWorkLedger m_empty = new(framesInFlight: 1, name: "unopened.live-pixels");

        public static ImageSourceDescriptor Descriptor { get; } = new(Producer: "live", Width: 4, Height: 4,
            Format: ImagePixelFormat.R8G8B8A8Unorm, Color: ImageColorEncoding.Srgb,
            Content: ImageContentClass.Deterministic, Cadence: ImageSourceCadence.Tick, Transport: ImageSourceTransport.Uploaded);

        public RenderGraphSourceConverter Converter { get; set; } = null!;
        public bool Red { get; set; } = true;

        public int Acquired { get; private set; }
        public GpuPixelFormat Format => GpuPixelFormat.R8G8B8A8Unorm;
        public string? NotReadyReason => Converter?.Render.Reason;
        public string? PendingCapturePath => null;
        public int Released { get; private set; }
        public long Revision { get; set; }
        public bool Tainted { get; set; }
        public IGpuWorkSource Work => (Converter?.Work ?? m_empty);

        public void Dispose() { }
        public void OnDeviceLost() => Converter.OnDeviceLost();
        public void RequestCapture(FrameCaptureRequest request) => _ = request.TryFail(error: new NotSupportedException(message: "Read the consuming image."));
        public FrameRender Produce(in FrameContext context, uint width, uint height, RenderGraphExternalReads? reads = null) {
            if (m_revision == Revision) { return FrameRender.Rendered; }
            var pixels = new byte[((4 * 4) * 4)];

            for (var at = 0; (at < pixels.Length); at += 4) { pixels[(at + (Red ? 0 : 2))] = 255; pixels[(at + 3)] = 255; }
            if (!Converter.TryConvert(context: context, planes: pixels)) { return Converter.Render; }
            device.WaitIdle();
            m_revision = Revision;
            return FrameRender.Rendered;
        }
        public bool TryAcquireOutput(out RenderGraphExternalOutput output) {
            output = default;
            if (!Converter.Publication.IsKnown) { return false; }
            Acquired++;
            output = new(Image: Converter.Output, Layout: Converter.OutputLayout,
                Lease: new GpuImageLease(Converter.ImageViewHandle, _ => Released++, Publication: Converter.Publication) { Image = Converter.Output }, Tainted: Tainted);
            return true;
        }
    }
    private sealed class PanoramaFrame(SdfSky sky) : ISdfFrameSource {
        private readonly SdfProgram m_program = new SdfProgramBuilder().Build();

        public SdfFrame CaptureFrame(uint width, uint height, float deltaSeconds, float interpolationAlpha) => new(
            Program: m_program, ProgramChanged: false, Time: 0,
            Views: [new(Camera: CameraSnapshot.LookAt(new(x: 0, y: 0, z: -5), Vector3.Zero, 1, width, height),
                Region: new(Height: 1, Width: 1, X: 0, Y: 0))]) { IndirectTier = SdfIndirectTier.Off, Sky = sky };
    }
    private sealed class PanoramaScreen : ISdfScreenSources {
        private readonly SourceMapping m_mapping = new(Source: SourceHandle.Producer(name: "feed"),
            Placement: new SourcePlacement.Surface(Origin: Vector3.Zero, Right: Vector3.UnitX, Up: Vector3.UnitY, HalfWidth: 1, HalfHeight: 1),
            SourceWidth: 4, SourceHeight: 4, Crop: SourcePixelRect.Whole(height: 4, width: 4));

        public IReadOnlyList<int> Screens => [0];

        public bool Emits(int screen) => false;
        public SdfScreenFace FaceOf(int screen) => SdfScreenFace.Opening;
        public SourceMapping? MappingOf(int screen) => m_mapping;
        public string? ReadOf(int view, int screen) => "feed";
    }
}
