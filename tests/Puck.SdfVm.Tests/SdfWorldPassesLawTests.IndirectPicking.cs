using System.Buffers.Binary;
using System.Numerics;
using Puck.Hosting;
using Puck.Shaders;
using Puck.SignedDistance;
using Puck.SignedDistance.Illumination;
using Puck.Testing;
using Xunit;

namespace Puck.SdfVm.Tests;

public sealed partial class SdfWorldPassesLawTests {
    [Theory]
    [InlineData(SdfIndirectTier.Medium, false)]
    [InlineData(SdfIndirectTier.High, true)]
    [InlineData(SdfIndirectTier.High, false)]
    public void IndirectInspectionWaitsForItsFenceAndKeepsItsEpochSourcesAndActualCensus(SdfIndirectTier tier, bool samePredecessor) {
        var gpu = new FakeGpuDevice(holdFences: true);
        var pipelines = SdfTestPipelines.Cache();
        var source = Frame() with { FarDistance = 12f, IndirectTier = tier };
        using var residency = new SdfWorldResidency(brickPoolVoxelCapacity: 0,
            frameSource: new FixedFrameSource(frame: source), height: Extent, kernels: SdfTestPipelines.Kernels(),
            name: "world", pipelines: pipelines, width: Extent);
        var views = new SdfWorldPasses(_ => new SdfWorldView(residency, 0));
        using var indirect = new SdfIndirectPasses(views);
        indirect.Register(residency.IndirectInstanceName, residency);
        var packages = new RenderGraphPackageRecorders(regionCopy: pipelines.RegionCopy);
        packages.Register(package: RenderGraphPackageCatalog.SdfWorld, factory: views);
        packages.Register(package: RenderGraphPackageCatalog.Indirect, factory: indirect);
        var context = ContextOf(gpu);
        residency.ProduceFirstFrame(context);
        Assert.True(RenderGraphInstanceSet.TryCreate([
            new RenderGraphInstance(Name: "world", ExternalPackage: RenderGraphPackageCatalog.SdfWorld,
                Passes: SdfWorldPackage.NativeFragment.Passes.Count,
                Reads: [new(residency.IndirectInstanceName, Kind: ShaderPipelineResourceKind.Buffer)], Refresh: RenderGraphRefresh.EveryFrame),
            new RenderGraphInstance(Name: residency.IndirectInstanceName, ExternalPackage: RenderGraphPackageCatalog.Indirect,
                Output: ShaderPipelineResourceKind.Buffer, Passes: indirect.FragmentOf(residency.IndirectInstanceName)!.Passes.Count, Reads: [], Refresh: RenderGraphRefresh.EveryFrame),
        ], out var set, out var setRefusal), setRefusal?.Message);
        Assert.True(RenderGraphRuntime.TryCreate(set, new RenderGraphRuntimeGraph?[2], "world", packages,
            pipelines.Pipelines, gpu, false, out var runtime, out var refusal), refusal?.Message);
        using var graph = runtime;
        var frame = 0L;
        void Produce() {
            var scheduled = new RenderGraphFrame(DisplayHeight: (int)Extent, DisplayHertz: 60, DisplayWidth: (int)Extent,
                Footprints: [], Index: frame, Roots: [new(Height: 1, Instance: "world", Width: 1)], Tick: frame++);
            _ = graph.ProduceFrame(frame: scheduled, context: context);
        }
        TestLiveness.Within(frames: 64, step: () => {
            Produce();
            return residency.Tables?.Indirect is { LightingComplete: true } && graph.Render.Completion == FrameCompletion.Rendered;
        }, building: () => graph.Node(0).IsBuildingCandidate || graph.Node(1).IsBuildingCandidate, reason: () => graph.Render.Reason);
        // Warm the independently counted receiver-completion ring before measuring this human pick's new buffers.
        for (var warm = 0; warm <= SdfWorldTables.FrameRingSize; warm++) { Produce(); }
        var cache = residency.Tables!.Indirect!;
        var picker = views.PickerOf("world");
        var before = graph.Node(0).OwnedBytes;
        var steady = graph.Node(0).InstalledAccount.SteadyBytes;
        var request = picker.Request(0.5f, 0.5f, surface: true);
        Produce();
        var fence = Assert.IsType<FakeGpuDevice.Fence>(gpu.LastSubmittedFence);
        var captured = cache.Snapshot();
        var lighting = cache.PublishedLightingSource;
        var previousStamp = cache.PreviousPublishedStamp;
        var probeBytes = tier == SdfIndirectTier.High ? 524288 : 262144;
        Assert.NotNull(lighting);
        Assert.NotEqual(0u, previousStamp);
        Assert.NotEqual(captured.PublishedStamp, previousStamp);
        Assert.Equal(before + 64UL + 288UL + (ulong)probeBytes, graph.Node(0).OwnedBytes);
        Assert.Equal(steady + 64UL + 288UL + (ulong)probeBytes, graph.Node(0).InstalledAccount.SteadyBytes);
        gpu.WriteReadback = (_, bytes) => {
            if (bytes.Length == 64) {
                BinaryPrimitives.WriteSingleLittleEndian(bytes, 2f);
                BinaryPrimitives.WriteUInt32LittleEndian(bytes[4..], SdfVisibility.IdentityOf(SdfVisibilityKind.Sdf, 1));
                BinaryPrimitives.WriteInt32LittleEndian(bytes[28..], SdfProgram.NoDynamicTransformSlot);
                WriteBox(box: WholeBox, bytes: bytes);
            } else if (bytes.Length == 288) {
                BinaryPrimitives.WriteUInt32LittleEndian(bytes, 3u);
                BinaryPrimitives.WriteUInt32LittleEndian(bytes[4..], (uint)tier);
                BinaryPrimitives.WriteUInt32LittleEndian(bytes[12..], 3u);
                BinaryPrimitives.WriteSingleLittleEndian(bytes[16..], 7f);
                BinaryPrimitives.WriteSingleLittleEndian(bytes[28..], 0.125f);
                BinaryPrimitives.WriteUInt32LittleEndian(bytes[44..], (uint)captured.PublishedGeneration);
                BinaryPrimitives.WriteUInt32LittleEndian(bytes[60..], captured.PublishedStamp);
                BinaryPrimitives.WriteSingleLittleEndian(bytes[72..], 0.25f);
                BinaryPrimitives.WriteSingleLittleEndian(bytes[88..], 0.75f);
                BinaryPrimitives.WriteSingleLittleEndian(bytes[192..], 0.125f);
                BinaryPrimitives.WriteSingleLittleEndian(bytes[212..], 0.25f);
                BinaryPrimitives.WriteSingleLittleEndian(bytes[232..], 0.5f);
                BinaryPrimitives.WriteSingleLittleEndian(bytes[240..], 1f);
                BinaryPrimitives.WriteSingleLittleEndian(bytes[260..], 2f);
                BinaryPrimitives.WriteUInt32LittleEndian(bytes[236..], 3u);
                BinaryPrimitives.WriteUInt32LittleEndian(bytes[204..], tier == SdfIndirectTier.High ? 0u : 2u);
                BinaryPrimitives.WriteSingleLittleEndian(bytes[280..], 1f);
                BinaryPrimitives.WriteUInt32LittleEndian(bytes[284..], samePredecessor ? previousStamp : previousStamp + 777u);
                BinaryPrimitives.WriteUInt32LittleEndian(bytes[220..], (uint)(SdfIndirectSources.Direct | SdfIndirectSources.Sky));
            } else if (bytes.Length == probeBytes) {
                foreach (var brick in captured.Bricks) {
                    for (var probe = 0; probe < 64; probe++) {
                        var index = brick.Slot * 64 + probe;
                        BinaryPrimitives.WriteUInt32LittleEndian(bytes[(index * 16 + 12)..], (captured.Epoch << 8) | ((uint)probe & 3u));
                    }
                }
                BinaryPrimitives.WriteUInt32LittleEndian(bytes[(captured.Bricks[0].Slot * 64 * 16 + 12)..], 0u);
            }
        };
        residency.RequestIndirectReset();
        Produce();
        Assert.Null(picker.Result);
        Assert.NotEqual(captured.Epoch, cache.Epoch);
        fence.Completed = true;
        Produce();
        var result = Assert.IsType<SdfIndirectPick>(picker.Result!.Value.Indirect);
        Assert.Equal(request, picker.Result.Value.Request);
        Assert.Equal(captured.Epoch, result.Cache!.Epoch);
        Assert.Same(lighting, result.LightingSource);
        Assert.Equal(SdfIndirectPickStatus.Resolved, result.Status);
        Assert.Equal(tier == SdfIndirectTier.High ? SdfIndirectMethod.Cache : SdfIndirectMethod.Cone, result.Method);
        Assert.Equal(Vector3.UnitZ, result.NearDirection);
        Assert.Equal(samePredecessor ? previousStamp : previousStamp + 777u, result.NearPreviousPublication);
        if (tier == SdfIndirectTier.High && samePredecessor) { Assert.Same(lighting, result.NearSource); }
        else { Assert.Null(result.NearSource); }
        Assert.Equal(SdfIndirectNearOutcome.Continuation, result.Near);
        Assert.Equal(SdfIndirectSources.Direct | SdfIndirectSources.Sky, result.SourcesEnabled);
        Assert.Equal(new Vector3(7, 0, 0), result.Position);
        Assert.Equal(0.125f, result.Clearance);
        Assert.Equal(new[] { 0.25f, 0.75f, 0, 0, 0, 0, 0, 0 }, result.Corners.Select(static corner => corner.Weight));
        Assert.Equal(new SdfIndirectPickSources(new(0.125f, 0, 0), new(0, 0.25f, 0), new(0, 0, 0.5f), new(1, 0, 0), new(0, 2, 0)), result.Sources);
        var each = captured.Bricks.Count * 16;
        Assert.Equal(new SdfIndirectCensus(each, each, each, each - 1, 1), result.Census);
    }
}
