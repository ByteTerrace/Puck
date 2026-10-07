using System.Buffers.Binary;
using System.Numerics;
using Puck.Hosting;
using Puck.Shaders;
using Puck.SignedDistance;
using Puck.Testing;
using Xunit;

namespace Puck.SdfVm.Tests;

public sealed partial class SdfWorldPassesLawTests {
    [InlineData(SdfIndirectTier.Medium, false)]
    [InlineData(SdfIndirectTier.High, true)]
    [InlineData(SdfIndirectTier.High, false)]
    [Theory]
    public void IndirectInspectionWaitsForItsFenceAndKeepsItsEpochSourcesAndActualCensus(SdfIndirectTier tier, bool samePredecessor) {
        var gpu = new FakeGpuDevice(holdFences: true);
        var pipelines = SdfTestPipelines.Cache();
        var source = Frame() with { FarDistance = 12f, IndirectTier = tier, IndirectApply = new(.25f, Vector3.UnitX, .5f) };
        using var residency = new SdfWorldResidency(brickPoolVoxelCapacity: 0,
            frameSource: new FixedFrameSource(frame: source), height: Extent, kernels: SdfTestPipelines.Kernels(),
            name: "world", pipelines: pipelines, width: Extent);
        var views = new SdfWorldPasses(_ => new SdfWorldView(Residency: residency, View: 0));
        using var indirect = new SdfIndirectPasses(views: views);

        indirect.Register(name: residency.IndirectInstanceName, residency: residency);
        var packages = new RenderGraphPackageRecorders(regionCopy: pipelines.RegionCopy);

        packages.Register(factory: views, package: RenderGraphPackageCatalog.SdfWorld);
        packages.Register(factory: indirect, package: RenderGraphPackageCatalog.Indirect);
        var context = ContextOf(gpu: gpu);

        residency.ProduceFirstFrame(context: context);
        Assert.True(condition: RenderGraphInstanceSet.TryCreate([
            new RenderGraphInstance(Name: "world", ExternalPackage: RenderGraphPackageCatalog.SdfWorld,
                Passes: SdfWorldPackage.NativeFragment.Passes.Count,
                Reads: [new(residency.IndirectInstanceName, Kind: ShaderPipelineResourceKind.Buffer)], Refresh: RenderGraphRefresh.EveryFrame),
            new RenderGraphInstance(Name: residency.IndirectInstanceName, ExternalPackage: RenderGraphPackageCatalog.Indirect,
                Output: ShaderPipelineResourceKind.Buffer, Passes: indirect.FragmentOf(instance: residency.IndirectInstanceName)!.Passes.Count, Reads: [], Refresh: RenderGraphRefresh.EveryFrame),
        ], out var set, out var setRefusal), userMessage: setRefusal?.Message);
        Assert.True(condition: RenderGraphRuntime.TryCreate(set, new RenderGraphRuntimeGraph?[2], "world", packages,
            pipelines.Pipelines, gpu, false, out var runtime, out var refusal), userMessage: refusal?.Message);
        using var graph = runtime;
        var frame = 0L;

        void Produce() {
            var scheduled = new RenderGraphFrame(DisplayHeight: ((int)Extent), DisplayHertz: 60, DisplayWidth: ((int)Extent),
                Footprints: [], Index: frame, Roots: [new(Height: 1, Instance: "world", Width: 1)], Tick: frame++);

            _ = graph.ProduceFrame(context: context, frame: scheduled);
        }
        TestLiveness.Within(frames: IndirectCompletionFrames(cache: residency.Tables!.Indirect!, source: source), step: () => {
            Produce();
            return ((residency.Tables?.Indirect is { LightingComplete: true }) && (graph.Render.Completion == FrameCompletion.Rendered));
        }, building: () => (graph.Node(instance: 0).IsBuildingCandidate || graph.Node(instance: 1).IsBuildingCandidate), reason: () => graph.Render.Reason);
        // Warm the independently counted receiver-completion ring before measuring this human pick's new buffers.
        for (var warm = 0; (warm <= SdfWorldTables.FrameRingSize); warm++) { Produce(); }
        var cache = residency.Tables!.Indirect!;
        var picker = views.PickerOf(instance: "world");
        var before = graph.Node(instance: 0).OwnedBytes;
        var steady = graph.Node(instance: 0).InstalledAccount.SteadyBytes;
        var request = picker.Request(0.5f, 0.5f, surface: true);

        Produce();
        var fence = Assert.IsType<FakeGpuDevice.Fence>(@object: gpu.LastSubmittedFence);
        var captured = cache.Snapshot();
        var lighting = cache.PublishedLightingSource;
        var previousStamp = cache.PreviousPublishedStamp;
        var probeBytes = ((tier == SdfIndirectTier.High) ? 524288 : 262144);

        Assert.NotNull(@object: lighting);
        Assert.NotEqual(actual: previousStamp, expected: 0u);
        Assert.NotEqual(captured.PublishedStamp, previousStamp);
        Assert.Equal((((before + 64UL) + 288UL) + ((ulong)probeBytes)), graph.Node(instance: 0).OwnedBytes);
        Assert.Equal((((steady + 64UL) + 288UL) + ((ulong)probeBytes)), graph.Node(instance: 0).InstalledAccount.SteadyBytes);
        gpu.WriteReadback = (_, bytes) => {
            if (bytes.Length == 64) {
                BinaryPrimitives.WriteSingleLittleEndian(destination: bytes, value: 2f);
                BinaryPrimitives.WriteUInt32LittleEndian(destination: bytes[4..], value: SdfVisibility.IdentityOf(kind: SdfVisibilityKind.Sdf, source: 1));
                BinaryPrimitives.WriteInt32LittleEndian(destination: bytes[28..], value: SdfProgram.NoDynamicTransformSlot);
                WriteBox(box: WholeBox, bytes: bytes);
            } else if (bytes.Length == 288) {
                BinaryPrimitives.WriteUInt32LittleEndian(destination: bytes, value: 3u);
                BinaryPrimitives.WriteUInt32LittleEndian(destination: bytes[4..], value: ((uint)tier));
                BinaryPrimitives.WriteUInt32LittleEndian(destination: bytes[12..], value: 3u);
                BinaryPrimitives.WriteSingleLittleEndian(destination: bytes[16..], value: 7f);
                BinaryPrimitives.WriteSingleLittleEndian(destination: bytes[28..], value: 0.125f);
                BinaryPrimitives.WriteUInt32LittleEndian(destination: bytes[44..], value: ((uint)captured.PublishedGeneration));
                BinaryPrimitives.WriteUInt32LittleEndian(destination: bytes[60..], value: captured.PublishedStamp);
                BinaryPrimitives.WriteSingleLittleEndian(destination: bytes[72..], value: 0.25f);
                BinaryPrimitives.WriteSingleLittleEndian(destination: bytes[88..], value: 0.75f);
                BinaryPrimitives.WriteSingleLittleEndian(destination: bytes[192..], value: 0.125f);
                BinaryPrimitives.WriteSingleLittleEndian(destination: bytes[212..], value: 0.25f);
                BinaryPrimitives.WriteSingleLittleEndian(destination: bytes[232..], value: 0.5f);
                BinaryPrimitives.WriteSingleLittleEndian(destination: bytes[240..], value: 1f);
                BinaryPrimitives.WriteSingleLittleEndian(destination: bytes[260..], value: 2f);
                BinaryPrimitives.WriteUInt32LittleEndian(destination: bytes[236..], value: 3u);
                BinaryPrimitives.WriteUInt32LittleEndian(destination: bytes[204..], value: ((tier == SdfIndirectTier.High) ? 0u : 2u));
                BinaryPrimitives.WriteSingleLittleEndian(destination: bytes[280..], value: 1f);
                BinaryPrimitives.WriteUInt32LittleEndian(destination: bytes[284..], value: (samePredecessor ? previousStamp : (previousStamp + 777u)));
                BinaryPrimitives.WriteUInt32LittleEndian(destination: bytes[220..], value: ((uint)(SdfIndirectSources.Direct | SdfIndirectSources.Sky)));
            } else if (bytes.Length == probeBytes) {
                foreach (var brick in captured.Bricks) {
                    for (var probe = 0; (probe < 64); probe++) {
                        var index = ((brick.Slot * 64) + probe);

                        BinaryPrimitives.WriteUInt32LittleEndian(destination: bytes[((index * 16) + 12)..], value: (captured.Epoch << 8) | (((uint)probe) & 3u));
                    }
                }
                BinaryPrimitives.WriteUInt32LittleEndian(destination: bytes[(((captured.Bricks[0].Slot * 64) * 16) + 12)..], value: 0u);
            }
        };
        residency.RequestIndirectReset();
        Produce();
        Assert.Null(value: picker.Result);
        Assert.NotEqual(captured.Epoch, cache.Epoch);
        fence.Completed = true;
        Produce();
        var result = Assert.IsType<SdfIndirectPick>(@object: picker.Result!.Value.Indirect);

        Assert.Equal(request, picker.Result.Value.Request);
        Assert.Equal(captured.Epoch, result.Cache!.Epoch);
        Assert.Same(lighting, result.LightingSource);
        Assert.Equal(SdfIndirectPickStatus.Resolved, result.Status);
        Assert.Equal(((tier == SdfIndirectTier.High) ? SdfIndirectMethod.Cache : SdfIndirectMethod.Cone), result.Method);
        Assert.Equal(Vector3.UnitZ, result.NearDirection);
        Assert.Equal((samePredecessor ? previousStamp : (previousStamp + 777u)), result.NearPreviousPublication);
        if ((tier == SdfIndirectTier.High) && samePredecessor) { Assert.Same(lighting, result.NearSource); } else { Assert.Null(@object: result.NearSource); }
        Assert.Equal(SdfIndirectNearOutcome.Continuation, result.Near);
        Assert.Equal(SdfIndirectSources.Direct | SdfIndirectSources.Sky, result.SourcesEnabled);
        Assert.Equal(new SdfIndirectApplication(.25f, Vector3.UnitX, .5f), result.Application);
        Assert.Equal(new Vector3(x: 7, y: 0, z: 0), result.Position);
        Assert.Equal(0.125f, result.Clearance);
        Assert.Equal(new[] { 0.25f, 0.75f, 0, 0, 0, 0, 0, 0 }, result.Corners.Select(selector: static corner => corner.Weight));
        Assert.Equal(new SdfIndirectPickSources(new(x: 0.125f, y: 0, z: 0), new(x: 0, y: 0.25f, z: 0), new(x: 0, y: 0, z: 0.5f), new(x: 1, y: 0, z: 0), new(x: 0, y: 2, z: 0)), result.Sources);
        var each = (captured.Bricks.Count * 16);

        Assert.Equal(new SdfIndirectCensus(Active: each, Dormant: (each - 1), Inactive: each, Relocated: each, Unpublished: 1), result.Census);
    }
}
