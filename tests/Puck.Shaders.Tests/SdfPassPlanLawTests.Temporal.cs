using Puck.Abstractions.Gpu;
using Puck.Hosting;

namespace Puck.Shaders.Tests;

public sealed partial class SdfPassPlanLawTests {
    private static RenderGraphPlan TemporalPlan { get; } = new RenderGraphCompiler(packages: new RenderGraphPackageCatalog(packages: [
        RenderGraphPackageCatalog.Engine.Packages.Single(predicate: static item => (item.Id == RenderGraphPackageCatalog.SdfWorld)) with { Fragment = SdfWorldPackage.TemporalFragment },
    ])).Compile(definition: new RenderGraphDefinition(Name: "temporal", Schema: RenderGraphSchemas.Graph,
        Outputs: [SdfWorldPackage.Color], Resources: [new ShaderPipelineResource(Name: SdfWorldPackage.Color,
            Format: RenderGraphPackageCatalog.WorkingFormat.ToString(), Dimensions: ShaderPipelineDimensions.Relative())],
        Packages: [new RenderGraphPackagePass(Name: Sdf, Package: RenderGraphPackageCatalog.SdfWorld, Outputs: [SdfWorldPackage.Color])]));

    // The temporal resolve reads the history the previous frame wrote and writes this frame's, both at the output extent
    // and one storage a frame slot, beside the lit image and the surface transport; the reactivity views writes is one
    // retained render-extent buffer the resolve consumes within the frame. The sky never enters the history: the sky
    // and composite passes follow the resolve.
    [Fact]
    public void TheTemporalResolveReadsLastFramesHistoryAndWritesThisFrames() {
        var plan = TemporalPlan.Pipeline;

        Assert.Equal(expected: ResolvedOrder, actual: plan.Passes.Select(selector: static pass => pass.Package!.Part));
        var color = Storage(plan: plan, version: SdfWorldPackage.Parts.HistoryColor);
        var surface = Storage(plan: plan, version: SdfWorldPackage.Parts.HistorySurface);
        var reactivity = Storage(plan: plan, version: SdfWorldPackage.Parts.Reactivity);
        var lit = Storage(plan: plan, version: SdfWorldPackage.Parts.Lit);
        var transport = Storage(plan: plan, version: SdfWorldPackage.Parts.Transport);

        Assert.All(collection: [color, surface], action: static storage => {
            Assert.True(condition: storage.Declaration.History);
            Assert.False(condition: storage.Declaration.Transient);
            Assert.Equal(expected: ShaderPipelineInitialization.Zero, actual: storage.Declaration.Initialization);
        });
        Assert.Equal(expected: ShaderPipelineDimensions.Relative(), actual: color.Declaration.Dimensions);
        Assert.Equal(expected: [ShaderPipelineCountBasis.Extent, ShaderPipelineCountBasis.Viewports], actual: surface.Declaration.Count!.Single().Per);
        Assert.Equal(expected: SdfWorldPackage.HistorySurfaceWords, actual: surface.Declaration.Count!.Single().Elements);
        Assert.True(condition: reactivity.Declaration.Retained);
        Assert.Equal(expected: [ShaderPipelineCountBasis.RenderExtent, ShaderPipelineCountBasis.Viewports], actual: reactivity.Declaration.Count!.Single().Per);

        var resolve = plan.Passes.Single(predicate: static pass => (pass.Package!.Part == SdfWorldPackage.Resolve));

        Assert.Equal(
            expected: new[] { color.Index, surface.Index }.Order(),
            actual: resolve.Accesses.Where(predicate: static access => access.PreviousFrame).Select(selector: static access => access.Storage).Order()
        );
        Assert.Equal(
            expected: new[] { lit.Index, transport.Index, color.Index, surface.Index }.Order(),
            actual: resolve.Accesses.Where(predicate: static access => (!access.PreviousFrame && access.Use.Access.HasFlag(flag: GpuAccess.ShaderWrite))).Select(selector: static access => access.Storage).Order()
        );
        Assert.Contains(collection: resolve.Accesses, filter: access => ((access.Storage == reactivity.Index) && !access.Use.Writes));
        Assert.Equal(
            expected: [SdfWorldPackage.Parts.Views],
            actual: plan.Passes.Where(predicate: pass => pass.Accesses.Any(predicate: access => ((access.Storage == reactivity.Index) && access.Use.Writes))).Select(selector: static pass => pass.Package!.Part)
        );
        Assert.Contains(collection: resolve.Parameters.Interface.Members, filter: static member => (member.Name == SdfWorldPackage.HistoryColor));
        Assert.Contains(collection: resolve.Parameters.Interface.Members, filter: static member => (member.Group == ShaderInterfaceGroup.World));
    }
    // Each frame slot holds output-sized color and surface history and render-sized shadow history. Reactivity is
    // transient: four bytes a render pixel held once. The shadow history has five words per render pixel.
    [Fact]
    public void ATemporalViewAddsOnlyItsHistoryAndReactivityOverTheSpatialGraph() {
        var counts = new ShaderPipelineStorageCounts(Height: 1080, Width: 1920) { InstanceMaskWords = 1, Instances = 5, RenderHeight = 540, RenderWidth = 960, Tiles = 8160, Viewports = 1 };
        const ulong History = ((1920UL * 1080) * (8 + (4 * SdfWorldPackage.HistorySurfaceWords)));
        const ulong Reactivity = ((960UL * 540) * 4);
        const ulong ShadowHistory = ((960UL * 540) * 20);

        Assert.Equal(actual: ((InFlight * History) + Reactivity), expected: 151_372_800UL);
        Assert.Equal(
            expected: ((InFlight * (History + ShadowHistory)) + Reactivity),
            actual: (PlannedBytes(plan: TemporalPlan, counts: counts) - PlannedBytes(plan: ResolvedPlan, counts: counts))
        );
    }

    private static ShaderPipelinePlannedStorage Storage(ShaderPipelinePlan plan, string version) =>
        plan.Storages.Single(predicate: storage => (storage.Versions.Contains(value: $"{Sdf}${version}") || storage.Versions.Contains(value: version)));
    // Every storage a node allocates for the plan, each image at its declared extent and format and each buffer at its
    // resolved size, once when transient and once a frame slot otherwise.
    private static ulong PlannedBytes(RenderGraphPlan plan, ShaderPipelineStorageCounts counts) => plan.Pipeline.Storages.Aggregate(seed: 0UL, func: (sum, storage) => {
        var declaration = storage.Declaration;
        var instances = ((declaration.Transient || declaration.Retained) ? 1UL : InFlight);

        if (declaration.Kind == ShaderPipelineResourceKind.Buffer) {
            return (sum + (declaration.ResolveSizeBytes(counts: counts) * instances));
        }
        var (width, height) = declaration.Dimensions!.Resolve(frameHeight: counts.Height, frameWidth: counts.Width, renderHeight: counts.RenderHeight, renderWidth: counts.RenderWidth);

        return (sum + (GpuPixelFormats.LevelByteLength(format: ShaderPipelineRenderNode.ParseFormat(format: declaration.Format), height: height, width: width) * instances));
    });
}
