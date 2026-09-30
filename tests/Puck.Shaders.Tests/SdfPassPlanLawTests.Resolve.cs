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

    [Fact]
    public void ReconstructionKeepsTheOutputGridAndPricesScratchAtTheRenderCeiling() {
        var package = RenderGraphPackageCatalog.Engine.Packages.Single(predicate: static item => (item.Id == RenderGraphPackageCatalog.SdfWorld));

        Assert.Same(expected: SdfWorldPackage.NativeFragment, actual: package.Fragment);
        var plan = ResolvedPlan;

        Assert.Equal(expected: Order.Append(element: SdfWorldPackage.Resolve), actual: plan.Pipeline.Passes.Select(selector: static pass => pass.Package!.Part));
        var counts = new ShaderPipelineStorageCounts(Height: 60, Width: 80) { RenderHeight = 30, RenderWidth = 40, Viewports = 1 };
        var visibility = plan.Pipeline.Storages.Single(predicate: static storage => (storage.Name == "sdf$visibility"));

        Assert.Equal(expected: ((40UL * 30) * 64), actual: visibility.Declaration.ResolveSizeBytes(counts: counts));
        var output = plan.Pipeline.Storages.Single(predicate: static storage => storage.Versions.Contains(value: SdfWorldPackage.Color));

        Assert.Equal(expected: ShaderPipelineDimensions.Relative(), actual: output.Declaration.Dimensions);
        Assert.False(condition: output.Declaration.Transient);
        var resolve = plan.Pipeline.Passes[^1];

        Assert.Contains(collection: resolve.Parameters.Interface.Members, filter: static member => (member.Name == SdfWorldPackage.CurrentColor));
        Assert.Contains(collection: resolve.Parameters.Interface.Members, filter: static member => (member.Name == SdfWorldPackage.UpscaleSharpness));
        Assert.DoesNotContain(collection: SdfWorldPackage.Values, filter: static member => (member.Name == SdfWorldPackage.UpscaleSharpness));
        // Reconstruction reads the render-grid color alone and writes the output alone.
        Assert.Equal(expected: [output.Index], actual: resolve.Accesses.Where(predicate: static access => access.Use.Access.HasFlag(flag: GpuAccess.ShaderWrite)).Select(selector: static access => access.Storage).Distinct());
        Assert.DoesNotContain(collection: resolve.Accesses, filter: access => (access.Storage == visibility.Index));
        Assert.Equal(expected: ShaderPipelineDimensions.Relative(), actual: resolve.Extent);
        Assert.All(collection: plan.Pipeline.Storages.Where(predicate: storage => ((storage.Index != output.Index) && (storage.Declaration.Dimensions is not null))),
            action: static storage => Assert.Equal(expected: ShaderPipelineDimensions.Render(), actual: storage.Declaration.Dimensions));
        Assert.All(collection: plan.Pipeline.Storages.Where(predicate: static storage => (storage.Declaration.Count is not null)),
            action: static storage => Assert.DoesNotContain(collection: storage.Declaration.Count!.SelectMany(selector: static term => term.Per), expected: ShaderPipelineCountBasis.Extent));
    }
    // A reduced view owns what the native graph at its render ceiling owns, with the ceiling's color held once instead of
    // once a frame slot, since the sky starts it, the views pass shades it and the resolve reads it inside one frame, and
    // with one output-sized color a frame slot beside it. It owns nothing else output-sized. At 1920x1080 and half scale
    // that is 49,766,400 output bytes less 8,294,400 bytes of slots the render-grid color no longer takes.
    [Fact]
    public void AReducedViewAddsOnlyItsOutputOverTheNativeGraphAtItsCeiling() {
        var render = new ShaderPipelineStorageCounts(Height: 540, Width: 960) { InstanceMaskWords = 1, Instances = 5, Tiles = 8160, Viewports = 1 };
        var reduced = render with { Height = 1080, RenderHeight = 540, RenderWidth = 960, Width = 1920 };
        const ulong Output = ((1920UL * 1080) * 8);
        const ulong RenderColor = ((960UL * 540) * 8);

        Assert.Equal(actual: ((InFlight * Output) - ((InFlight - 1) * RenderColor)), expected: 41_472_000UL);
        Assert.Equal(
            actual: (Bytes(plan: ResolvedPlan, counts: reduced) - Bytes(plan: Plan, counts: render)),
            expected: ((InFlight * Output) - ((InFlight - 1) * RenderColor))
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
