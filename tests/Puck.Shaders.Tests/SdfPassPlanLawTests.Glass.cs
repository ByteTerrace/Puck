using Puck.Abstractions.Gpu;

namespace Puck.Shaders.Tests;

// The glass contract (SdfWorldPackage.WithGlass): four passes after views, the clear, the glass raster, the glass tiles
// pass and the glass shading pass, over storages of their own; every later pass that reads the lit image views wrote reads
// the glass image and the glass tiles beside it, a resolved composite the tiles alone, and a temporal view's resolve reads
// the reactivity the glass pass raised. The planner orders the sums from the clear's compute write to the raster's
// fragment-stage accumulation to the shading pass's compute read.
public sealed partial class SdfPassPlanLawTests {
    private static RenderGraphPlan GlassPlan(RenderGraphPackageFragment fragment) => FragmentPlan(fragment: SdfWorldPackage.WithGlass(fragment: fragment));
    // The plan of a graph running the world package as a fragment, compiled as the runtime compiles an instance's graph,
    // which throws for a graph the planner refuses.
    private static RenderGraphPlan FragmentPlan(RenderGraphPackageFragment fragment) => new RenderGraphCompiler(packages: new RenderGraphPackageCatalog(packages: [
        RenderGraphPackageCatalog.Engine.Packages.Single(predicate: static item => (item.Id == RenderGraphPackageCatalog.SdfWorld)) with { Fragment = fragment },
    ])).Compile(definition: new RenderGraphDefinition(Name: "glass", Schema: RenderGraphSchemas.Graph,
        Outputs: [SdfWorldPackage.Color], Resources: [new ShaderPipelineResource(Name: SdfWorldPackage.Color,
            Format: RenderGraphPackageCatalog.WorkingFormat.ToString(), Dimensions: ShaderPipelineDimensions.Relative())],
        Packages: [new RenderGraphPackagePass(Name: Sdf, Package: RenderGraphPackageCatalog.SdfWorld, Outputs: [SdfWorldPackage.Color])]));

    [Fact]
    public void TheGlassPassesFollowViewsAndEveryLaterReaderOfTheLitImageReadsTheGlassBesideIt() {
        foreach (var fragment in new[] { SdfWorldPackage.NativeFragment, SdfWorldPackage.Fragment, SdfWorldPackage.TemporalFragment }) {
            var glassed = SdfWorldPackage.WithGlass(fragment: fragment);
            var names = glassed.Passes.Select(selector: static pass => pass.Name).ToArray();
            var views = Array.IndexOf(array: names, value: SdfWorldPackage.Parts.Views);
            var lit = fragment.Passes.Single(predicate: static pass => (pass.Name == SdfWorldPackage.Parts.Views)).Outputs[0].Name;
            var temporal = ReferenceEquals(objA: fragment, objB: SdfWorldPackage.TemporalFragment);

            Assert.Equal(
                actual: names.Skip(count: views).Take(count: 5),
                expected: [SdfWorldPackage.Parts.Views, SdfWorldPackage.Parts.GlassClear, SdfWorldPackage.Parts.Glass, SdfWorldPackage.Parts.GlassTiles, SdfWorldPackage.Parts.GlassShade]
            );
            Assert.Equal(actual: names.Take(count: views), expected: fragment.Passes.Select(selector: static pass => pass.Name).Take(count: views));
            Assert.Equal(actual: names.Skip(count: (views + 5)), expected: fragment.Passes.Select(selector: static pass => pass.Name).Skip(count: (views + 1)));

            var clear = glassed.Passes.Single(predicate: static pass => (pass.Name == SdfWorldPackage.Parts.GlassClear));
            var raster = glassed.Passes.Single(predicate: static pass => (pass.Name == SdfWorldPackage.Parts.Glass));
            var tiles = glassed.Passes.Single(predicate: static pass => (pass.Name == SdfWorldPackage.Parts.GlassTiles));
            var shade = glassed.Passes.Single(predicate: static pass => (pass.Name == SdfWorldPackage.Parts.GlassShade));

            // The clear reads the raster's one input, so the two stand or record together.
            Assert.Equal(expected: raster.Inputs.Select(selector: static input => input.Name), actual: clear.Inputs.Select(selector: static input => input.Name));
            Assert.Equal(expected: [SdfWorldPackage.Parts.GlassSumsCleared, SdfWorldPackage.Parts.GlassTilesCleared], actual: clear.Outputs.Select(selector: static output => output.Name));
            Assert.Equal(expected: [RenderGraphPortAccess.ComputeWrite, RenderGraphPortAccess.ComputeWrite], actual: clear.OutputAccesses);
            Assert.Same(expected: SdfWorldPackage.GlassMembers, actual: clear.Members);
            Assert.Equal(expected: [SdfWorldPackage.Parts.Visibility], actual: raster.Inputs.Select(selector: static input => input.Name));
            Assert.Equal(expected: [RenderGraphPortAccess.FragmentSampled], actual: raster.InputAccesses);
            Assert.Equal(expected: [SdfWorldPackage.Parts.GlassTarget, SdfWorldPackage.Parts.GlassDepth, SdfWorldPackage.Parts.GlassSums, SdfWorldPackage.Parts.GlassTilesMarked], actual: raster.Outputs.Select(selector: static output => output.Name));
            Assert.Equal(expected: [RenderGraphPortAccess.ColorAttachmentWrite, RenderGraphPortAccess.ColorAttachmentWrite, RenderGraphPortAccess.FragmentWrite, RenderGraphPortAccess.FragmentWrite], actual: raster.OutputAccesses);
            Assert.Empty(collection: tiles.Inputs);
            Assert.Equal(expected: [SdfWorldPackage.Parts.GlassTilesListed, SdfWorldPackage.Parts.GlassArguments], actual: tiles.Outputs.Select(selector: static output => output.Name));
            Assert.Equal(expected: [lit, SdfWorldPackage.Parts.Visibility, SdfWorldPackage.Parts.GlassTarget, SdfWorldPackage.Parts.GlassSums, SdfWorldPackage.Parts.GlassProbeHistory], actual: shade.Inputs.Select(selector: static input => input.Name));
            Assert.Equal(
                expected: (temporal
                    ? [SdfWorldPackage.Parts.GlassLit, SdfWorldPackage.Parts.GlassReactivity, SdfWorldPackage.Parts.GlassProbeHistory, SdfWorldPackage.Parts.GlassTilesShaded]
                    : [SdfWorldPackage.Parts.GlassLit, SdfWorldPackage.Parts.GlassProbeHistory, SdfWorldPackage.Parts.GlassTilesShaded]),
                actual: shade.Outputs.Select(selector: static output => output.Name)
            );
            Assert.Equal(expected: ShaderPipelineDispatch.Indirect(arguments: SdfWorldPackage.Parts.GlassArguments), actual: shade.Dispatch);
            Assert.Same(expected: SdfWorldPackage.GlassMembers, actual: shade.Members);

            // Every pass after the glass that reads the lit image views wrote reads the glass image and the tiles beside it,
            // and none reads the reactivity views wrote.
            foreach (var pass in glassed.Passes.Skip(count: (views + 5))) {
                Assert.DoesNotContain(collection: pass.Inputs, filter: static input => (input.Name == SdfWorldPackage.Parts.Reactivity));
                if (pass.Inputs.Any(predicate: input => (input.Name == lit))) {
                    Assert.Contains(collection: pass.Inputs, filter: static input => (input.Name == SdfWorldPackage.Parts.GlassLit));
                    Assert.Contains(collection: pass.Inputs, filter: static input => (input.Name == SdfWorldPackage.Parts.GlassTilesShaded));
                }
            }
            Assert.Contains(collection: glassed.Passes.Single(predicate: static pass => (pass.Name == SdfWorldPackage.Parts.Sky)).Inputs, filter: static input => (input.Name == SdfWorldPackage.Parts.GlassLit));
            var composite = glassed.Passes.Single(predicate: static pass => (pass.Name == SdfWorldPackage.Parts.Composite));

            Assert.Contains(collection: composite.Inputs, filter: static input => (input.Name == SdfWorldPackage.Parts.GlassTilesShaded));
            Assert.Equal(expected: ReferenceEquals(objA: fragment, objB: SdfWorldPackage.NativeFragment), actual: composite.Inputs.Any(predicate: static input => (input.Name == SdfWorldPackage.Parts.GlassLit)));
            if (temporal) {
                Assert.Contains(collection: glassed.Passes.Single(predicate: static pass => (pass.Name == SdfWorldPackage.Resolve)).Inputs, filter: static input => (input.Name == SdfWorldPackage.Parts.GlassReactivity));
            }

            // The sums and the tiles forward their clear as preserving versions, each laid out by the render grid's tiles,
            // and the glass storages take the mesh target's and depth's shape and the lit image's.
            var sums = glassed.Resources.Single(predicate: static resource => (resource.Name == SdfWorldPackage.Parts.GlassSums));
            var cleared = glassed.Resources.Single(predicate: static resource => (resource.Name == SdfWorldPackage.Parts.GlassSumsCleared));
            var marked = glassed.Resources.Single(predicate: static resource => (resource.Name == SdfWorldPackage.Parts.GlassTilesMarked));
            var shaded = glassed.Resources.Single(predicate: static resource => (resource.Name == SdfWorldPackage.Parts.GlassTilesShaded));

            Assert.Equal(expected: SdfWorldPackage.Parts.GlassSumsCleared, actual: sums.From);
            Assert.True(condition: sums.PreservesPredecessor);
            Assert.True(condition: cleared.Retained);
            Assert.Equal(expected: sizeof(uint), actual: ((int)cleared.StrideBytes!.Value));
            Assert.All(collection: cleared.Count!, action: static term => Assert.Equal(expected: (SdfWorldPackage.GlassTilePixels * SdfWorldPackage.GlassSumWords), actual: ((uint)term.Elements)));
            Assert.Equal(expected: SdfWorldPackage.Parts.GlassTilesCleared, actual: marked.From);
            Assert.True(condition: marked.PreservesPredecessor);
            Assert.Equal(expected: SdfWorldPackage.Parts.GlassTilesListed, actual: shaded.From);
            Assert.True(condition: shaded.PreservesPredecessor);
            Assert.Equal(expected: SdfWorldPackage.Parts.GlassTilesMarked, actual: glassed.Resources.Single(predicate: static resource => (resource.Name == SdfWorldPackage.Parts.GlassTilesListed)).From);
            Assert.True(condition: glassed.Resources.Single(predicate: static resource => (resource.Name == SdfWorldPackage.Parts.GlassTilesCleared)).Retained);
            Assert.Equal(expected: glassed.Resources.Single(predicate: static resource => (resource.Name == SdfWorldPackage.Parts.MeshTarget)) with { Name = SdfWorldPackage.Parts.GlassTarget }, actual: glassed.Resources.Single(predicate: static resource => (resource.Name == SdfWorldPackage.Parts.GlassTarget)));
            Assert.Equal(expected: glassed.Resources.Single(predicate: static resource => (resource.Name == SdfWorldPackage.Parts.MeshDepth)) with { Name = SdfWorldPackage.Parts.GlassDepth }, actual: glassed.Resources.Single(predicate: static resource => (resource.Name == SdfWorldPackage.Parts.GlassDepth)));
            Assert.Equal(expected: glassed.Resources.Single(predicate: resource => (resource.Name == lit)) with { Name = SdfWorldPackage.Parts.GlassLit }, actual: glassed.Resources.Single(predicate: static resource => (resource.Name == SdfWorldPackage.Parts.GlassLit)));
            _ = Assert.Throws<ArgumentException>(testCode: () => SdfWorldPackage.WithGlass(fragment: glassed));
        }
    }
    // Every glass fragment a view can select compiles: each base fragment, binned or not, with the display stage or
    // without, glass added where SdfWorldPasses adds it (after the bins, before the display).
    [Fact]
    public void EveryGlassFragmentAViewSelectsCompiles() {
        foreach (var source in new[] { SdfWorldPackage.NativeFragment, SdfWorldPackage.Fragment, SdfWorldPackage.TemporalFragment, SdfWorldPackage.RateFragment }) {
            foreach (var binned in new[] { source, SdfWorldPackage.WithBins(fragment: source) }) {
                var glassed = SdfWorldPackage.WithGlass(fragment: binned);

                Assert.NotNull(@object: FragmentPlan(fragment: glassed));
                Assert.NotNull(@object: FragmentPlan(fragment: SdfWorldPackage.WithDisplay(fragment: glassed, quality: Puck.Hosting.BloomQuality.High)));
            }
        }
    }
    [Fact]
    public void ThePlannerOrdersTheSumsFromTheClearThroughTheRasterToTheShadingPass() {
        foreach (var fragment in new[] { SdfWorldPackage.NativeFragment, SdfWorldPackage.TemporalFragment }) {
            var plan = GlassPlan(fragment: fragment);
            var passes = plan.Pipeline.Passes;
            var sums = plan.Pipeline.Storages.Single(predicate: static storage => storage.Versions.Contains(value: $"{Sdf}${SdfWorldPackage.Parts.GlassSums}"));
            var edges = new List<(string Producer, string Consumer)>();

            foreach (var pass in passes) {
                foreach (var access in pass.Accesses) {
                    if ((access.Storage != sums.Index) || (access.PriorKind != ShaderPipelinePriorKind.Pass) || (access.Barrier.Kind == ShaderPipelineBarrierKind.None)) {
                        continue;
                    }
                    edges.Add(item: (passes[access.PriorPass].Package!.Part!, pass.Package!.Part!));
                    Assert.True(condition: ((access.Barrier.SourceAccess & GpuAccess.ShaderWrite) != 0));
                }
            }

            Assert.Equal(
                actual: edges,
                expected: [
                    (SdfWorldPackage.Parts.GlassClear, SdfWorldPackage.Parts.Glass),
                    (SdfWorldPackage.Parts.Glass, SdfWorldPackage.Parts.GlassShade),
                ]
            );
            Assert.Equal(
                actual: passes.Select(selector: static pass => pass.Package!.Part).Where(predicate: static part => part!.StartsWith(value: "glass", comparisonType: StringComparison.Ordinal)),
                expected: [SdfWorldPackage.Parts.GlassClear, SdfWorldPackage.Parts.Glass, SdfWorldPackage.Parts.GlassTiles, SdfWorldPackage.Parts.GlassShade]
            );
        }
    }
}
