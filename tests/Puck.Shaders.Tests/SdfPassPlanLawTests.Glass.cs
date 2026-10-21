using Puck.Abstractions.Gpu;

namespace Puck.Shaders.Tests;

// The glass contract (SdfWorldPackage.WithGlass): three passes after views, the clear, the glass raster and the glass
// shading pass, over storages of their own; every later pass reads the glass shading pass's image in the lit image's
// place, and a temporal view's resolve reads the reactivity the glass pass raised. The planner orders the sums from the
// clear's transfer write to the raster's fragment-stage accumulation to the shading pass's compute read.
public sealed partial class SdfPassPlanLawTests {
    private static RenderGraphPlan GlassPlan(RenderGraphPackageFragment fragment) => new RenderGraphCompiler(packages: new RenderGraphPackageCatalog(packages: [
        RenderGraphPackageCatalog.Engine.Packages.Single(predicate: static item => (item.Id == RenderGraphPackageCatalog.SdfWorld)) with { Fragment = SdfWorldPackage.WithGlass(fragment: fragment) },
    ])).Compile(definition: new RenderGraphDefinition(Name: "glass", Schema: RenderGraphSchemas.Graph,
        Outputs: [SdfWorldPackage.Color], Resources: [new ShaderPipelineResource(Name: SdfWorldPackage.Color,
            Format: RenderGraphPackageCatalog.WorkingFormat.ToString(), Dimensions: ShaderPipelineDimensions.Relative())],
        Packages: [new RenderGraphPackagePass(Name: Sdf, Package: RenderGraphPackageCatalog.SdfWorld, Outputs: [SdfWorldPackage.Color])]));

    [Fact]
    public void TheGlassPassesFollowViewsAndEveryLaterPassReadsTheirImage() {
        foreach (var fragment in new[] { SdfWorldPackage.NativeFragment, SdfWorldPackage.Fragment, SdfWorldPackage.TemporalFragment }) {
            var glassed = SdfWorldPackage.WithGlass(fragment: fragment);
            var names = glassed.Passes.Select(selector: static pass => pass.Name).ToArray();
            var views = Array.IndexOf(array: names, value: SdfWorldPackage.Parts.Views);
            var lit = fragment.Passes.Single(predicate: static pass => (pass.Name == SdfWorldPackage.Parts.Views)).Outputs[0].Name;
            var temporal = ReferenceEquals(objA: fragment, objB: SdfWorldPackage.TemporalFragment);

            Assert.Equal(
                actual: names.Skip(count: views).Take(count: 4),
                expected: [SdfWorldPackage.Parts.Views, SdfWorldPackage.Parts.GlassClear, SdfWorldPackage.Parts.Glass, SdfWorldPackage.Parts.GlassShade]
            );
            Assert.Equal(actual: names.Take(count: views), expected: fragment.Passes.Select(selector: static pass => pass.Name).Take(count: views));
            Assert.Equal(actual: names.Skip(count: (views + 4)), expected: fragment.Passes.Select(selector: static pass => pass.Name).Skip(count: (views + 1)));

            var clear = glassed.Passes.Single(predicate: static pass => (pass.Name == SdfWorldPackage.Parts.GlassClear));
            var raster = glassed.Passes.Single(predicate: static pass => (pass.Name == SdfWorldPackage.Parts.Glass));
            var shade = glassed.Passes.Single(predicate: static pass => (pass.Name == SdfWorldPackage.Parts.GlassShade));

            Assert.Equal(expected: [SdfWorldPackage.Parts.GlassSumsCleared], actual: clear.Outputs.Select(selector: static output => output.Name));
            Assert.Equal(expected: [RenderGraphPortAccess.TransferWrite], actual: clear.OutputAccesses);
            Assert.False(condition: clear.CountsKernelWork);
            Assert.Equal(expected: [SdfWorldPackage.Parts.Visibility], actual: raster.Inputs.Select(selector: static input => input.Name));
            Assert.Equal(expected: [RenderGraphPortAccess.FragmentSampled], actual: raster.InputAccesses);
            Assert.Equal(expected: [SdfWorldPackage.Parts.GlassTarget, SdfWorldPackage.Parts.GlassDepth, SdfWorldPackage.Parts.GlassSums], actual: raster.Outputs.Select(selector: static output => output.Name));
            Assert.Equal(expected: [RenderGraphPortAccess.ColorAttachmentWrite, RenderGraphPortAccess.ColorAttachmentWrite, RenderGraphPortAccess.FragmentWrite], actual: raster.OutputAccesses);
            Assert.Equal(expected: [lit, SdfWorldPackage.Parts.Visibility, SdfWorldPackage.Parts.GlassTarget, SdfWorldPackage.Parts.GlassSums], actual: shade.Inputs.Select(selector: static input => input.Name));
            Assert.Equal(expected: (temporal ? [SdfWorldPackage.Parts.GlassLit, SdfWorldPackage.Parts.GlassReactivity] : [SdfWorldPackage.Parts.GlassLit]), actual: shade.Outputs.Select(selector: static output => output.Name));
            Assert.Same(expected: SdfWorldPackage.GlassMembers, actual: shade.Members);

            // No pass after the glass shading pass reads the lit image views wrote, or the reactivity views wrote.
            foreach (var pass in glassed.Passes.Skip(count: (views + 4))) {
                Assert.DoesNotContain(collection: pass.Inputs, filter: input => (input.Name == lit));
                Assert.DoesNotContain(collection: pass.Inputs, filter: static input => (input.Name == SdfWorldPackage.Parts.Reactivity));
            }
            Assert.Contains(collection: glassed.Passes.Single(predicate: static pass => (pass.Name == SdfWorldPackage.Parts.Sky)).Inputs, filter: static input => (input.Name == SdfWorldPackage.Parts.GlassLit));
            var compositeReads = (ReferenceEquals(objA: fragment, objB: SdfWorldPackage.NativeFragment) ? SdfWorldPackage.Parts.GlassLit : SdfWorldPackage.Parts.Lit);

            Assert.Contains(collection: glassed.Passes.Single(predicate: static pass => (pass.Name == SdfWorldPackage.Parts.Composite)).Inputs, filter: input => (input.Name == compositeReads));
            if (temporal) {
                Assert.Contains(collection: glassed.Passes.Single(predicate: static pass => (pass.Name == SdfWorldPackage.Resolve)).Inputs, filter: static input => (input.Name == SdfWorldPackage.Parts.GlassReactivity));
            }

            // The sums forward their clear as a preserving version, and the glass storages take the mesh target's and
            // depth's shape and the lit image's.
            var sums = glassed.Resources.Single(predicate: static resource => (resource.Name == SdfWorldPackage.Parts.GlassSums));
            var cleared = glassed.Resources.Single(predicate: static resource => (resource.Name == SdfWorldPackage.Parts.GlassSumsCleared));

            Assert.Equal(expected: SdfWorldPackage.Parts.GlassSumsCleared, actual: sums.From);
            Assert.True(condition: sums.PreservesPredecessor);
            Assert.True(condition: cleared.Retained);
            Assert.Equal(expected: sizeof(uint), actual: ((int)cleared.StrideBytes!.Value));
            Assert.All(collection: cleared.Count!, action: static term => Assert.Equal(expected: SdfWorldPackage.GlassSumWords, actual: ((uint)term.Elements)));
            Assert.Equal(expected: glassed.Resources.Single(predicate: static resource => (resource.Name == SdfWorldPackage.Parts.MeshTarget)) with { Name = SdfWorldPackage.Parts.GlassTarget }, actual: glassed.Resources.Single(predicate: static resource => (resource.Name == SdfWorldPackage.Parts.GlassTarget)));
            Assert.Equal(expected: glassed.Resources.Single(predicate: static resource => (resource.Name == SdfWorldPackage.Parts.MeshDepth)) with { Name = SdfWorldPackage.Parts.GlassDepth }, actual: glassed.Resources.Single(predicate: static resource => (resource.Name == SdfWorldPackage.Parts.GlassDepth)));
            Assert.Equal(expected: glassed.Resources.Single(predicate: resource => (resource.Name == lit)) with { Name = SdfWorldPackage.Parts.GlassLit }, actual: glassed.Resources.Single(predicate: static resource => (resource.Name == SdfWorldPackage.Parts.GlassLit)));
            _ = Assert.Throws<ArgumentException>(testCode: () => SdfWorldPackage.WithGlass(fragment: glassed));
        }
    }
    [Fact]
    public void ThePlannerOrdersTheSumsFromTheClearThroughTheRasterToTheShadingPass() {
        foreach (var fragment in new[] { SdfWorldPackage.NativeFragment, SdfWorldPackage.TemporalFragment }) {
            var plan = GlassPlan(fragment: fragment);
            var passes = plan.Pipeline.Passes;
            var sums = plan.Pipeline.Storages.Single(predicate: static storage => storage.Versions.Contains(value: $"{Sdf}${SdfWorldPackage.Parts.GlassSums}"));
            var edges = new List<(string Producer, string Consumer, GpuAccess SourceAccess, GpuAccess DestinationAccess, GpuStage SourceStage, GpuStage DestinationStage)>();

            foreach (var pass in passes) {
                foreach (var access in pass.Accesses) {
                    if ((access.Storage != sums.Index) || (access.PriorKind != ShaderPipelinePriorKind.Pass) || (access.Barrier.Kind == ShaderPipelineBarrierKind.None)) {
                        continue;
                    }
                    edges.Add(item: (passes[access.PriorPass].Package!.Part!, pass.Package!.Part!, access.Barrier.SourceAccess, access.Barrier.DestinationAccess, access.Barrier.SourceStage, access.Barrier.DestinationStage));
                }
            }

            Assert.Equal(
                actual: edges,
                expected: [
                    (SdfWorldPackage.Parts.GlassClear, SdfWorldPackage.Parts.Glass, GpuAccess.TransferWrite, GpuAccess.ShaderRead | GpuAccess.ShaderWrite, GpuStage.Transfer, GpuStage.FragmentShader),
                    (SdfWorldPackage.Parts.Glass, SdfWorldPackage.Parts.GlassShade, GpuAccess.ShaderRead | GpuAccess.ShaderWrite, GpuAccess.ShaderRead, GpuStage.FragmentShader, GpuStage.ComputeShader),
                ]
            );
            Assert.Equal(
                actual: passes.Select(selector: static pass => pass.Package!.Part).Where(predicate: static part => part!.StartsWith(value: "glass", comparisonType: StringComparison.Ordinal)),
                expected: [SdfWorldPackage.Parts.GlassClear, SdfWorldPackage.Parts.Glass, SdfWorldPackage.Parts.GlassShade]
            );
        }
    }
}
