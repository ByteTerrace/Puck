using System.Numerics;
using System.Runtime.Versioning;
using Puck.Abstractions.Cameras;
using Puck.Abstractions.Gpu;
using Puck.Abstractions.Presentation;
using Puck.Hosting;
using Puck.SdfVm;
using Puck.Shaders;
using Puck.SignedDistance;
using Puck.Testing;
using Xunit;

namespace Puck.World.Tests;

// The primary hit is matte in both scenes. Only the shading-only shell emits, so the resolve's reactivity input must
// describe the shaded material rather than the primary visibility record's material. The readback uses the graph's
// normal package-copy path and therefore observes the buffer the real views pass wrote on each backend.
[SupportedOSPlatform("windows10.0.15063")]
[Trait("Category", "Gpu")]
public sealed class SdfReactivityDeviceLawTests {
    private const uint Extent = 32;

    [Fact]
    public void VulkanReactsToEmissionSelectedByDetailShading() {
        using var device = HeadlessVulkanDevice.Create(applicationName: nameof(SdfReactivityDeviceLawTests));

        Verify(device: device, extension: ".spv");
    }
    [Fact]
    public void DirectXReactsToEmissionSelectedByDetailShading() {
        using var device = DirectXTestDevices.Hardware();

        Verify(device: device, extension: ".dxil");
    }

    private static void Verify(IGpuDeviceContext device, string extension) {
        Assert.InRange(actual: Reactivity(device: device, emissive: false, extension: extension), low: 0f, high: 0.001f);
        Assert.InRange(actual: Reactivity(device: device, emissive: true, extension: extension), low: 0.8f, high: 1f);
    }
    // The default lights at zero strength: the room is unlit, so the emission alone decides the pixel's color.
    private static SdfLights Dark() {
        var lights = SdfLights.Default();

        for (var index = 0; (index < lights.Count); index++) {
            lights.Set(
                index: index,
                light: (lights[index] with {
                    Param = 0f,
                    Weight = 0f,
                })
            );
        }

        return lights;
    }
    private static float Reactivity(IGpuDeviceContext device, string extension, bool emissive) {
        SdfInstruction Sphere(float radius, uint material, bool detail) => new(
            Op: SdfOp.ShapeBlend, Shape: ((uint)SdfShapeType.Sphere), Blend: ((uint)SdfBlendOp.Union), Material: material,
            Data0: new Vector4(w: 0f, x: radius, y: 0f, z: 0f), Data1: Vector4.Zero, Detail: detail);
        var program = new SdfProgram(instances: null, screenSurfaces: null,
            instructions: [Sphere(detail: false, material: 0u, radius: 1f), Sphere(detail: true, material: 1u, radius: 1.1f)],
            materials: [new SdfMaterial(Albedo: Vector3.One), new SdfMaterial(Albedo: Vector3.One, Emissive: (emissive ? 4f : 0f))]);
        var frame = new SdfFrame(Program: program, ProgramChanged: false, Time: 0f,
            Views: [new SdfViewSnapshot(
                Camera: new CameraSnapshot(Position: new Vector3(x: 0f, y: 0f, z: -3f), Right: Vector3.UnitX,
                    Up: Vector3.UnitY, Forward: Vector3.UnitZ, TanHalfFieldOfView: 0.5f, AspectRatio: 1f),
                Region: new NormalizedRect(Height: 1f, Width: 1f, X: 0f, Y: 0f)) {
                Quality = new SdfViewQuality { Temporal = true },
            }]) { Lights = Dark() };
        var pipelines = new GpuPassPipelineCache();
        var catalog = new SdfWorldPipelineCatalog(
            regionCopy: new GpuRegionCopyPass(bytecodeExtension: extension, pipelines: pipelines),
            meshRaster: new SdfMeshRasterPass(bytecodeExtension: extension, pipelines: pipelines));
        using var residency = new SdfWorldResidency(pipelines: catalog, frameSource: new Source(frame: frame),
            kernels: catalog.LoadDeployed(bytecodeExtension: extension), name: "world", width: Extent, height: Extent, brickPoolVoxelCapacity: 0);
        var passes = new SdfWorldPasses(resolve: _ => new SdfWorldView(Residency: residency, View: 0));
        var probe = new ProbeFactory(inner: passes);
        var packages = new RenderGraphPackageRecorders(regionCopy: catalog.RegionCopy);

        packages.Register(factory: probe, package: RenderGraphPackageCatalog.SdfWorld);
        Assert.True(condition: RenderGraphInstanceSet.TryCreate(instances: [new RenderGraphInstance(
            ExternalPackage: RenderGraphPackageCatalog.SdfWorld, Name: "world", Passes: SdfWorldPackage.TemporalFragment.Passes.Count,
            Reads: [], Refresh: RenderGraphRefresh.EveryFrame)], set: out var set, refusal: out var setRefusal), userMessage: setRefusal?.Message);
        Assert.True(condition: RenderGraphRuntime.TryCreate(deviceContext: device, graphs: new RenderGraphRuntimeGraph?[1],
            hostsOnDirectX: (extension == ".dxil"), packages: packages, pipelines: pipelines, root: "world", set: set,
            runtime: out var runtime, refusal: out var refusal), userMessage: refusal?.Message);
        using var running = runtime!;
        var context = new FrameContext(AccumulatorTicks: 0, DeltaTicks: 0, ElapsedTicks: 0, FrameDeltaTicks: 0, StepTicks: 0,
            TargetWidth: Extent, TargetHeight: Extent,
            Host: new HostContext(capabilities: new Dictionary<Type, object> { [typeof(IGpuDeviceContext)] = device }));
        var index = 0L;

        TestLiveness.Until(step: () => {
            var scheduled = new RenderGraphFrame(DisplayHeight: ((int)Extent), DisplayWidth: ((int)Extent), DisplayHertz: 60,
                Footprints: [], Index: index, Tick: index++, Roots: [new RenderGraphRoot(Height: 1, Instance: "world", Width: 1)]);

            _ = running.ProduceFrame(context: in context, frame: in scheduled);
            return (probe.Latest is not null);
        }, reason: () => residency.NotReadyReason, wait: residency.WaitPipelineBuilds);
        device.WaitIdle();
        Span<byte> result = stackalloc byte[sizeof(float)];

        probe.Latest!.Read(destination: result);
        return BitConverter.ToSingle(value: result);
    }

    private sealed class Source(SdfFrame frame) : ISdfFrameSource {
        public SdfFrame CaptureFrame(uint width, uint height, float deltaSeconds, float interpolationAlpha) => frame;
    }
    private sealed class ProbeFactory(SdfWorldPasses inner) : IRenderGraphPackageFactory {
        public IGpuReadbackBuffer? Latest { get; set; }

        public void BeginFrame(in FrameContext context) => inner.BeginFrame(context: in context);
        public IShaderPipelineStorageCounter? CounterOf(string instance) => inner.CounterOf(instance: instance);
        public IShaderPipelineRenderExtent? RenderExtentOf(string instance) => inner.RenderExtentOf(instance: instance);
        public RenderGraphPackageFragment? FragmentOf(string instance) => inner.FragmentOf(instance: instance);
        public ValueTask<IDisposable?> BuildAsync(RenderGraphPackageRecorderContext context, CancellationToken cancellationToken) => inner.BuildAsync(cancellationToken: cancellationToken, context: context);
        public IRenderGraphPackageRecorder Create(RenderGraphPackageRecorderContext context, IDisposable? built, RenderGraphPackageGroups groups) {
            var recorder = inner.Create(built: built, context: context, groups: groups);

            return ((context.Part == SdfWorldPackage.Resolve) ? new Probe(context: context, inner: recorder, owner: this) : recorder);
        }
    }
    private sealed class Probe : IRenderGraphPackageRecorder, IRenderGraphPackageReadback {
        public ulong ReadbackBytes => m_copies.Aggregate(0UL, static (bytes, copy) => checked((bytes + copy.SizeBytes)));

        private readonly IRenderGraphPackageRecorder m_inner;
        private readonly ProbeFactory m_owner;
        private readonly IGpuReadbackBuffer[] m_copies;

        private string m_version = string.Empty;
        private ulong m_offset;

        public Probe(IRenderGraphPackageRecorder inner, ProbeFactory owner, RenderGraphPackageRecorderContext context) {
            m_inner = inner;
            m_owner = owner;
            m_copies = [.. Enumerable.Range(start: 0, count: ((int)context.InFlightFrames)).Select(selector: _ =>
                context.Services.BufferFactory.CreateReadback(name: default, sizeBytes: sizeof(float)))];
        }

        public RenderGraphPackageOutcome Record(in RenderGraphPackageRecording recording) {
            m_version = recording.Inputs[3].Version;
            m_offset = (sizeof(float) * (((recording.RenderHeight / 2) * recording.RenderWidth) + (recording.RenderWidth / 2)));
            return m_inner.Record(recording: in recording);
        }
        public bool TryReadback(int slot, int index, out RenderGraphBufferReadback readback) {
            readback = new RenderGraphBufferReadback(Version: m_version, SourceOffsetBytes: m_offset,
                SizeBytes: sizeof(float), Destination: m_copies[slot]);
            return (index == 0);
        }
        public void Submitted(int slot, IGpuSubmissionFence fence) => m_owner.Latest = m_copies[slot];
        public void Dispose() {
            m_inner.Dispose();
            foreach (var copy in m_copies) { copy.Dispose(); }
        }
    }
}
