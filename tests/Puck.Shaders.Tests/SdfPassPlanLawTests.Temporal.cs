using Puck.Abstractions.Gpu;

namespace Puck.Shaders.Tests;

public sealed partial class SdfPassPlanLawTests {
    [Fact]
    public void TemporalHistoryIsPerOutputWhileReactivityExistsOnlyAtTheRenderGrid() {
        var package = RenderGraphPackageCatalog.Engine.Packages.Single(static item => item.Id == RenderGraphPackageCatalog.SdfWorld);
        var plan = new RenderGraphCompiler(packages: new RenderGraphPackageCatalog(packages: [package with { Fragment = SdfWorldPackage.TemporalFragment }]))
            .Compile(definition: new RenderGraphDefinition(Name: "temporal", Schema: RenderGraphSchemas.Graph,
                Outputs: [SdfWorldPackage.Color], Resources: [new ShaderPipelineResource(Name: SdfWorldPackage.Color,
                    Format: RenderGraphPackageCatalog.WorkingFormat.ToString(), Dimensions: ShaderPipelineDimensions.Relative())],
                Packages: [new RenderGraphPackagePass(Name: Sdf, Package: RenderGraphPackageCatalog.SdfWorld, Outputs: [SdfWorldPackage.Color])]));
        var color = plan.Pipeline.Storages.Single(static storage => storage.Versions.Contains(SdfWorldPackage.Color));
        var surface = plan.Pipeline.Storages.Single(static storage => storage.Name == "sdf$resolvedSurface");
        var reactive = plan.Pipeline.Storages.Single(static storage => storage.Name == "sdf$reactivity");
        var resolve = plan.Pipeline.Passes[^1];

        foreach (var history in new[] { color, surface }) {
            Assert.True(condition: history.Declaration.History);
            Assert.False(condition: history.Declaration.Transient);
            Assert.Equal(expected: ShaderPipelineInitialization.Zero, actual: history.Declaration.Initialization);
            Assert.Contains(collection: resolve.Accesses, filter: access => access.Storage == history.Index && access.PreviousFrame && access.Use.Access == GpuAccess.ShaderRead);
            Assert.Contains(collection: resolve.Accesses, filter: access => access.Storage == history.Index && !access.PreviousFrame && access.Use.Access == GpuAccess.ShaderWrite);
        }
        Assert.Equal(expected: ShaderPipelineDimensions.Relative(), actual: color.Declaration.Dimensions);
        Assert.Equal(expected: 80UL * 60 * 8, actual: surface.Declaration.ResolveSizeBytes(counts: new ShaderPipelineStorageCounts(Height: 60, Width: 80) { RenderHeight = 30, RenderWidth = 40 }));
        Assert.Equal(expected: ShaderPipelineDimensions.Render(), actual: reactive.Declaration.Dimensions);
        Assert.Equal(expected: nameof(GpuPixelFormat.R32Float), actual: reactive.Declaration.Format);
        Assert.Equal(expected: 40UL * 30 * 4, actual: GpuPixelFormats.LevelByteLength(format: GpuPixelFormat.R32Float, height: 30, width: 40));
        Assert.True(condition: reactive.Declaration.Transient);
        Assert.False(condition: reactive.Declaration.History);
        Assert.Single(collection: plan.Pipeline.Passes.Where(pass => pass.Accesses.Any(access => access.Storage == reactive.Index && access.Use.Access == GpuAccess.ShaderRead)));
        Assert.DoesNotContain(collection: SdfWorldPackage.NativeFragment.Resources, filter: static resource => resource.History || resource.Name == SdfWorldPackage.Reactivity);
        Assert.DoesNotContain(collection: SdfWorldPackage.Fragment.Resources, filter: static resource => resource.History || resource.Name == SdfWorldPackage.Reactivity);
    }
}
