using Puck.Hosting;
using Puck.Shaders;
using Xunit;

namespace Puck.SdfVm.Tests;

/// <summary>The shared producer keeps independent images current and breaks world-image feedback through the existing
/// previous-frame edge, including postprocessing chains.</summary>
public sealed class SdfSkyEnvironmentGraphLawTests {
    [Fact]
    public void WorldDerivedPanoramasUseFeedbackAndIndependentFeedsStayCurrent() {
        Assert.True(condition: RenderGraphInstanceSet.TryCreate(instances: [
            new(Name: "source", Refresh: RenderGraphRefresh.EveryFrame, Passes: 1, Reads: []),
            new(Name: "camera", Refresh: RenderGraphRefresh.EveryFrame, Passes: 1, Reads: [], ExternalPackage: RenderGraphPackageCatalog.SdfWorld),
            new(Name: "post", Refresh: RenderGraphRefresh.EveryFrame, Passes: 1, Reads: [new(Producer: "camera")]),
            new(Name: "view", Refresh: RenderGraphRefresh.EveryFrame, Passes: 1,
                Reads: [new(Producer: "source"), new(Producer: "camera"), new(Producer: "post"), new(Producer: "view", PreviousFrame: true)],
                ExternalPackage: RenderGraphPackageCatalog.SdfWorld),
        ], set: out var set, refusal: out var refusal), userMessage: refusal?.Message);
        var reads = SdfSkyEnvironmentGraph.ReadsOf(set: set, view: "view");

        Assert.Equal(expected: new[] { ("source", false), ("camera", true), ("post", true), ("view", true) },
            actual: reads.Select(static read => (read.Producer, read.PreviousFrame)));
        var instances = set.Instances.Select(static instance => instance.ExternalPackage == RenderGraphPackageCatalog.SdfWorld
            ? instance with { Reads = [.. instance.Reads, new(Producer: "environment", Kind: ShaderPipelineResourceKind.Buffer)] }
            : instance).ToList();
        instances.Add(new(Name: "environment", Refresh: RenderGraphRefresh.EveryFrame, Passes: 2, Reads: reads,
            Output: ShaderPipelineResourceKind.Buffer, ExternalPackage: RenderGraphPackageCatalog.SkyEnvironment));
        Assert.True(condition: RenderGraphInstanceSet.TryCreate(instances: instances, set: out _, refusal: out refusal), userMessage: refusal?.Message);
    }
}
