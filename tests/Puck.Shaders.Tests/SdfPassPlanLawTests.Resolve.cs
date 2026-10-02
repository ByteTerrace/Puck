using Puck.Abstractions.Gpu;
using Puck.Hosting;

namespace Puck.Shaders.Tests;

public sealed partial class SdfPassPlanLawTests {
    // A node's frame slots, whose every storage but a transient or retained one is allocated once each.
    private const uint InFlight = 3;

    private static RenderGraphPlan ResolvedPlan { get; } = new RenderGraphCompiler(packages: new RenderGraphPackageCatalog(packages: [
        RenderGraphPackageCatalog.Engine.Packages.Single(predicate: static item => (item.Id == RenderGraphPackageCatalog.SdfWorld)) with { Fragment = SdfWorldPackage.Fragment },
    ])).Compile(definition: new RenderGraphDefinition(Name: "resolved", Schema: RenderGraphSchemas.Graph,
        Outputs: [SdfWorldPackage.Color], Resources: [new ShaderPipelineResource(Name: SdfWorldPackage.Color,
            Format: RenderGraphPackageCatalog.WorkingFormat.ToString(), Dimensions: ShaderPipelineDimensions.Relative())],
        Packages: [new RenderGraphPackagePass(Name: Sdf, Package: RenderGraphPackageCatalog.SdfWorld, Outputs: [SdfWorldPackage.Color])]));
    // A reduced or temporal view's passes: the native fragment's through views, then the resolve, the sky's field runs
    // and the composite, which run at the output extent over what the resolve wrote.
    private static IEnumerable<string> ResolvedOrder => [
        .. Order.TakeWhile(predicate: static part => (part != SdfWorldPackage.Parts.Sky)),
        SdfWorldPackage.Resolve,
        SdfWorldPackage.Parts.Sky,
        SdfWorldPackage.Parts.Composite,
    ];

    [Fact]
    public void ReconstructionKeepsTheOutputGridAndPricesScratchAtTheRenderCeiling() {
        var package = RenderGraphPackageCatalog.Engine.Packages.Single(predicate: static item => (item.Id == RenderGraphPackageCatalog.SdfWorld));

        Assert.Same(expected: SdfWorldPackage.NativeFragment, actual: package.Fragment);
        var plan = ResolvedPlan;

        Assert.Equal(expected: ResolvedOrder, actual: plan.Pipeline.Passes.Select(selector: static pass => pass.Package!.Part));
        var counts = new ShaderPipelineStorageCounts(Height: 60, Width: 80) { RenderHeight = 30, RenderWidth = 40, Viewports = 1 };
        var visibility = plan.Pipeline.Storages.Single(predicate: static storage => (storage.Name == "sdf$visibility"));

        Assert.Equal(expected: ((40UL * 30) * 64), actual: visibility.Declaration.ResolveSizeBytes(counts: counts));
        var output = plan.Pipeline.Storages.Single(predicate: static storage => storage.Versions.Contains(value: SdfWorldPackage.Color));
        var lit = Storage(plan: plan.Pipeline, version: SdfWorldPackage.Parts.Lit);
        var distance = Storage(plan: plan.Pipeline, version: SdfWorldPackage.Parts.SurfaceDistance);
        var current = Storage(plan: plan.Pipeline, version: SdfWorldPackage.CurrentColor);

        Assert.Equal(expected: ShaderPipelineDimensions.Relative(), actual: output.Declaration.Dimensions);
        Assert.False(condition: output.Declaration.Transient);
        var resolve = plan.Pipeline.Passes.Single(predicate: static pass => (pass.Package!.Part == SdfWorldPackage.Resolve));

        Assert.Contains(collection: resolve.Parameters.Interface.Members, filter: static member => (member.Name == SdfWorldPackage.CurrentColor));
        Assert.Contains(collection: resolve.Parameters.Interface.Members, filter: static member => (member.Name == SdfWorldPackage.UpscaleSharpness));
        Assert.DoesNotContain(collection: SdfWorldPackage.Values, filter: static member => (member.Name == SdfWorldPackage.UpscaleSharpness));
        // Reconstruction reads the render-grid color through the visibility records and the dispatch box, and writes the
        // lit image and the surface distance at the output extent; the composite alone writes the output.
        Assert.Equal(expected: new[] { lit.Index, distance.Index }.Order(), actual: resolve.Accesses.Where(predicate: static access => access.Use.Access.HasFlag(flag: GpuAccess.ShaderWrite)).Select(selector: static access => access.Storage).Distinct().Order());
        Assert.Contains(collection: resolve.Accesses, filter: access => ((access.Storage == current.Index) && !access.Use.Writes));
        Assert.Contains(collection: resolve.Accesses, filter: access => ((access.Storage == visibility.Index) && !access.Use.Writes));
        Assert.Equal(expected: ShaderPipelineDimensions.Relative(), actual: resolve.Extent);
        Assert.All(collection: plan.Pipeline.Passes.Where(predicate: static pass => (pass.Package!.Part is SdfWorldPackage.Parts.Sky or SdfWorldPackage.Parts.Composite)),
            action: static pass => Assert.Equal(expected: ShaderPipelineDimensions.Relative(), actual: pass.Extent));
        Assert.Equal(expected: [SdfWorldPackage.Parts.Composite], actual: plan.Pipeline.Passes.Where(predicate: pass => pass.Accesses.Any(predicate: access => ((access.Storage == output.Index) && access.Use.Writes))).Select(selector: static pass => pass.Package!.Part));
        // Everything after the resolve is at the output extent; everything before it at the render ceiling.
        string[] outputExtent = [SdfWorldPackage.Color, SdfWorldPackage.Parts.Lit, SdfWorldPackage.Parts.SkyBase, SdfWorldPackage.Parts.SkyScale, SdfWorldPackage.Parts.SkyOffset];

        Assert.All(collection: plan.Pipeline.Storages.Where(predicate: static storage => (storage.Declaration.Dimensions is not null)),
            action: storage => Assert.Equal(
                expected: (outputExtent.Any(predicate: version => (storage.Versions.Contains(value: version) || storage.Versions.Contains(value: $"{Sdf}${version}"))) ? ShaderPipelineDimensions.Relative() : ShaderPipelineDimensions.Render()),
                actual: storage.Declaration.Dimensions
            ));
        Assert.All(collection: plan.Pipeline.Storages.Where(predicate: storage => ((storage.Declaration.Count is not null) && (storage.Index != distance.Index))),
            action: static storage => Assert.DoesNotContain(collection: storage.Declaration.Count!.SelectMany(selector: static term => term.Per), expected: ShaderPipelineCountBasis.Extent));
    }
    // A reduced view owns what the native graph at its render ceiling owns, with the ceiling's color held once as the
    // render-grid color, since views shades it and the resolve reads it inside one frame, and at the output extent
    // instead of the ceiling everything after the resolve: the color a frame slot, and once each the lit image, the
    // sky's three runs and the surface distance, four bytes an output pixel. At 1920x1080 and half scale that is
    // 99,532,800 bytes more.
    [Fact]
    public void AReducedViewAddsOnlyItsOutputExtentPassesOverTheNativeGraphAtItsCeiling() {
        var render = new ShaderPipelineStorageCounts(Height: 540, Width: 960) { InstanceMaskWords = 1, Instances = 5, Tiles = 8160, Viewports = 1 };
        var reduced = render with { Height = 1080, RenderHeight = 540, RenderWidth = 960, Width = 1920 };
        const ulong Output = ((1920UL * 1080) * 8);
        const ulong RenderColor = ((960UL * 540) * 8);
        const ulong Distance = ((1920UL * 1080) * 4);
        const ulong Expected = (((InFlight * (Output - RenderColor)) + (4 * (Output - RenderColor))) + Distance + RenderColor);

        Assert.Equal(actual: Expected, expected: 99_532_800UL);
        Assert.Equal(
            actual: (Bytes(plan: ResolvedPlan, counts: reduced) - Bytes(plan: Plan, counts: render)),
            expected: Expected
        );
        var currentColor = ResolvedPlan.Pipeline.Storages.Single(predicate: static storage => storage.Versions.Contains(value: $"{Sdf}${SdfWorldPackage.CurrentColor}"));

        Assert.True(condition: currentColor.Declaration.Transient);
        Assert.Equal(expected: ShaderPipelineDimensions.Render(), actual: currentColor.Declaration.Dimensions);

        // Every storage a node allocates for the plan, each image at its declared extent and format and each buffer at
        // its resolved size, once when transient and once a frame slot otherwise.
        static ulong Bytes(RenderGraphPlan plan, ShaderPipelineStorageCounts counts) => plan.Pipeline.Storages.Aggregate(seed: 0UL, func: (sum, storage) => {
            var declaration = storage.Declaration;
            var instances = ((declaration.Transient || declaration.Retained) ? 1UL : InFlight);

            if (declaration.Kind == ShaderPipelineResourceKind.Buffer) {
                return (sum + (declaration.ResolveSizeBytes(counts: counts) * instances));
            }
            var (width, height) = declaration.Dimensions!.Resolve(frameHeight: counts.Height, frameWidth: counts.Width, renderHeight: counts.RenderHeight, renderWidth: counts.RenderWidth);

            return (sum + (GpuPixelFormats.LevelByteLength(format: ShaderPipelineRenderNode.ParseFormat(format: declaration.Format), height: height, width: width) * instances));
        });
    }
}
