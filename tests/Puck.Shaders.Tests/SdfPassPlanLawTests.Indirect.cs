using Puck.Abstractions.Gpu;
using Puck.Hosting;

namespace Puck.Shaders.Tests;

public sealed partial class SdfPassPlanLawTests {
    [Fact]
    public void TheIndirectProducerPublishesOneForwardedBufferWithPlacementPartitionAndTraceBarriers() {
        const ulong Bytes = 4096;
        var package = RenderGraphPackageCatalog.Engine.Packages.Single(item => (item.Id == RenderGraphPackageCatalog.Indirect));
        var fragment = SdfWorldPackage.IndirectFragment(Bytes);
        var plan = new RenderGraphCompiler(new RenderGraphPackageCatalog([package with { Fragment = fragment }])).Compile(new RenderGraphDefinition(
            Name: "cache", Schema: RenderGraphSchemas.Graph, Outputs: [SdfWorldPackage.IndirectCache],
            Resources: [new ShaderPipelineResource(Name: SdfWorldPackage.IndirectCache, Kind: ShaderPipelineResourceKind.Buffer, SizeBytes: Bytes, StrideBytes: 4)],
            Packages: [new RenderGraphPackagePass(Name: "indirect", Package: RenderGraphPackageCatalog.Indirect, Outputs: [SdfWorldPackage.IndirectCache])]
        ));

        Assert.Equal(new[] { "indirect$place", "indirect$classify", "indirect$trace" }, plan.Pipeline.PassOrder);
        var storage = Assert.Single(plan.Pipeline.Storages);

        Assert.Equal(Bytes, storage.Declaration.ResolveSizeBytes(new ShaderPipelineStorageCounts(1, 1)));
        foreach (var pass in plan.Pipeline.Passes.Skip(1)) {
            var access = Assert.Single(pass.Accesses);

            Assert.Equal(ShaderPipelineBarrierKind.Buffer, access.Barrier.Kind);
            Assert.True(((access.Barrier.SourceAccess & GpuAccess.ShaderWrite) != 0));
            Assert.Equal(GpuAccess.ShaderRead | GpuAccess.ShaderWrite, access.Barrier.DestinationAccess);
            Assert.Equal(GpuStage.ComputeShader, access.Barrier.DestinationStage);
        }
        Assert.All(plan.Pipeline.Passes, pass => Assert.True(pass.Package!.CountsKernelWork));
    }
    [Fact]
    public void AnEnabledViewReadsTheCacheThroughAnExternalBufferEdgeOnlyAtPrimaryAndViews() {
        var fragment = SdfWorldPackage.WithIndirect(SdfWorldPackage.NativeFragment, 4096);

        Assert.Equal([SdfWorldPackage.IndirectCache], fragment.InputVersions);
        var external = Assert.Single(fragment.Resources, resource => (resource.Name == SdfWorldPackage.IndirectCache));

        Assert.True(external.IsExternal);
        Assert.Equal(ShaderPipelineResourceKind.Buffer, external.Kind);
        Assert.Equal([SdfWorldPackage.Parts.Primary, SdfWorldPackage.Parts.Views],
            fragment.Passes.Where(pass => pass.Inputs.Any(input => (input.Name == SdfWorldPackage.IndirectCache))).Select(pass => pass.Name));
    }
}
