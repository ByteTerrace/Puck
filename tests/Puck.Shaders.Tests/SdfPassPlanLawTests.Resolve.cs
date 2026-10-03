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
    // A reduced or temporal view's passes: the native fragment's through views, then the resolve, the sky's field runs on
    // the render grid, and the composite at the output extent over what the resolve wrote.
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
        var counts = new ShaderPipelineStorageCounts(Height: 60, Width: 80) { RenderHeight = 30, RenderWidth = 40, SegmentTapeWords = 33, Viewports = 1 };
        var visibility = plan.Pipeline.Storages.Single(predicate: static storage => (storage.Name == "sdf$visibility"));

        Assert.Equal(expected: ((40UL * 30) * 64), actual: visibility.Declaration.ResolveSizeBytes(counts: counts));
        var output = plan.Pipeline.Storages.Single(predicate: static storage => storage.Versions.Contains(value: SdfWorldPackage.Color));
        var lit = Storage(plan: plan.Pipeline, version: SdfWorldPackage.Parts.Lit);
        var transport = Storage(plan: plan.Pipeline, version: SdfWorldPackage.Parts.Transport);
        var current = Storage(plan: plan.Pipeline, version: SdfWorldPackage.CurrentColor);

        Assert.Equal(expected: ShaderPipelineDimensions.Relative(), actual: output.Declaration.Dimensions);
        Assert.False(condition: output.Declaration.Transient);
        var resolve = plan.Pipeline.Passes.Single(predicate: static pass => (pass.Package!.Part == SdfWorldPackage.Resolve));

        Assert.Contains(collection: resolve.Parameters.Interface.Members, filter: static member => (member.Name == SdfWorldPackage.CurrentColor));
        Assert.Contains(collection: resolve.Parameters.Interface.Members, filter: static member => (member.Name == SdfWorldPackage.UpscaleSharpness));
        Assert.DoesNotContain(collection: SdfWorldPackage.Values, filter: static member => (member.Name == SdfWorldPackage.UpscaleSharpness));
        // Reconstruction reads the render-grid color through the visibility records and the dispatch box, and writes the
        // lit image and the surface transport at the output extent; the composite alone writes the output.
        Assert.Equal(expected: new[] { lit.Index, transport.Index }.Order(), actual: resolve.Accesses.Where(predicate: static access => access.Use.Access.HasFlag(flag: GpuAccess.ShaderWrite)).Select(selector: static access => access.Storage).Distinct().Order());
        Assert.Contains(collection: resolve.Accesses, filter: access => ((access.Storage == current.Index) && !access.Use.Writes));
        Assert.Contains(collection: resolve.Accesses, filter: access => ((access.Storage == visibility.Index) && !access.Use.Writes));
        Assert.Equal(expected: ShaderPipelineDimensions.Relative(), actual: resolve.Extent);
        // The sky evaluates its field runs on the render grid, where views' color says what it covers; the composite reads
        // them at the output extent.
        var sky = plan.Pipeline.Passes.Single(predicate: static pass => (pass.Package!.Part == SdfWorldPackage.Parts.Sky));

        Assert.Equal(expected: ShaderPipelineDimensions.Render(), actual: sky.Extent);
        Assert.Contains(collection: sky.Accesses, filter: access => ((access.Storage == current.Index) && !access.Use.Writes));
        Assert.DoesNotContain(collection: sky.Accesses, filter: access => (access.Storage == lit.Index));
        Assert.Equal(expected: ShaderPipelineDimensions.Relative(), actual: plan.Pipeline.Passes.Single(predicate: static pass => (pass.Package!.Part == SdfWorldPackage.Parts.Composite)).Extent);
        Assert.Equal(expected: [SdfWorldPackage.Parts.Composite], actual: plan.Pipeline.Passes.Where(predicate: pass => pass.Accesses.Any(predicate: access => ((access.Storage == output.Index) && access.Use.Writes))).Select(selector: static pass => pass.Package!.Part));
        // What the resolve and the composite write is at the output extent; everything else at the render ceiling.
        string[] outputExtent = [SdfWorldPackage.Color, SdfWorldPackage.Parts.Lit];

        Assert.All(collection: plan.Pipeline.Storages.Where(predicate: static storage => (storage.Declaration.Dimensions is not null)),
            action: storage => Assert.Equal(
                expected: (outputExtent.Any(predicate: version => (storage.Versions.Contains(value: version) || storage.Versions.Contains(value: $"{Sdf}${version}"))) ? ShaderPipelineDimensions.Relative() : ShaderPipelineDimensions.Render()),
                actual: storage.Declaration.Dimensions
            ));
        Assert.All(collection: plan.Pipeline.Storages.Where(predicate: storage => ((storage.Declaration.Count is not null) && (storage.Index != transport.Index))),
            action: static storage => Assert.DoesNotContain(collection: storage.Declaration.Count!.SelectMany(selector: static term => term.Per), expected: ShaderPipelineCountBasis.Extent));
    }
    // A reduced view owns what the native graph at its render ceiling owns, with the ceiling's color held once as the
    // render-grid color, since views shades it and the resolve reads it inside one frame, and at the output extent
    // instead of the ceiling what the resolve and the composite write: the color a frame slot, and once each the lit
    // image and the surface transport, four bytes an output pixel. The sky's three runs stay at the ceiling. At 1920x1080
    // and half scale that is 62,208,000 bytes more.
    [Fact]
    public void AReducedViewAddsOnlyItsOutputExtentPassesOverTheNativeGraphAtItsCeiling() {
        var render = new ShaderPipelineStorageCounts(Height: 540, Width: 960) { InstanceMaskWords = 1, Instances = 5, SegmentTapeWords = 36, Tiles = 8160, Viewports = 1 };
        var reduced = render with { Height = 1080, RenderHeight = 540, RenderWidth = 960, Width = 1920 };
        const ulong Output = ((1920UL * 1080) * 8);
        const ulong RenderColor = ((960UL * 540) * 8);
        const ulong Transport = ((1920UL * 1080) * 4);
        const ulong Expected = ((((InFlight * (Output - RenderColor)) + (Output - RenderColor)) + Transport) + RenderColor);

        Assert.Equal(actual: Expected, expected: 62_208_000UL);
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
