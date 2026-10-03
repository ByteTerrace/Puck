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

/// <summary>Reachable shadow capacities acquire only their own variants, before handoffs, through residency readiness.</summary>
public sealed class SdfShadowFadePipelinesLawTests {
    // The F=0 residency owns fourteen base kernels (including environment and reduction) plus three shared passes.
    [InlineData(SdfShadowFadeVariants.None, 0, 0)]
    [InlineData(SdfShadowFadeVariants.One, 0, 1)]
    [InlineData(SdfShadowFadeVariants.Two, 0, 2)]
    [InlineData(SdfShadowFadeVariants.One | SdfShadowFadeVariants.Two, 0, 3)]
    [InlineData(SdfShadowFadeVariants.None, 1, 1)]
    [Theory]
    public void OnlyAuthoredOrCurrentlyDemandedCapacitiesCreatePipelines(SdfShadowFadeVariants authored, int current, int expectedMask) {
        var created = new ConcurrentBag<string>();
        var gpu = new FakeGpuDevice { BeforeComputePipeline = description => created.Add(item: description.Name) };
        var catalog = SdfTestPipelines.Cache();
        var source = new Source { Frame = Frame() with { ShadowFadeVariants = authored, Lights = Lights(capacity: current) } };
        using var residency = Residency(catalog: catalog, source: source);
        var context = Context(gpu: gpu);

        residency.ProduceFirstFrame(context: in context);
        _ = residency.WaitPipelineBuilds(cancellationToken: TestContext.Current.CancellationToken);
        var expected = new List<string>();

        foreach (var capacity in new[] { 1, 2 }) {
            if ((expectedMask & capacity) != 0) {
                expected.AddRange(collection: [$"sdf-world-shadow-fade{capacity}", $"sdf-world-views-fade{capacity}",
                    $"sdf-world-views-core-fade{capacity}", $"sdf-world-views-folds-fade{capacity}"]);
            }
        }
        Assert.Equal(expected: expected.Order(), actual: created.Where(predicate: static name => name.Contains(comparisonType: StringComparison.Ordinal, value: "-fade")).Order());
        Assert.Equal(expected: (17 + expected.Count), actual: catalog.Pipelines.SharedPipelines);
        Assert.True(condition: catalog.Pipelines.Work.TryRead(kind: GpuWork.PipelinesCreated, value: out var pipelines));
        Assert.Equal(expected: (17L + expected.Count), actual: pipelines);
    }
    [Fact]
    public async Task NewPolicyCapacityWaitsForItsPipelinesBeforeAnyHandoffAndCrossingsCreateNothing() {
        using var allowFadeBuilds = new ManualResetEventSlim(initialState: false);
        var gpu = new FakeGpuDevice {
            BeforeComputePipeline = description => {
                if (description.Name.Contains(comparisonType: StringComparison.Ordinal, value: "-fade")) { allowFadeBuilds.Wait(cancellationToken: TestContext.Current.CancellationToken); }
            },
        };
        var catalog = SdfTestPipelines.Cache();
        var source = new Source { Frame = Frame() };
        var residency = Residency(catalog: catalog, source: source);
        var context = Context(gpu: gpu);

        try {
            residency.ProduceFirstFrame(context: in context);
            foreach (var capacity in new[] { 1, 2 }) {
                allowFadeBuilds.Reset();
                var recycled = source.Frame.Lights;

                source.Frame = source.Frame with { Lights = Lights(capacity: capacity) };
                _ = residency.Produce(context: in context);
                Assert.False(condition: residency.IsReady);
                Assert.Equal(expected: (capacity - 1), actual: residency.Frame!.Lights.ShadowSlots.FadeCapacity);
                var ready = residency.WaitReadyAsync(cancellationToken: TestContext.Current.CancellationToken);

                Assert.False(condition: ready.IsCompleted);
                // World presentation alternates two mutable light tables. A subsequent capture may reuse the old one.
                recycled.ShadowSlots.Configure(fadeCapacity: capacity, slots: 1);
                Assert.Equal(expected: (capacity - 1), actual: residency.Frame!.Lights.ShadowSlots.FadeCapacity);
                allowFadeBuilds.Set();
                _ = residency.WaitPipelineBuilds(cancellationToken: TestContext.Current.CancellationToken);
                _ = residency.Produce(context: in context);
                Assert.True(condition: residency.IsReady, userMessage: residency.NotReadyReason);
                await ready;
                Assert.Equal(expected: capacity, actual: residency.Frame!.Lights.ShadowSlots.FadeCapacity);
                Assert.Equal(expected: 0, actual: source.Frame.Lights.ShadowSlots.FadeCount);
                Assert.True(condition: catalog.Pipelines.Work.TryRead(kind: GpuWork.PipelinesCreated, value: out var beforeCrossing));
                Assert.Equal(actual: beforeCrossing, expected: (17L + (4L * capacity)));

                foreach (var active in new[] { true, false, true }) {
                    var lights = Lights(capacity: capacity);

                    if (active) { lights.ShadowSlots.SetHandoffs(handoffs: [new SdfShadowHandoff(Incoming: 1, Outgoing: 0, Slot: 0, Weight: 0.5f)]); }
                    source.Frame = source.Frame with { Lights = lights };
                    Assert.True(condition: residency.Produce(context: in context));
                    Assert.True(condition: residency.IsReady);
                    Assert.True(condition: catalog.Pipelines.Work.TryRead(kind: GpuWork.PipelinesCreated, value: out var afterCrossing));
                    Assert.Equal(actual: afterCrossing, expected: beforeCrossing);
                }
            }
        } finally {
            allowFadeBuilds.Set();
            residency.Dispose();
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
