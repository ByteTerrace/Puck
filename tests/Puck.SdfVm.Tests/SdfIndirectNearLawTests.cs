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
        Assert.Equal(actual: SdfIndirectNearLayout.Steps, expected: 12);
        Assert.Equal(actual: SdfIndirectNearLayout.Phases, expected: 4);
        Assert.Equal(actual: SdfIndirectNearLayout.Reach, expected: 0.5f);
        var generated = SdfIndirectHlsl.Generate();

        Assert.Contains(actualString: generated, comparisonType: StringComparison.Ordinal, expectedSubstring: "static const uint SdfIndirectNearSteps = 12u;");
        Assert.Contains(actualString: generated, comparisonType: StringComparison.Ordinal, expectedSubstring: "static const uint SdfIndirectNearPhases = 4u;");
        Assert.Contains(actualString: generated, comparisonType: StringComparison.Ordinal, expectedSubstring: "static const float SdfIndirectNearReach = 0.5;");
    }
    [Fact]
    public void OnlyWorldPassesCarryNearAdmissionAndItsExactPredecessor() {
        var names = SdfWorldPackage.WorldIndirectValues.Select(selector: member => member.Name).ToArray();

        Assert.Contains(collection: names, expected: SdfWorldPackage.IndirectNearEnabled);
        Assert.Contains(collection: names, expected: SdfWorldPackage.IndirectPreviousPublication);
        var solveNames = SdfWorldPackage.IndirectMembers.Select(selector: member => member.Name).ToArray();

        Assert.DoesNotContain(collection: solveNames, expected: SdfWorldPackage.IndirectNearEnabled);
        Assert.DoesNotContain(collection: solveNames, expected: SdfWorldPackage.IndirectPreviousPublication);
        var parameters = SdfWorldInterfaces.WorldParameters;
        var enable = parameters.BlockOffsetOf(member: SdfWorldPackage.IndirectNearEnabled);
        var previous = parameters.BlockOffsetOf(member: SdfWorldPackage.IndirectPreviousPublication);

        Assert.NotEqual(actual: previous, expected: enable);
        Assert.True(condition: ((enable + sizeof(uint)) <= parameters.SizeBytes));
        Assert.True(condition: ((previous + sizeof(uint)) <= parameters.SizeBytes));
    }
    [Fact]
    public void AnUnboundCacheClearsBothNearWordsInsteadOfRetainingPriorAdmission() {
        var parameters = SdfWorldInterfaces.WorldParameters;
        var block = new byte[parameters.SizeBytes];

        Array.Fill(array: block, value: ((byte)255));
        SdfFrameBlock.WriteIndirect(block: block, cache: null);
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(source: block.AsSpan(start: ((int)parameters.BlockOffsetOf(member: SdfWorldPackage.IndirectNearEnabled)))));
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(source: block.AsSpan(start: ((int)parameters.BlockOffsetOf(member: SdfWorldPackage.IndirectPreviousPublication)))));
    }
}
public sealed partial class SdfWorldPassesLawTests {
    [InlineData(SdfIndirectTier.High, "ready")]
    [InlineData(SdfIndirectTier.High, "unfenced")]
    [InlineData(SdfIndirectTier.High, "changed-source")]
    [InlineData(SdfIndirectTier.High, "frozen")]
    [InlineData(SdfIndirectTier.Medium, "ready")]
    [InlineData(SdfIndirectTier.Off, "ready")]
    [Theory]
    public void NearReadsOnlyTheCurrentHighSourceAfterItsWholeSolveAndFence(SdfIndirectTier tier, string state) {
        var source = Frame() with { FarDistance = 12f, IndirectTier = tier, EnableCadenceGate = true };
        var block = new byte[SdfWorldInterfaces.WorldParameters.SizeBytes];

        uint Word(string name) => BinaryPrimitives.ReadUInt32LittleEndian(source: block.AsSpan(
            start: ((int)SdfWorldInterfaces.WorldParameters.BlockOffsetOf(member: name))));
        if (tier == SdfIndirectTier.Off) {
            SdfFrameBlock.WriteIndirectNear(block, null, source, ready: true);
            Assert.Equal(0u, Word(name: SdfWorldPackage.IndirectNearEnabled));
            Assert.Equal(0u, Word(name: SdfWorldPackage.IndirectPreviousPublication));
            return;
        }
        var gpu = new FakeGpuDevice(holdFences: true);
        var pipelines = SdfTestPipelines.Cache();
        using var residency = new SdfWorldResidency(brickPoolVoxelCapacity: 0,
            frameSource: new CapturingFrameSource(capture: () => source), height: Extent, kernels: SdfTestPipelines.Kernels(),
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
                Output: ShaderPipelineResourceKind.Buffer, Passes: indirect.FragmentOf(instance: residency.IndirectInstanceName)!.Passes.Count,
                Reads: [], Refresh: RenderGraphRefresh.EveryFrame),
        ], out var set, out var setRefusal), userMessage: setRefusal?.Message);
        Assert.True(condition: RenderGraphRuntime.TryCreate(set, new RenderGraphRuntimeGraph?[2], "world", packages,
            pipelines.Pipelines, gpu, false, out var runtime, out var refusal), userMessage: refusal?.Message);
        using var graph = runtime;
        var frame = 0L;
        var priorStamp = 0u;
        SdfIndirectLightingSnapshot? priorSource = null;

        void Produce() {
            foreach (var fence in gpu.SubmittedFences) { fence.Completed = true; }
            var scheduled = new RenderGraphFrame(DisplayHeight: ((int)Extent), DisplayHertz: 60, DisplayWidth: ((int)Extent),
                Footprints: [], Index: frame, Roots: [new(Height: 1, Instance: "world", Width: 1)], Tick: frame++);

            _ = graph.ProduceFrame(context: context, frame: scheduled);
            graph.Node(instance: 0).PollReadbacks();
            if ((residency.Tables?.Indirect is { PublishedStamp: > 0 } published) && (published.PublishedStamp != priorStamp)) {
                // A completed whole sweep, rather than an in-flight batch or another source, owns the predecessor.
                Assert.Equal((ReferenceEquals(objA: priorSource, objB: published.PublishedLightingSource) ? priorStamp : 0u),
                    published.PreviousPublishedStamp);
                priorStamp = published.PublishedStamp;
                priorSource = published.PublishedLightingSource;
            }
        }
        TestLiveness.Within(frames: 128, step: () => {
            Produce();
            return (residency.IsIndirectReady && (graph.Render.Completion == FrameCompletion.Rendered));
        }, building: () => (graph.Node(instance: 0).IsBuildingCandidate || graph.Node(instance: 1).IsBuildingCandidate),
            reason: () => graph.Render.Reason);
        var cache = residency.Tables!.Indirect!;

        Assert.True(condition: cache.LightingComplete);
        Assert.NotNull(@object: cache.PublishedLightingSource);
        Assert.Same(cache.LightingSource, cache.PublishedLightingSource);
        Assert.NotEqual(0u, cache.PreviousPublishedStamp);
        Assert.NotEqual(cache.PublishedStamp, cache.PreviousPublishedStamp);
        if (state == "changed-source") { source = source with { IndirectSources = SdfIndirectSources.Direct }; }
        if (state == "frozen") { cache.Frozen = true; }
        SdfFrameBlock.WriteIndirectNear(block, cache, source, ready: (state != "unfenced"));
        var enabled = ((tier == SdfIndirectTier.High) && (state == "ready"));

        Assert.Equal((enabled ? 1u : 0u), Word(name: SdfWorldPackage.IndirectNearEnabled));
        Assert.Equal((enabled ? cache.PreviousPublishedStamp : 0u), Word(name: SdfWorldPackage.IndirectPreviousPublication));
    }
}
