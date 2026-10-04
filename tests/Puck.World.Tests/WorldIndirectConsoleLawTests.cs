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
        public SdfFrame CaptureFrame(uint width, uint height, float deltaSeconds, float interpolationAlpha) => throw new InvalidOperationException("No frame is needed for a queued presentation control.");
    }
    private static SdfWorldResidency Residency(string name) => new(pipelines: SdfTestPipelines.Cache(), frameSource: new UncapturedSource(),
        kernels: new SdfKernelSet(SdfKernelSet.Kernels.Select(_ => (ReadOnlyMemory<byte>)new byte[] { 1 }).ToArray()),
        name: name, width: 1, height: 1, brickPoolVoxelCapacity: 0) { IndirectTierOverride = SdfIndirectTier.Medium };
    private static CommandRegistry Registry(HostRow row, WorldRenderProbe probe) => new(modules: [new WorldRenderLeverCommandModule(
        population: row.Server.Population, settings: new WorldRenderSettings(row.Server.Definition.Render), server: row.Server,
        link: row.Instance.Link, renderProbe: probe)]);

    [Fact]
    public void FreezeAndResetControlEveryActiveResidencyWithoutChangingTheAuthorityOrTier() {
        using var row = HostRow.Build(name: "indirect-console");
        using var first = Residency("first");
        using var second = Residency("second");
        var probe = new WorldRenderProbe();
        probe.RegisterIndirectResidency(first, active: true);
        probe.RegisterIndirectResidency(second, active: true);
        var registry = Registry(row, probe);
        var document = WorldDefinitionSerialization.Serialize(row.Server.Definition);
        Assert.False(registry.Submit("world.indirect-freeze on").IsError);
        Assert.True(first.IndirectFrozen);
        Assert.True(second.IndirectFrozen);
        Assert.False(registry.Submit("world.indirect-reset").IsError);
        Assert.True(first.IndirectResetPending);
        Assert.True(second.IndirectResetPending);
        Assert.Contains("reset-pending=True", registry.Submit("world.indirect-freeze").Output);
        Assert.False(registry.Submit("world.indirect-freeze off").IsError);
        Assert.False(first.IndirectFrozen);
        Assert.False(second.IndirectFrozen);
        Assert.Equal(SdfIndirectTier.Medium, first.IndirectTier);
        Assert.Equal(SdfIndirectTier.Medium, second.IndirectTier);
        Assert.Equal(document, WorldDefinitionSerialization.Serialize(row.Server.Definition));
        foreach (var residency in probe.IndirectResidencies) {
            foreach (var kind in residency.IndirectWork.WorkKinds) {
                Assert.True(residency.IndirectWork.TryRead(kind, out var value));
                Assert.Equal(0L, value);
            }
        }
    }

    [Theory]
    [InlineData("world.indirect-freeze wrong")]
    [InlineData("world.indirect-freeze on off")]
    [InlineData("world.indirect-reset now")]
    public void MalformedPresentationControlsRefuseWithoutChangingAResidency(string command) {
        using var row = HostRow.Build(name: "indirect-refusal");
        using var residency = Residency("first");
        var probe = new WorldRenderProbe();
        probe.RegisterIndirectResidency(residency, active: true);
        var result = Registry(row, probe).Submit(command);
        Assert.True(result.IsError, result.Output);
        Assert.False(residency.IndirectFrozen);
        Assert.False(residency.IndirectResetPending);
    }

    [Fact]
    public void ASeatCannotFreezeOrResetPresentationCaches() {
        using var row = HostRow.Build(name: "indirect-seat");
        using var residency = Residency("first");
        var probe = new WorldRenderProbe();
        probe.RegisterIndirectResidency(residency, active: true);
        var source = new TextCommandSource(Registry(row, probe));
        var results = new List<CommandResult>();
        using var session = source.CreateSession(Principal.Seat(0), onResult: (_, result) => results.Add(result));
        session.Enqueue("world.indirect-freeze on");
        session.Enqueue("world.indirect-reset");
        source.Collect();
        Assert.Equal(2, results.Count);
        Assert.All(results, result => Assert.True(result.IsError, result.Output));
        Assert.False(residency.IndirectFrozen);
        Assert.False(residency.IndirectResetPending);
    }

    private sealed class FixedSource(SdfFrame frame) : ISdfFrameSource {
        public SdfFrame CaptureFrame(uint width, uint height, float deltaSeconds, float interpolationAlpha) => frame;
    }
    [Fact]
    public void AnInactiveCacheStaysInTheBudgetUntilItsLastGraphReaderReleasesIt() {
        var gpu = new FakeGpuDevice();
        var builder = new SdfProgramBuilder();
        builder.Sphere(material: builder.AddMaterial(new SdfMaterial(Albedo: Vector3.One)), radius: 1f);
        var frame = new SdfFrame(Program: builder.Build(), ProgramChanged: false, Time: 0f,
            Views: [new SdfViewSnapshot(CameraSnapshot.LookAt(position: new Vector3(0, 0, -5), target: Vector3.Zero,
                fieldOfViewRadians: 1f, viewportHeight: 32, viewportWidth: 32), new NormalizedRect(0, 0, 1, 1))]);
        using var residency = new SdfWorldResidency(pipelines: SdfTestPipelines.Cache(), frameSource: new FixedSource(frame),
            kernels: SdfTestPipelines.Kernels(), name: "retiring", width: 32, height: 32, brickPoolVoxelCapacity: 0) {
            IndirectTierOverride = SdfIndirectTier.Medium,
        };
        var context = new FrameContext(AccumulatorTicks: 0, DeltaTicks: 0, ElapsedTicks: 0, FrameDeltaTicks: 0,
            Host: new HostContext(new Dictionary<Type, object> { [typeof(IGpuDeviceContext)] = gpu }),
            StepTicks: 0, TargetHeight: 32, TargetWidth: 32);
        TestLiveness.Until(step: () => { residency.BeginFrame(); return residency.Prepare(context); },
            reason: () => residency.NotReadyReason, wait: residency.WaitPipelineBuilds);
        var reader = (SdfIndirectCache?)residency.Tables!.Indirect!.Retain();
        try {
            Assert.NotNull(reader);
            var bytes = reader.Bytes;
            var probe = new WorldRenderProbe();
            probe.RegisterIndirectResidency(residency, active: true);
            probe.RegisterIndirectResidency(residency, active: true);
            residency.IndirectTierOverride = SdfIndirectTier.Off;
            residency.BeginFrame();
            Assert.True(residency.Prepare(context));
            probe.RegisterIndirectResidency(residency, active: false);
            Assert.Empty(probe.IndirectResidencies);
            Assert.Same(residency, Assert.Single(probe.IndirectAllocationResidencies));
            Assert.Equal(bytes, residency.Tables.IndirectBytes);
            Assert.Contains("retiring-device=" + bytes.DeviceLocal.ToString(CultureInfo.InvariantCulture), WorldIndirectDiagnosticText.Describe(probe));
            reader.Dispose();
            reader = null;
            Assert.Empty(probe.IndirectAllocationResidencies);
            Assert.Equal("indirect off, 0 byte(s)", WorldIndirectDiagnosticText.Describe(probe));
        } finally { reader?.Dispose(); }
    }

    [Theory]
    [InlineData(SdfIndirectTier.Medium, 20971520UL, 41943040UL, 131072UL)]
    [InlineData(SdfIndirectTier.High, 167772160UL, 83886080UL, 262144UL)]
    public void BudgetIncludesBothLightingGenerationsRetiringCachesAndTheWholeLightFragmentOnce(SdfIndirectTier tier,
        ulong radiance, ulong irradiance, ulong publication) {
        var layout = new SdfIndirectLayout(tier);
        var active = new GpuMemoryBytes(layout.ByteLength + 123, 456);
        var all = active + new GpuMemoryBytes(789, 321);
        var output = WorldIndirectDiagnosticText.DescribeMemory(layout, all, active, lightDepth: 1024, lightFragment: 4096);
        foreach (var (field, value) in new[] { ("radiance", radiance), ("irradiance", irradiance), ("publication", publication), ("receiver-proofs", 8UL),
            ("regions-device", 123UL), ("regions-host", 456UL), ("retiring-device", 789UL), ("retiring-host", 321UL),
            ("light-view", 1024UL), ("light-fragment", 4096UL), ("total", all.DeviceLocal + all.HostVisible + 4096UL) }) {
            Assert.Contains(field + "=" + value.ToString(CultureInfo.InvariantCulture), output);
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
        using var residency = Residency("first");
        var probe = new WorldRenderProbe();
        probe.RegisterIndirectResidency(residency, active: true);
        var expected = WorldIndirectDiagnosticText.Describe(probe);
        var registry = new CommandRegistry(modules: [new WorldLightingCommandModule(new Authority(row.Instance),
            indirectReport: _ => WorldIndirectDiagnosticText.Describe(probe))]);
        var result = registry.Submit("world.lighting");
        Assert.False(result.IsError, result.Output);
        Assert.EndsWith(expected, result.Output);
        Assert.Contains("cache=unallocated", expected);
        Assert.Contains("no renderer", WorldIndirectDiagnosticText.Describe(null));
    }

    [Fact]
    public void LightingKeepsTheRetainedPixelsResidencyAndCensusSeparateFromLiveHostInventory() {
        using var row = HostRow.Build(name: "indirect-retained-lighting");
        using var live = Residency("live");
        using var capturedOwner = Residency("captured-pane");
        var probe = new WorldRenderProbe();
        probe.RegisterIndirectResidency(live, active: true);
        var pick = new SdfIndirectPick(SdfIndirectPickStatus.Resolved, SdfIndirectTier.Medium, 0, 1,
            Vector3.Zero, Vector3.UnitY, .1f, Vector3.UnitY, 1, 23, [], default,
            new SdfIndirectCacheSnapshot(31, SdfIndirectTier.Medium, 7, 8, 64, true, 0, 0, 0, 0,
                4, true, 1, 23, 3, false, default, [], []), new SdfIndirectCensus(2, 3, 4, 5, 6), null) {
            SourcesEnabled = SdfIndirectSources.Emission,
        };
        var registry = new CommandRegistry(modules: [new WorldLightingCommandModule(new Authority(row.Instance),
            indirectReport: _ => WorldIndirectDiagnosticText.Describe(probe, pick, capturedOwner))]);
        var result = registry.Submit("world.lighting");
        Assert.False(result.IsError, result.Output);
        Assert.Contains("indirect live", result.Output);
        Assert.Contains("retained-pixel residency=captured-pane allocation=31 epoch=7 generation=1 stamp=23", result.Output);
        Assert.Contains("sources=0x04", result.Output);
        Assert.EndsWith(WorldIndirectPickText.DescribeCensus(pick), result.Output);
        Assert.DoesNotContain("retained-pixel", WorldIndirectDiagnosticText.Describe(probe, pick));
    }
}
