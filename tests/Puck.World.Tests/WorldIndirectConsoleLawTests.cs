using System.Globalization;
using System.Numerics;
using Puck.Abstractions.Cameras;
using Puck.Abstractions.Gpu;
using Puck.Abstractions.Presentation;
using Puck.Commands;
using Puck.Hosting;
using Puck.SdfVm;
using Puck.SignedDistance;
using Puck.Testing;
using Puck.World.Client;
using Puck.World.Server;
using Xunit;

namespace Puck.World.Tests;

[Collection(AllocationCollection.Name)]
public sealed class WorldIndirectConsoleLawTests {
    private sealed class UncapturedSource : ISdfFrameSource {
        public SdfFrame CaptureFrame(uint width, uint height, float deltaSeconds, float interpolationAlpha) => throw new InvalidOperationException(message: "No frame is needed for a queued presentation control.");
    }

    private static SdfWorldResidency Residency(string name) => new(pipelines: SdfTestPipelines.Cache(), frameSource: new UncapturedSource(),
        kernels: new SdfKernelSet(bytecode: SdfKernelSet.Kernels.Select(selector: _ => ((ReadOnlyMemory<byte>)new byte[] { 1 })).ToArray()),
        name: name, width: 1, height: 1, brickPoolVoxelCapacity: 0) { IndirectTierOverride = SdfIndirectTier.Medium };
    private static CommandRegistry Registry(HostRow row, WorldRenderProbe probe) => new(modules: [new WorldRenderLeverCommandModule(
        population: row.Server.Population, settings: new WorldRenderSettings(defaults: row.Server.Definition.Render), server: row.Server,
        link: row.Instance.Link, renderProbe: probe)]);

    [Fact]
    public void FreezeAndResetControlEveryActiveResidencyWithoutChangingTheAuthorityOrTier() {
        using var row = HostRow.Build(name: "indirect-console");
        using var first = Residency(name: "first");
        using var second = Residency(name: "second");
        var probe = new WorldRenderProbe();

        probe.RegisterIndirectResidency(first, active: true);
        probe.RegisterIndirectResidency(second, active: true);
        var registry = Registry(probe: probe, row: row);
        var document = WorldDefinitionSerialization.Serialize(definition: row.Server.Definition);

        Assert.False(condition: registry.Submit(line: "world.indirect-freeze on").IsError);
        Assert.True(condition: first.IndirectFrozen);
        Assert.True(condition: second.IndirectFrozen);
        Assert.False(condition: registry.Submit(line: "world.indirect-reset").IsError);
        Assert.True(condition: first.IndirectResetPending);
        Assert.True(condition: second.IndirectResetPending);
        Assert.Contains("reset-pending=True", registry.Submit(line: "world.indirect-freeze").Output);
        Assert.False(condition: registry.Submit(line: "world.indirect-freeze off").IsError);
        Assert.False(condition: first.IndirectFrozen);
        Assert.False(condition: second.IndirectFrozen);
        Assert.Equal(SdfIndirectTier.Medium, first.IndirectTier);
        Assert.Equal(SdfIndirectTier.Medium, second.IndirectTier);
        Assert.Equal(document, WorldDefinitionSerialization.Serialize(definition: row.Server.Definition));
        foreach (var residency in probe.IndirectResidencies) {
            foreach (var kind in residency.IndirectWork.WorkKinds) {
                Assert.True(condition: residency.IndirectWork.TryRead(kind: kind, value: out var value));
                Assert.Equal(actual: value, expected: 0L);
            }
        }
    }
    [InlineData("world.indirect-freeze wrong")]
    [InlineData("world.indirect-freeze on off")]
    [InlineData("world.indirect-reset now")]
    [Theory]
    public void MalformedPresentationControlsRefuseWithoutChangingAResidency(string command) {
        using var row = HostRow.Build(name: "indirect-refusal");
        using var residency = Residency(name: "first");
        var probe = new WorldRenderProbe();

        probe.RegisterIndirectResidency(residency, active: true);
        var result = Registry(probe: probe, row: row).Submit(line: command);

        Assert.True(condition: result.IsError, userMessage: result.Output);
        Assert.False(condition: residency.IndirectFrozen);
        Assert.False(condition: residency.IndirectResetPending);
    }
    [Fact]
    public void ASeatCannotFreezeOrResetPresentationCaches() {
        using var row = HostRow.Build(name: "indirect-seat");
        using var residency = Residency(name: "first");
        var probe = new WorldRenderProbe();

        probe.RegisterIndirectResidency(residency, active: true);
        var source = new TextCommandSource(Registry(probe: probe, row: row));
        var results = new List<CommandResult>();
        using var session = source.CreateSession(Principal.Seat(slot: 0), onResult: (_, result) => results.Add(item: result));

        session.Enqueue(line: "world.indirect-freeze on");
        session.Enqueue(line: "world.indirect-reset");
        source.Collect();
        Assert.Equal(2, results.Count);
        Assert.All(results, result => Assert.True(condition: result.IsError, userMessage: result.Output));
        Assert.False(condition: residency.IndirectFrozen);
        Assert.False(condition: residency.IndirectResetPending);
    }

    private sealed class FixedSource(SdfFrame frame) : ISdfFrameSource {
        public SdfFrame CaptureFrame(uint width, uint height, float deltaSeconds, float interpolationAlpha) => frame;
    }

    [Fact]
    public void AnInactiveCacheStaysInTheBudgetUntilItsLastGraphReaderReleasesIt() {
        var gpu = new FakeGpuDevice();
        var builder = new SdfProgramBuilder();

        builder.Sphere(material: builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One)), radius: 1f);
        var frame = new SdfFrame(Program: builder.Build(), ProgramChanged: false, Time: 0f,
            Views: [new SdfViewSnapshot(Camera: CameraSnapshot.LookAt(position: new Vector3(x: 0, y: 0, z: -5), target: Vector3.Zero,
                fieldOfViewRadians: 1f, viewportHeight: 32, viewportWidth: 32), Region: new NormalizedRect(Height: 1, Width: 1, X: 0, Y: 0))]);
        using var residency = new SdfWorldResidency(pipelines: SdfTestPipelines.Cache(), frameSource: new FixedSource(frame: frame),
            kernels: SdfTestPipelines.Kernels(), name: "retiring", width: 32, height: 32, brickPoolVoxelCapacity: 0) {
            IndirectTierOverride = SdfIndirectTier.Medium,
        };
        var context = new FrameContext(AccumulatorTicks: 0, DeltaTicks: 0, ElapsedTicks: 0, FrameDeltaTicks: 0,
            Host: new HostContext(new Dictionary<Type, object> { [typeof(IGpuDeviceContext)] = gpu }),
            StepTicks: 0, TargetHeight: 32, TargetWidth: 32);

        TestLiveness.Until(step: () => { residency.BeginFrame(); return residency.Prepare(context: context); },
            reason: () => residency.NotReadyReason, wait: residency.WaitPipelineBuilds);
        var reader = ((SdfIndirectCache?)residency.Tables!.Indirect!.Retain());

        try {
            Assert.NotNull(@object: reader);
            var bytes = reader.Bytes;
            var probe = new WorldRenderProbe();

            probe.RegisterIndirectResidency(residency, active: true);
            probe.RegisterIndirectResidency(residency, active: true);
            residency.IndirectTierOverride = SdfIndirectTier.Off;
            residency.BeginFrame();
            Assert.True(condition: residency.Prepare(context: context));
            probe.RegisterIndirectResidency(residency, active: false);
            Assert.Empty(collection: probe.IndirectResidencies);
            Assert.Same(residency, Assert.Single(collection: probe.IndirectAllocationResidencies));
            Assert.Equal(bytes, residency.Tables.IndirectBytes);
            Assert.Contains(("retiring-device=" + bytes.DeviceLocal.ToString(provider: CultureInfo.InvariantCulture)), WorldIndirectDiagnosticText.Describe(probe));
            reader.Dispose();
            reader = null;
            Assert.Empty(collection: probe.IndirectAllocationResidencies);
            Assert.Equal("indirect off, 0 byte(s)", WorldIndirectDiagnosticText.Describe(probe));
        } finally { reader?.Dispose(); }
    }
    [InlineData(SdfIndirectTier.Medium, 20_971_520UL, 41_943_040UL, 131_072UL, 100_735_645UL)]
    [InlineData(SdfIndirectTier.High, 335_544_320UL, 83_886_080UL, 262_144UL, 562_174_621UL)]
    [Theory]
    public void BudgetIncludesBothLightingGenerationsRetiringCachesAndTheWholeLightFragmentOnce(SdfIndirectTier tier,
        ulong radiance, ulong irradiance, ulong publication, ulong total) {
        var layout = new SdfIndirectLayout(tier: tier);
        var active = new GpuMemoryBytes(DeviceLocal: ((layout.ByteLength + 123) + 1024), HostVisible: 456);
        var all = (active + new GpuMemoryBytes(DeviceLocal: 789, HostVisible: 321));
        var output = WorldIndirectDiagnosticText.DescribeMemory(layout, all, active, lightDepth: 1024, lightFragment: 4096);

        foreach (var (field, value) in new[] { ("radiance", radiance), ("irradiance", irradiance), ("publication", publication), ("receiver-proofs", 4UL),
            ("regions-device", 123UL), ("regions-host", 456UL), ("retiring-device", 789UL), ("retiring-host", 321UL),
            ("light-view", 1024UL), ("light-fragment", 4096UL), ("total", total) }) {
            Assert.Contains(((field + "=") + value.ToString(provider: CultureInfo.InvariantCulture)), output);
        }
    }

    private sealed class Authority(WorldInstance instance) : IWorldConsoleAuthority {
        public bool TryResolve(CommandContext context, out WorldInstance resolved, out string refusal) {
            resolved = instance;
            refusal = string.Empty;
            return true;
        }
    }

    [Fact]
    public void LightingUsesTheSharedHostInventoryAndNamesUnavailableGpuFacts() {
        using var row = HostRow.Build(name: "indirect-lighting");
        using var residency = Residency(name: "first");
        var probe = new WorldRenderProbe();

        probe.RegisterIndirectResidency(residency, active: true);
        var expected = WorldIndirectDiagnosticText.Describe(probe);
        var registry = new CommandRegistry(modules: [new WorldLightingCommandModule(new Authority(instance: row.Instance),
            indirectReport: _ => WorldIndirectDiagnosticText.Describe(probe))]);
        var result = registry.Submit(line: "world.lighting");

        Assert.False(condition: result.IsError, userMessage: result.Output);
        Assert.EndsWith(expected, result.Output);
        Assert.Contains(actualString: expected, expectedSubstring: "cache=unallocated");
        Assert.Contains("no renderer", WorldIndirectDiagnosticText.Describe(null));
    }
    [Fact]
    public void LightingKeepsTheRetainedPixelsResidencyAndCensusSeparateFromLiveHostInventory() {
        using var row = HostRow.Build(name: "indirect-retained-lighting");
        using var live = Residency(name: "live");
        using var capturedOwner = Residency(name: "captured-pane");
        var probe = new WorldRenderProbe();

        probe.RegisterIndirectResidency(live, active: true);
        var pick = new SdfIndirectPick(SdfIndirectPickStatus.Resolved, SdfIndirectTier.Medium, 0, 1,
            Vector3.Zero, Vector3.UnitY, .1f, Vector3.UnitY, 1, 23, [], default,
            new SdfIndirectCacheSnapshot(Allocation: 31, Bricks: [], Bytes: default, CompletedSweeps: 4, Epoch: 7, FarDistance: 64, Frozen: false, Levels: [], LightingComplete: true, PendingClassifications: 0,
                PendingPlacements: 0, PendingShades: 0, PendingTraces: 0, PublishedGeneration: 1, PublishedStamp: 23, PublishedSweeps: 3, Submission: 8, Tier: SdfIndirectTier.Medium, TraceComplete: true), new SdfIndirectCensus(Active: 2, Dormant: 5, Inactive: 4, Relocated: 3, Unpublished: 6), null) {
            SourcesEnabled = SdfIndirectSources.Emission,
        };
        var registry = new CommandRegistry(modules: [new WorldLightingCommandModule(new Authority(instance: row.Instance),
            indirectReport: _ => WorldIndirectDiagnosticText.Describe(captured: pick, capturedResidency: capturedOwner, probe: probe))]);
        var result = registry.Submit(line: "world.lighting");

        Assert.False(condition: result.IsError, userMessage: result.Output);
        Assert.Contains("indirect live", result.Output);
        Assert.Contains("retained-pixel residency=captured-pane allocation=31 epoch=7 generation=1 stamp=23", result.Output);
        Assert.Contains("sources=0x04", result.Output);
        Assert.EndsWith(WorldIndirectPickText.DescribeCensus(pick: pick), result.Output);
        Assert.DoesNotContain("retained-pixel", WorldIndirectDiagnosticText.Describe(probe, pick));
    }
}
