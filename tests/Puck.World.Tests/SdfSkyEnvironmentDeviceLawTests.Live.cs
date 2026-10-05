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
    public void VulkanFreezesActualPixelsPublicationAndTaintForAnEpoch() {
        using var device = HeadlessVulkanDevice.Create(nameof(SdfSkyEnvironmentDeviceLawTests));
        VerifyEpoch(device, ".spv");
    }

    [Fact]
    public void DirectXFreezesActualPixelsPublicationAndTaintForAnEpoch() {
        using var device = DirectXTestDevices.Hardware();
        VerifyEpoch(device, ".dxil");
    }

    [Fact]
    public void VulkanRefreshesALivePanoramaFromItsActualPublication() {
        using var device = HeadlessVulkanDevice.Create(nameof(SdfSkyEnvironmentDeviceLawTests));
        VerifyLivePanorama(device, ".spv");
    }

    [Fact]
    public void DirectXRefreshesALivePanoramaFromItsActualPublication() {
        using var device = DirectXTestDevices.Hardware();
        VerifyLivePanorama(device, ".dxil");
    }

    // A real conversion ring keeps publishing and reusing its slots. The two readers must see the original pixels
    // and their original provenance until the epoch ends, including when the live source becomes clean.
    private static void VerifyEpoch(IGpuDeviceContext device, string extension) {
        const string Package = "test.epoch-pixels";
        var cache = new GpuPassPipelineCache();
        var packages = new RenderGraphPackageRecorders(new GpuRegionCopyPass(bytecodeExtension: extension, pipelines: cache));
        SourceConversionPackage.RegisterAll(packages);
        var source = new LivePixels(device) { Tainted = true };
        var readers = new PixelReaders();
        packages.RegisterProducer("test.live-pixels", _ => source);
        packages.Register(Package, readers);
        var compiled = PixelGraph(Package);
        Assert.True(RenderGraphInstanceSet.TryCreate([LiveInstance(), Reader("left"), Reader("right")], out var set, out var setRefusal), setRefusal?.Message);
        Assert.True(RenderGraphRuntime.TryCreate(set, [null, compiled, compiled], "left", packages, cache,
            device, extension == ".dxil", out var runtime, out var refusal), refusal?.Message);
        using var graph = runtime;
        using var converter = graph.CreateConverter("epoch-live-pixels", LivePixels.Descriptor);
        source.Converter = converter;
        using var readback = device.Services.SurfaceTransferFactory.CreateReadback();
        var frame = 0L;
        void Produce() {
            _ = graph.ProduceFrame(new(DisplayHeight: 4, DisplayWidth: 4, DisplayHertz: 60, Index: frame, Tick: frame++,
                Roots: [new("left", 1, 1), new("right", 1, 1)],
                Footprints: [new("left", "feed", 1, 1), new("right", "feed", 1, 1)]), LiveContext(device));
            device.WaitIdle();
        }
        TestLiveness.Until(() => { Produce(); return graph.IsSettled && readers.Seen.Count == 2; }, reason: () => graph.Render.Reason);
        using var epoch = new RenderGraphReadEpoch();
        readers.Epoch = epoch;
        Produce();
        var frozen = readers.Seen["left"];
        Assert.Equal(frozen, readers.Seen["right"]);
        Assert.Equal(converter.Publication, frozen.Publication);
        Assert.True(frozen.Tainted);
        Assert.NotEqual(converter.Output.ImageHandle, frozen.Image.ImageHandle);
        AssertPixels(frozen, red: true);
        var originalHandles = new HashSet<nint>();
        source.Tainted = false;
        for (var update = 0; update < 12; update++) {
            source.Red = update % 2 != 0;
            source.Revision++;
            Produce();
            originalHandles.Add(converter.Output.ImageHandle);
            Assert.Equal(frozen, readers.Seen["left"]);
            Assert.Equal(frozen, readers.Seen["right"]);
            AssertPixels(frozen, red: true);
            AssertPixels(new(converter.Output, converter.OutputLayout, converter.Publication, false), source.Red);
        }
        Assert.True(originalHandles.Count < 12, "The live producer must actually reuse storage during the epoch.");
        Assert.True(epoch.Changed);
        Assert.True(converter.Publication.Sequence > frozen.Publication.Sequence + 3);
        epoch.Dispose();
        using var next = new RenderGraphReadEpoch();
        readers.Epoch = next;
        source.Red = false;
        source.Revision++;
        Produce();
        var adopted = readers.Seen["left"];
        Assert.Equal(adopted, readers.Seen["right"]);
        Assert.Equal(converter.Publication, adopted.Publication);
        Assert.NotEqual(frozen.Publication, adopted.Publication);
        Assert.False(adopted.Tainted);
        AssertPixels(adopted, red: false);
        graph.Dispose();
        Assert.False(next.IsValid);
        Assert.Equal(source.Acquired, source.Released);

        RenderGraphInstance Reader(string name) => new(Name: name, Passes: 1, Reads: [new("feed")], Refresh: RenderGraphRefresh.EveryFrame);
        void AssertPixels(PixelRead input, bool red) {
            var bytes = readback.Read(sourceImageHandle: input.Image.ImageHandle, sourceLayout: input.Layout,
                width: input.Image.Width, height: input.Image.Height, format: input.Image.Format, bytesPerPixel: 4);
            var values = bytes.Span;
            for (var at = 0; at < values.Length; at += 4) {
                Assert.Equal((byte)(red ? 255 : 0), values[at]);
                Assert.Equal((byte)0, values[at + 1]);
                Assert.Equal((byte)(red ? 0 : 255), values[at + 2]);
                Assert.Equal((byte)255, values[at + 3]);
            }
        }
    }

    // The sky definition, mapping and dimensions are held while successful conversion publications change and the
    // source ring rotates. The production environment factory must refresh both retained buffers and their identity.
    private static void VerifyLivePanorama(IGpuDeviceContext device, string extension) {
        var cache = new GpuPassPipelineCache();
        var pipelines = new SdfWorldPipelineCatalog(new GpuRegionCopyPass(bytecodeExtension: extension, pipelines: cache),
            new SdfMeshRasterPass(bytecodeExtension: extension, pipelines: cache));
        var sky = new SdfSky();
        sky.ClearLayers();
        sky.Add(new SdfSkyPanorama { Screen = 0, Intensity = 1 }, "live", visibility: SdfSkyVisibility.Lighting);
        var frameSource = new PanoramaFrame(sky);
        using var residency = new SdfWorldResidency(pipelines, frameSource, pipelines.LoadDeployed(extension),
            "panorama", 4, 4, screenSources: new PanoramaScreen(), brickPoolVoxelCapacity: 0);
        var views = new SdfWorldPasses(_ => new SdfWorldView(residency, 0));
        using var environment = new SdfSkyEnvironmentPasses(views);
        environment.Register("environment", residency, 0);
        var source = new LivePixels(device) { Tainted = true };
        var packages = new RenderGraphPackageRecorders(pipelines.RegionCopy);
        SourceConversionPackage.RegisterAll(packages);
        packages.RegisterProducer("test.live-pixels", _ => source);
        packages.Register(RenderGraphPackageCatalog.SkyEnvironment, environment);
        const string Observer = "test.panorama-observer";
        packages.Register(Observer, new PixelReaders());
        Assert.True(RenderGraphInstanceSet.TryCreate([LiveInstance(), new(Name: "environment",
            ExternalPackage: RenderGraphPackageCatalog.SkyEnvironment, Passes: SdfSkyEnvironmentGraph.Fragment.Passes.Count,
            Output: ShaderPipelineResourceKind.Buffer, Reads: [new("feed")], Refresh: RenderGraphRefresh.EveryFrame),
            new(Name: "observer", Passes: 1, Reads: [new("feed"), new("environment", Kind: ShaderPipelineResourceKind.Buffer)],
                Refresh: RenderGraphRefresh.EveryFrame)],
            out var set, out var setRefusal), setRefusal?.Message);
        Assert.True(RenderGraphRuntime.TryCreate(set, [null, null, PixelGraph(Observer, environment: true)], "observer", packages,
            cache, device, extension == ".dxil", out var runtime, out var refusal), refusal?.Message);
        using var graph = runtime;
        using var converter = graph.CreateConverter("panorama-live-pixels", LivePixels.Descriptor);
        source.Converter = converter;
        var context = LiveContext(device);
        var index = 0L;
        void Produce() {
            views.BeginFrame(context);
            residency.BeginFrame();
            _ = residency.Prepare(context);
            _ = graph.ProduceFrame(new(DisplayHeight: 4, DisplayWidth: 4, DisplayHertz: 60, Index: index, Tick: index++,
                Roots: [new("observer", 1, 1)], Footprints: [new("environment", "feed", 1, 1), new("observer", "feed", 1, 1)]), context);
            device.WaitIdle();
        }
        TestLiveness.Until(() => { Produce(); return residency.Tables?.CompletedSkyEnvironment.IsKnown == true; },
            reason: () => residency.NotReadyReason ?? graph.Render.Reason, wait: residency.WaitPipelineBuilds);
        var tables = residency.Tables!;
        var initial = tables.CompletedSkyEnvironment;
        Assert.True(tables.SubmittedSkyEnvironment!.Value.Tainted);
        AssertPanorama(red: true);
        for (var held = 0; held < 8; held++) { Produce(); }
        Assert.Equal(initial, tables.CompletedSkyEnvironment);
        var input = converter.Publication;
        source.Red = false;
        source.Tainted = false;
        source.Revision++;
        TestLiveness.Until(() => { Produce(); return tables.CompletedSkyEnvironment != initial; }, reason: () => graph.Render.Reason);
        Assert.NotEqual(input, converter.Publication);
        Assert.Same(initial.Owner, tables.CompletedSkyEnvironment.Owner);
        Assert.Equal(initial.Sequence + 1, tables.CompletedSkyEnvironment.Sequence);
        Assert.False(tables.SubmittedSkyEnvironment!.Value.Tainted);
        AssertPanorama(red: false);
        var updated = tables.CompletedSkyEnvironment;
        for (var held = 0; held < 8; held++) { Produce(); }
        Assert.Equal(updated, tables.CompletedSkyEnvironment);

        void AssertPanorama(bool red) {
            var map = ReadEnvironmentBuffer(device.Services, tables.SkyEnvironmentMap);
            var texels = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, Half>(map);
            for (var at = 0; at < texels.Length; at += 4) {
                Assert.Equal((Half)(red ? 1 : 0), texels[at]);
                Assert.Equal((Half)0, texels[at + 1]);
                Assert.Equal((Half)(red ? 0 : 1), texels[at + 2]);
            }
            var coefficients = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(ReadEnvironmentBuffer(device.Services, tables.SkyEnvironmentCoefficients));
            // A constant unit radiance has c00 = integral(Y00) = sqrt(4*pi), and no other bands.
            for (var band = 0; band < 9; band++) {
                for (var channel = 0; channel < 3; channel++) {
                    var expected = band == 0 && channel == (red ? 0 : 2) ? Math.Sqrt(4 * Math.PI) : 0;
                    Assert.InRange(coefficients[band * 4 + channel], expected - 0.0001, expected + 0.0001);
                }
            }
        }
    }

    private static RenderGraphRuntimeGraph PixelGraph(string package, bool environment = false) {
        var catalog = new RenderGraphPackageCatalog(packages: [new RenderGraphPackage(
            Id: package, Inputs: environment ? [RenderGraphPackagePort.Buffer(RenderGraphPortAccess.ComputeRead,
                strideBytes: 16, count: null)] : [], Outputs: [RenderGraphPackagePort.Image(RenderGraphPortAccess.ComputeWrite)],
            Members: [], Summary: "Observes acquired pixels after any declared environment dependency.")]);
        List<ShaderPipelineResource> resources = [new(Name: "color", Format: "R8G8B8A8Unorm", Dimensions: ShaderPipelineDimensions.Relative())];
        if (environment) {
            resources.Add(new(Name: "environment", Kind: ShaderPipelineResourceKind.Buffer,
                Initialization: ShaderPipelineInitialization.External, SizeBytes: SdfSkyEnvironment.CoefficientBytes, StrideBytes: 16));
        }
        var plan = new RenderGraphCompiler(catalog).Compile(new RenderGraphDefinition(
            Name: package, Schema: RenderGraphSchemas.Graph, Outputs: ["color"], Resources: resources,
            Packages: [new(Name: "observe", Package: package, Inputs: environment ? [new("environment")] : [], Outputs: ["color"])]));
        return new(new CompiledShaderPipeline(plan.Pipeline, new Dictionary<string, CompiledShader>()),
            environment ? [new(Version: "environment", Producer: "environment")] : []);
    }

    private static byte[] ReadEnvironmentBuffer(GpuDeviceServices services, IGpuBuffer source) {
        using var readback = services.BufferFactory.CreateReadback(sizeBytes: source.SizeBytes, name: default);
        using var commands = services.CommandPoolFactory.Create(name: default);
        var recorder = services.Recorder;
        var command = commands.CommandBufferHandle;
        recorder.BeginCommandBuffer(command);
        // The map's reduction and the observer's coefficient port leave both buffers in ComputeRead. Restore that
        // exact state so this diagnostic read does not change what the production graph's next barrier starts from.
        recorder.TransitionBuffer(command, source.BufferHandle, GpuAccess.ShaderRead,
            GpuAccess.TransferRead, GpuStage.ComputeShader, GpuStage.Transfer);
        recorder.CopyBuffer(command, source.BufferHandle, readback.BufferHandle, source.SizeBytes);
        recorder.TransitionBuffer(command, source.BufferHandle, GpuAccess.TransferRead,
            GpuAccess.ShaderRead, GpuStage.Transfer, GpuStage.ComputeShader);
        recorder.TransitionBuffer(command, readback.BufferHandle, GpuAccess.TransferWrite, GpuAccess.HostRead,
            GpuStage.Transfer, GpuStage.Host);
        recorder.EndCommandBuffer(command);
        services.QueueSubmitter.SubmitAndWait([command]);
        var bytes = new byte[source.SizeBytes];
        readback.Read(bytes);
        return bytes;
    }

    private static FrameContext LiveContext(IGpuDeviceContext device) => new(AccumulatorTicks: 0, DeltaTicks: 0,
        ElapsedTicks: 0, FrameDeltaTicks: 0, StepTicks: 0, TargetWidth: 4, TargetHeight: 4,
        Host: new HostContext(capabilities: new Dictionary<Type, object> { [typeof(IGpuDeviceContext)] = device }));

    private static RenderGraphInstance LiveInstance() => new(Name: "feed", ExternalPackage: "test.live-pixels", Passes: 1,
        Reads: [], Refresh: RenderGraphRefresh.EveryFrame) { OutputExtent = new(Width: 4, Height: 4) };

    private readonly record struct PixelRead(Surface Image, GpuImageLayout Layout, GpuImagePublication Publication, bool Tainted);

    private sealed class PixelReaders : IRenderGraphPackageFactory {
        public RenderGraphReadEpoch? Epoch { get; set; }
        public Dictionary<string, PixelRead> Seen { get; } = [];
        public bool SamplesReads => true;
        public RenderGraphReadEpoch? ReadEpochOf(string instance, string producer) => Epoch;
        public ValueTask<IDisposable?> BuildAsync(RenderGraphPackageRecorderContext context, CancellationToken cancellationToken) => ValueTask.FromResult<IDisposable?>(null);
        public IRenderGraphPackageRecorder Create(RenderGraphPackageRecorderContext context, IDisposable? built, RenderGraphPackageGroups groups) => new Reader(this, context.Instance);
        private sealed class Reader(PixelReaders owner, string instance) : IRenderGraphPackageRecorder {
            public void Dispose() { }
            public RenderGraphPackageOutcome Record(in RenderGraphPackageRecording recording) {
                var reads = recording.Reads!;
                var input = reads[0];
                owner.Seen[instance] = new(input.Image, input.Layout, input.Publication, input.Tainted);
                recording.Leases.Hold(reads.Take(0));
                recording.Recorder.ClearStorageImage(recording.CommandBuffer, recording.Outputs[0].Image.ImageHandle, GpuPixelFormat.R8G8B8A8Unorm);
                return RenderGraphPackageOutcome.Drew;
            }
        }
    }

    // The normal source converter supplies actual GPU pixels, ring reuse and submission identity. This fixture's
    // writer waits between updates so it never overwrites an in-flight reader; it does not promise epoch immutability.
    private sealed class LivePixels(IGpuDeviceContext device) : IRenderGraphExternalProducer {
        private long m_revision = -1;
        private readonly GpuWorkLedger m_empty = new(name: "unopened.live-pixels", framesInFlight: 1);
        public static ImageSourceDescriptor Descriptor { get; } = new(Producer: "live", Width: 4, Height: 4,
            Format: ImagePixelFormat.R8G8B8A8Unorm, Color: ImageColorEncoding.Srgb,
            Content: ImageContentClass.Deterministic, Cadence: ImageSourceCadence.Tick, Transport: ImageSourceTransport.Uploaded);
        public RenderGraphSourceConverter Converter { get; set; } = null!;
        public bool Red { get; set; } = true;
        public bool Tainted { get; set; }
        public long Revision { get; set; }
        public int Acquired { get; private set; }
        public int Released { get; private set; }
        public GpuPixelFormat Format => GpuPixelFormat.R8G8B8A8Unorm;
        public string? NotReadyReason => Converter?.Render.Reason;
        public string? PendingCapturePath => null;
        public IGpuWorkSource Work => Converter?.Work ?? m_empty;
        public void Dispose() { }
        public void OnDeviceLost() => Converter.OnDeviceLost();
        public void RequestCapture(FrameCaptureRequest request) => _ = request.TryFail(new NotSupportedException("Read the consuming image."));
        public FrameRender Produce(in FrameContext context, uint width, uint height, RenderGraphExternalReads? reads = null) {
            if (m_revision == Revision) { return FrameRender.Rendered; }
            var pixels = new byte[4 * 4 * 4];
            for (var at = 0; at < pixels.Length; at += 4) { pixels[at + (Red ? 0 : 2)] = 255; pixels[at + 3] = 255; }
            if (!Converter.TryConvert(context, pixels)) { return Converter.Render; }
            device.WaitIdle();
            m_revision = Revision;
            return FrameRender.Rendered;
        }
        public bool TryAcquireOutput(out RenderGraphExternalOutput output) {
            output = default;
            if (!Converter.Publication.IsKnown) { return false; }
            Acquired++;
            output = new(Converter.Output, Converter.OutputLayout,
                new GpuImageLease(Converter.ImageViewHandle, _ => Released++, Publication: Converter.Publication) { Image = Converter.Output }, Tainted);
            return true;
        }
    }

    private sealed class PanoramaFrame(SdfSky sky) : ISdfFrameSource {
        private readonly SdfProgram m_program = new SdfProgramBuilder().Build();
        public SdfFrame CaptureFrame(uint width, uint height, float deltaSeconds, float interpolationAlpha) => new(
            Program: m_program, ProgramChanged: false, Time: 0,
            Views: [new(Camera: CameraSnapshot.LookAt(new(0, 0, -5), Vector3.Zero, 1, width, height),
                Region: new(Height: 1, Width: 1, X: 0, Y: 0))]) { Sky = sky, IndirectTier = SdfIndirectTier.Off };
    }

    private sealed class PanoramaScreen : ISdfScreenSources {
        private readonly SourceMapping m_mapping = new(Source: SourceHandle.Producer("feed"),
            Placement: new SourcePlacement.Surface(Origin: Vector3.Zero, Right: Vector3.UnitX, Up: Vector3.UnitY, HalfWidth: 1, HalfHeight: 1),
            SourceWidth: 4, SourceHeight: 4, Crop: SourcePixelRect.Whole(width: 4, height: 4));
        public IReadOnlyList<int> Screens => [0];
        public bool Emits(int screen) => false;
        public SourceMapping? MappingOf(int screen) => m_mapping;
        public string? ReadOf(int view, int screen) => "feed";
    }
}
