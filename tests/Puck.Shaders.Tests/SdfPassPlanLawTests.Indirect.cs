using Puck.Abstractions.Gpu;
using Puck.Hosting;

namespace Puck.Shaders.Tests;

public sealed partial class SdfPassPlanLawTests {
    [Fact]
    public void TheIndirectProducerPublishesOneForwardedBufferWithPlacementPartitionAndTraceBarriers() {
        const ulong Bytes = 4096;
        var package = RenderGraphPackageCatalog.Engine.Packages.Single(predicate: item => (item.Id == RenderGraphPackageCatalog.Indirect));
        var fragment = SdfWorldPackage.IndirectFragment(bytes: Bytes);
        var plan = new RenderGraphCompiler(new RenderGraphPackageCatalog(packages: [package with { Fragment = fragment }])).Compile(definition: new RenderGraphDefinition(
            Name: "cache", Schema: RenderGraphSchemas.Graph, Outputs: [SdfWorldPackage.IndirectCache],
            Resources: [new ShaderPipelineResource(Name: SdfWorldPackage.IndirectCache, Kind: ShaderPipelineResourceKind.Buffer, SizeBytes: Bytes, StrideBytes: 4)],
            Packages: [new RenderGraphPackagePass(Name: "indirect", Package: RenderGraphPackageCatalog.Indirect, Outputs: [SdfWorldPackage.IndirectCache])]
        ));

        Assert.Equal(new[] { "indirect$place", "indirect$classify", "indirect$trace" }, plan.Pipeline.PassOrder);
        var storage = Assert.Single(collection: plan.Pipeline.Storages);

        Assert.Equal(Bytes, storage.Declaration.ResolveSizeBytes(counts: new ShaderPipelineStorageCounts(Height: 1, Width: 1)));
        foreach (var pass in plan.Pipeline.Passes.Skip(count: 1)) {
            var access = Assert.Single(collection: pass.Accesses);

            Assert.Equal(ShaderPipelineBarrierKind.Buffer, access.Barrier.Kind);
            Assert.True(condition: ((access.Barrier.SourceAccess & GpuAccess.ShaderWrite) != 0));
            Assert.Equal(GpuAccess.ShaderRead | GpuAccess.ShaderWrite, access.Barrier.DestinationAccess);
            Assert.Equal(GpuStage.ComputeShader, access.Barrier.DestinationStage);
        }
        Assert.All(plan.Pipeline.Passes, pass => Assert.True(condition: pass.Package!.CountsKernelWork));
    }
    [Fact]
    public void AnEnabledViewReadsTheCacheThroughAnExternalBufferEdgeOnlyAtPrimaryAndViews() {
        var fragment = SdfWorldPackage.WithIndirect(SdfWorldPackage.NativeFragment, 4096);

        Assert.Equal([SdfWorldPackage.IndirectCache], fragment.InputVersions);
        var external = Assert.Single(collection: fragment.Resources, predicate: resource => (resource.Name == SdfWorldPackage.IndirectCache));

        Assert.True(condition: external.IsExternal);
        Assert.Equal(ShaderPipelineResourceKind.Buffer, external.Kind);
        Assert.Equal([SdfWorldPackage.Parts.Primary, SdfWorldPackage.Parts.Views],
            fragment.Passes.Where(predicate: pass => pass.Inputs.Any(predicate: input => (input.Name == SdfWorldPackage.IndirectCache))).Select(selector: pass => pass.Name));
    }
}
