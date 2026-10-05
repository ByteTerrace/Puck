using Puck.Abstractions.Gpu;
using Puck.Hosting;

namespace Puck.Shaders.Tests;

public sealed partial class SdfPassPlanLawTests {
    [Fact]
    public void TheIndirectProducerPublishesOneForwardedBufferWithPlacementPartitionTraceAndShadeBarriers() {
        const ulong Bytes = 4096;
        var package = RenderGraphPackageCatalog.Engine.Packages.Single(predicate: item => (item.Id == RenderGraphPackageCatalog.Indirect));
        var fragment = SdfWorldPackage.IndirectFragment(bytes: Bytes);
        var plan = new RenderGraphCompiler(new RenderGraphPackageCatalog(packages: [package with { Fragment = fragment }])).Compile(definition: new RenderGraphDefinition(
            Name: "cache", Schema: RenderGraphSchemas.Graph, Outputs: [SdfWorldPackage.IndirectCache],
            Resources: [new ShaderPipelineResource(Name: SdfWorldPackage.IndirectCache, Kind: ShaderPipelineResourceKind.Buffer, SizeBytes: Bytes, StrideBytes: 4)],
            Packages: [new RenderGraphPackagePass(Name: "indirect", Package: RenderGraphPackageCatalog.Indirect, Outputs: [SdfWorldPackage.IndirectCache])]
        ));

        Assert.Equal(new[] { "indirect$place", "indirect$classify", "indirect$trace", "indirect$shade" }, plan.Pipeline.PassOrder);
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
    public void AnEnabledViewReadsTheCacheOnlyWhileShadingAndPreservesTraversalInItsCertificateVersion() {
        var fragment = SdfWorldPackage.WithIndirect(SdfWorldPackage.NativeFragment, 4096);

        Assert.Equal([SdfWorldPackage.IndirectCache], fragment.InputVersions);
        var external = Assert.Single(collection: fragment.Resources, predicate: resource => (resource.Name == SdfWorldPackage.IndirectCache));

        Assert.True(condition: external.IsExternal);
        Assert.Equal(ShaderPipelineResourceKind.Buffer, external.Kind);
        Assert.Equal([SdfWorldPackage.Parts.Views],
            fragment.Passes.Where(predicate: pass => pass.Inputs.Any(predicate: input => (input.Name == SdfWorldPackage.IndirectCache))).Select(selector: pass => pass.Name));
        var certificate = Assert.Single(fragment.Resources, resource => resource.Name == SdfWorldPackage.IndirectVisibility);
        Assert.Equal(SdfWorldPackage.Parts.ShadowVisibility, certificate.From);
        Assert.True(certificate.PreservesPredecessor);
        Assert.Equal(96u, certificate.StrideBytes);
        var views = Assert.Single(fragment.Passes, pass => pass.Name == SdfWorldPackage.Parts.Views);
        Assert.Contains(views.Outputs, output => output.Name == SdfWorldPackage.IndirectVisibility);
    }
    [InlineData("native", false)]
    [InlineData("native", true)]
    [InlineData("reduced", false)]
    [InlineData("reduced", true)]
    [InlineData("temporal", false)]
    [InlineData("temporal", true)]
    [Theory]
    public void EveryViewPlansOneWritableVisibilityAllocationBeforeItsLaterReaders(string quality, bool indirect) {
        var fragment = quality switch {
            "native" => SdfWorldPackage.NativeFragment,
            "reduced" => SdfWorldPackage.Fragment,
            _ => SdfWorldPackage.TemporalFragment,
        };
        if (indirect) { fragment = SdfWorldPackage.WithIndirect(fragment, 4096); }
        var package = RenderGraphPackageCatalog.Engine.Packages.Single(item => item.Id == RenderGraphPackageCatalog.SdfWorld) with {
            Fragment = fragment,
            Inputs = indirect ? [RenderGraphPackagePort.Buffer(RenderGraphPortAccess.ComputeReadWrite, strideBytes: 4, count: null)] : [],
        };
        var plan = new RenderGraphCompiler(new RenderGraphPackageCatalog(packages: [package])).Compile(definition: new RenderGraphDefinition(
            Name: "visibility", Schema: RenderGraphSchemas.Graph, Outputs: [SdfWorldPackage.Color],
            Resources: [new ShaderPipelineResource(Name: SdfWorldPackage.Color, Format: RenderGraphPackageCatalog.WorkingFormat.ToString(), Dimensions: ShaderPipelineDimensions.Relative()),
                .. (indirect ? new[] { new ShaderPipelineResource(Name: SdfWorldPackage.IndirectCache, Kind: ShaderPipelineResourceKind.Buffer,
                    SizeBytes: 4096, StrideBytes: 4, Initialization: ShaderPipelineInitialization.External) } : [])],
            Packages: [new RenderGraphPackagePass(Name: Sdf, Package: package.Id,
                Inputs: indirect ? [SdfWorldPackage.IndirectCache] : [], Outputs: [SdfWorldPackage.Color])])).Pipeline;
        var views = Assert.Single(plan.Passes, pass => pass.Package!.Part == SdfWorldPackage.Parts.Views);
        var visibility = Assert.Single(plan.Storages, storage => storage.Name == $"{Sdf}${SdfWorldPackage.Parts.Visibility}");
        var write = Assert.Single(views.Accesses, access => access.Version == $"{Sdf}${SdfWorldPackage.IndirectVisibility}");

        Assert.True(visibility.Declaration.Retained);
        Assert.Equal(96u, visibility.Declaration.StrideBytes);
        Assert.Equal(visibility.Index, write.Storage);
        Assert.Equal(GpuAccess.ShaderRead | GpuAccess.ShaderWrite, write.Use.Access);
        Assert.Equal(GpuStage.ComputeShader, write.Use.Stage);
        Assert.Equal(ShaderPipelineBarrierKind.Buffer, write.Barrier.Kind);
        Assert.Equal(GpuAccess.ShaderRead | GpuAccess.ShaderWrite, write.Barrier.DestinationAccess);
        Assert.Equal(1, views.Accesses.Count(access => access.Storage == visibility.Index && access.Use.Writes));
        var later = Assert.Single(plan.Passes, pass => pass.Package!.Part == (quality == "native" ? SdfWorldPackage.Parts.Composite : SdfWorldPackage.Resolve));
        var read = Assert.Single(later.Accesses, access => access.Storage == visibility.Index);

        Assert.True(later.Index > views.Index);
        Assert.Equal(views.Index, read.PriorPass);
        Assert.Equal(GpuAccess.ShaderRead | GpuAccess.ShaderWrite, read.Barrier.SourceAccess);
        Assert.Equal(GpuAccess.ShaderRead, read.Barrier.DestinationAccess);
        Assert.Equal(fragment.Passes.Count, plan.Passes.Count);
    }
}
