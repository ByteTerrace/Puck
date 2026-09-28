using Puck.Abstractions.Gpu;

namespace Puck.Shaders.Tests;

public sealed partial class SdfPassPlanLawTests {
    [Fact]
    public void ReconstructionKeepsTheOutputGridAndPricesScratchAtTheRenderCeiling() {
        var package = RenderGraphPackageCatalog.Engine.Packages.Single(predicate: static item => (item.Id == RenderGraphPackageCatalog.SdfWorld));

        Assert.Same(expected: SdfWorldPackage.NativeFragment, actual: package.Fragment);
        var plan = new RenderGraphCompiler(packages: new RenderGraphPackageCatalog(packages: [package with { Fragment = SdfWorldPackage.Fragment }]))
            .Compile(definition: new RenderGraphDefinition(Name: "resolved", Schema: RenderGraphSchemas.Graph,
                Outputs: [SdfWorldPackage.Color], Resources: [new ShaderPipelineResource(Name: SdfWorldPackage.Color,
                    Format: RenderGraphPackageCatalog.WorkingFormat.ToString(), Dimensions: ShaderPipelineDimensions.Relative())],
                Packages: [new RenderGraphPackagePass(Name: Sdf, Package: RenderGraphPackageCatalog.SdfWorld, Outputs: [SdfWorldPackage.Color])]));

        Assert.Equal(expected: Order.Append(element: SdfWorldPackage.Resolve), actual: plan.Pipeline.Passes.Select(selector: static pass => pass.Package!.Part));
        var counts = new ShaderPipelineStorageCounts(Height: 60, Width: 80) { RenderHeight = 30, RenderWidth = 40, Viewports = 1 };
        var visibility = plan.Pipeline.Storages.Single(predicate: static storage => (storage.Name == "sdf$visibility"));

        Assert.Equal(expected: ((40UL * 30) * 64), actual: visibility.Declaration.ResolveSizeBytes(counts: counts));
        var surface = plan.Pipeline.Storages.Single(predicate: static storage => (storage.Name == "sdf$resolvedSurface"));

        Assert.Equal(expected: ((80UL * 60) * 8), actual: surface.Declaration.ResolveSizeBytes(counts: counts));
        Assert.True(condition: surface.Declaration.Transient);
        var output = plan.Pipeline.Storages.Single(predicate: static storage => storage.Versions.Contains(value: SdfWorldPackage.Color));

        Assert.Equal(expected: ShaderPipelineDimensions.Relative(), actual: output.Declaration.Dimensions);
        Assert.False(condition: output.Declaration.Transient);
        var resolve = plan.Pipeline.Passes[^1];

        Assert.Contains(collection: resolve.Parameters.Interface.Members, filter: static member => (member.Name == SdfWorldPackage.CurrentColor));
        Assert.Contains(collection: resolve.Parameters.Interface.Members, filter: static member => (member.Name == SdfWorldPackage.UpscaleSharpness));
        Assert.DoesNotContain(collection: SdfWorldPackage.Values, filter: static member => (member.Name == SdfWorldPackage.UpscaleSharpness));
        Assert.Contains(collection: resolve.Accesses, filter: access => ((access.Storage == visibility.Index) && (access.Use.Access == GpuAccess.ShaderRead)));
        Assert.Contains(collection: resolve.Accesses, filter: access => ((access.Storage == surface.Index) && (access.Use.Access == GpuAccess.ShaderWrite)));
        Assert.Equal(expected: ShaderPipelineDimensions.Relative(), actual: resolve.Extent);
        Assert.All(collection: plan.Pipeline.Storages.Where(predicate: storage => ((storage.Index != output.Index) && (storage.Declaration.Dimensions is not null))),
            action: static storage => Assert.Equal(expected: ShaderPipelineDimensions.Render(), actual: storage.Declaration.Dimensions));
    }
}
