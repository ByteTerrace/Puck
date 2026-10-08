using System.Collections.Concurrent;
using System.Numerics;
using Puck.Abstractions.Cameras;
using Puck.Abstractions.Gpu;
using Puck.Abstractions.Presentation;
using Puck.Hosting;
using Puck.SignedDistance;
using Puck.Testing;
using Xunit;

namespace Puck.SdfVm.Tests;

/// <summary>One shadow kernel and one kernel per views variant serve every fade capacity: no capacity leases a pipeline of
/// its own, and a policy change or a handoff crossing creates none and waits for none.</summary>
public sealed class SdfShadowFadePipelinesLawTests {
    // The residency owns seventeen kernels (the receiver and the environment and screen reductions among them) plus three shared passes.
    private const long Pipelines = 20;

    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [Theory]
    public void EveryCapacityLeasesTheSamePipelines(int capacity) {
        var created = new ConcurrentBag<string>();
        var gpu = new FakeGpuDevice { BeforeComputePipeline = description => created.Add(item: description.Name) };
        var catalog = SdfTestPipelines.Cache();
        var source = new Source { Frame = Frame() with { Lights = Lights(capacity: capacity) } };
        using var residency = Residency(catalog: catalog, source: source);
        var context = Context(gpu: gpu);

        residency.ProduceFirstFrame(context: in context);
        _ = residency.WaitPipelineBuilds(cancellationToken: TestContext.Current.CancellationToken);
        Assert.DoesNotContain(collection: created, filter: static name => name.Contains(comparisonType: StringComparison.Ordinal, value: "-fade"));
        Assert.Equal(expected: Pipelines, actual: catalog.Pipelines.SharedPipelines);
        Assert.True(condition: catalog.Pipelines.Work.TryRead(kind: GpuWork.PipelinesCreated, value: out var pipelines));
        Assert.Equal(actual: pipelines, expected: Pipelines);
    }
    [Fact]
    public void APolicyChangeAndItsCrossingsCreateNoPipelineAndWaitForNone() {
        var gpu = new FakeGpuDevice();
        var catalog = SdfTestPipelines.Cache();
        var source = new Source { Frame = Frame() };
        using var residency = Residency(catalog: catalog, source: source);
        var context = Context(gpu: gpu);

        residency.ProduceFirstFrame(context: in context);
        _ = residency.WaitPipelineBuilds(cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(condition: catalog.Pipelines.Work.TryRead(kind: GpuWork.PipelinesCreated, value: out var initial));
        Assert.Equal(actual: initial, expected: Pipelines);
        foreach (var capacity in new[] { 1, 2, 0 }) {
            source.Frame = source.Frame with { Lights = Lights(capacity: capacity) };
            Assert.True(condition: residency.Produce(context: in context));
            Assert.True(condition: residency.IsReady, userMessage: residency.NotReadyReason);
            Assert.Equal(expected: capacity, actual: residency.Frame!.Lights.ShadowSlots.FadeCapacity);

            foreach (var active in new[] { true, false, true }) {
                var lights = Lights(capacity: capacity);

                if (active && (capacity > 0)) { lights.ShadowSlots.SetHandoffs(handoffs: [new SdfShadowHandoff(Incoming: 1, Outgoing: 0, Slot: 0, Weight: 0.5f)]); }
                source.Frame = source.Frame with { Lights = lights };
                Assert.True(condition: residency.Produce(context: in context));
                Assert.True(condition: residency.IsReady);
            }
            Assert.True(condition: catalog.Pipelines.Work.TryRead(kind: GpuWork.PipelinesCreated, value: out var after));
            Assert.Equal(actual: after, expected: initial);
        }
    }

    private static SdfLights Lights(int capacity) {
        var lights = new SdfLights { Count = 2 };
        var light = new SdfLight(SdfLightKind.Directional, Vector3.UnitY, Vector3.One, 1f, 0.1f, true);

        lights.Set(index: 0, light: light);
        lights.Set(index: 1, light: light);
        lights.ShadowSlots.Configure(fadeCapacity: capacity, slots: 1);
        lights.ShadowSlots.SetSlot(light: 0, slot: 0);
        return lights;
    }
    private static SdfFrame Frame() {
        var builder = new SdfProgramBuilder();

        builder.Sphere(material: builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One)), radius: 1f);
        return new SdfFrame(Program: builder.Build(), ProgramChanged: false, Time: 0f,
            Views: [new SdfViewSnapshot(Camera: CameraSnapshot.LookAt(position: new Vector3(x: 0f, y: 0f, z: -5f), target: Vector3.Zero,
                fieldOfViewRadians: 1f, viewportWidth: 32, viewportHeight: 32), Region: new NormalizedRect(Height: 1f, Width: 1f, X: 0f, Y: 0f))]) { Lights = Lights(capacity: 0) };
    }
    private static SdfWorldResidency Residency(Source source, SdfWorldPipelineCatalog catalog) => new(
        pipelines: catalog, frameSource: source, kernels: SdfTestPipelines.Kernels(), name: "fade-policy", width: 32, height: 32, brickPoolVoxelCapacity: 0);
    private static FrameContext Context(FakeGpuDevice gpu) => new(AccumulatorTicks: 0UL, DeltaTicks: 0UL, ElapsedTicks: 0UL, FrameDeltaTicks: 0UL,
        Host: new HostContext(capabilities: new Dictionary<Type, object> { [typeof(IGpuDeviceContext)] = gpu }), StepTicks: 0UL, TargetHeight: 32, TargetWidth: 32);

    private sealed class Source : ISdfFrameSource {
        public required SdfFrame Frame { get; set; }

        public SdfFrame CaptureFrame(uint width, uint height, float deltaSeconds, float interpolationAlpha) => Frame;
    }
}
