using System.Buffers.Binary;
using Puck.Hosting;
using Puck.Shaders;
using Puck.SignedDistance;
using Puck.Testing;
using Xunit;

namespace Puck.SdfVm.Tests;

/// <summary>Near's bounded policy and admission words come from the production model and WORLD declaration.</summary>
public sealed class SdfIndirectNearLawTests {
    [Fact]
    public void TheNearAllowanceAndPhasesReachGeneratedShadersWithoutAnotherPolicyTable() {
        Assert.Equal(12, SdfIndirectNearLayout.Steps);
        Assert.Equal(4, SdfIndirectNearLayout.Phases);
        Assert.Equal(0.5f, SdfIndirectNearLayout.Reach);
        var generated = SdfIndirectHlsl.Generate();
        Assert.Contains("static const uint SdfIndirectNearSteps = 12u;", generated, StringComparison.Ordinal);
        Assert.Contains("static const uint SdfIndirectNearPhases = 4u;", generated, StringComparison.Ordinal);
        Assert.Contains("static const float SdfIndirectNearReach = 0.5;", generated, StringComparison.Ordinal);
    }

    [Fact]
    public void OnlyWorldPassesCarryNearAdmissionAndItsExactPredecessor() {
        var names = SdfWorldPackage.WorldIndirectValues.Select(member => member.Name).ToArray();
        Assert.Contains(SdfWorldPackage.IndirectNearEnabled, names);
        Assert.Contains(SdfWorldPackage.IndirectPreviousPublication, names);
        var solveNames = SdfWorldPackage.IndirectMembers.Select(member => member.Name).ToArray();
        Assert.DoesNotContain(SdfWorldPackage.IndirectNearEnabled, solveNames);
        Assert.DoesNotContain(SdfWorldPackage.IndirectPreviousPublication, solveNames);
        var parameters = SdfWorldInterfaces.WorldParameters;
        var enable = parameters.BlockOffsetOf(SdfWorldPackage.IndirectNearEnabled);
        var previous = parameters.BlockOffsetOf(SdfWorldPackage.IndirectPreviousPublication);
        Assert.NotEqual(enable, previous);
        Assert.True(enable + sizeof(uint) <= parameters.SizeBytes);
        Assert.True(previous + sizeof(uint) <= parameters.SizeBytes);
    }

    [Fact]
    public void AnUnboundCacheClearsBothNearWordsInsteadOfRetainingPriorAdmission() {
        var parameters = SdfWorldInterfaces.WorldParameters;
        var block = new byte[parameters.SizeBytes];
        Array.Fill(block, (byte)255);
        SdfFrameBlock.WriteIndirect(block, null);
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(block.AsSpan((int)parameters.BlockOffsetOf(SdfWorldPackage.IndirectNearEnabled))));
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(block.AsSpan((int)parameters.BlockOffsetOf(SdfWorldPackage.IndirectPreviousPublication))));
    }
}

public sealed partial class SdfWorldPassesLawTests {
    [Theory]
    [InlineData(SdfIndirectTier.High, "ready")]
    [InlineData(SdfIndirectTier.High, "unfenced")]
    [InlineData(SdfIndirectTier.High, "changed-source")]
    [InlineData(SdfIndirectTier.High, "frozen")]
    [InlineData(SdfIndirectTier.Medium, "ready")]
    [InlineData(SdfIndirectTier.Off, "ready")]
    public void NearReadsOnlyTheCurrentHighSourceAfterItsWholeSolveAndFence(SdfIndirectTier tier, string state) {
        var source = Frame() with { FarDistance = 12f, IndirectTier = tier, EnableCadenceGate = true };
        var block = new byte[SdfWorldInterfaces.WorldParameters.SizeBytes];
        uint Word(string name) => BinaryPrimitives.ReadUInt32LittleEndian(block.AsSpan(
            (int)SdfWorldInterfaces.WorldParameters.BlockOffsetOf(name)));
        if (tier == SdfIndirectTier.Off) {
            SdfFrameBlock.WriteIndirectNear(block, null, source, ready: true);
            Assert.Equal(0u, Word(SdfWorldPackage.IndirectNearEnabled));
            Assert.Equal(0u, Word(SdfWorldPackage.IndirectPreviousPublication));
            return;
        }
        var gpu = new FakeGpuDevice(holdFences: true);
        var pipelines = SdfTestPipelines.Cache();
        using var residency = new SdfWorldResidency(brickPoolVoxelCapacity: 0,
            frameSource: new CapturingFrameSource(() => source), height: Extent, kernels: SdfTestPipelines.Kernels(),
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
                Output: ShaderPipelineResourceKind.Buffer, Passes: indirect.FragmentOf(residency.IndirectInstanceName)!.Passes.Count,
                Reads: [], Refresh: RenderGraphRefresh.EveryFrame),
        ], out var set, out var setRefusal), setRefusal?.Message);
        Assert.True(RenderGraphRuntime.TryCreate(set, new RenderGraphRuntimeGraph?[2], "world", packages,
            pipelines.Pipelines, gpu, false, out var runtime, out var refusal), refusal?.Message);
        using var graph = runtime;
        var frame = 0L;
        var priorStamp = 0u;
        SdfIndirectLightingSnapshot? priorSource = null;
        void Produce() {
            foreach (var fence in gpu.SubmittedFences) { fence.Completed = true; }
            var scheduled = new RenderGraphFrame(DisplayHeight: (int)Extent, DisplayHertz: 60, DisplayWidth: (int)Extent,
                Footprints: [], Index: frame, Roots: [new(Height: 1, Instance: "world", Width: 1)], Tick: frame++);
            _ = graph.ProduceFrame(frame: scheduled, context: context);
            graph.Node(0).PollReadbacks();
            if (residency.Tables?.Indirect is { PublishedStamp: > 0 } published && published.PublishedStamp != priorStamp) {
                // A completed whole sweep, rather than an in-flight batch or another source, owns the predecessor.
                Assert.Equal(ReferenceEquals(priorSource, published.PublishedLightingSource) ? priorStamp : 0u,
                    published.PreviousPublishedStamp);
                priorStamp = published.PublishedStamp;
                priorSource = published.PublishedLightingSource;
            }
        }
        TestLiveness.Within(frames: 128, step: () => {
            Produce();
            return residency.IsIndirectReady && graph.Render.Completion == FrameCompletion.Rendered;
        }, building: () => graph.Node(0).IsBuildingCandidate || graph.Node(1).IsBuildingCandidate,
            reason: () => graph.Render.Reason);
        var cache = residency.Tables!.Indirect!;
        Assert.True(cache.LightingComplete);
        Assert.NotNull(cache.PublishedLightingSource);
        Assert.Same(cache.LightingSource, cache.PublishedLightingSource);
        Assert.NotEqual(0u, cache.PreviousPublishedStamp);
        Assert.NotEqual(cache.PublishedStamp, cache.PreviousPublishedStamp);
        if (state == "changed-source") { source = source with { IndirectSources = SdfIndirectSources.Direct }; }
        if (state == "frozen") { cache.Frozen = true; }
        SdfFrameBlock.WriteIndirectNear(block, cache, source, ready: state != "unfenced");
        var enabled = tier == SdfIndirectTier.High && state == "ready";
        Assert.Equal(enabled ? 1u : 0u, Word(SdfWorldPackage.IndirectNearEnabled));
        Assert.Equal(enabled ? cache.PreviousPublishedStamp : 0u, Word(SdfWorldPackage.IndirectPreviousPublication));
    }
}
