using Puck.Abstractions.Gpu;
using Puck.Hosting;

namespace Puck.Shaders.Tests;

public sealed partial class SdfPassPlanLawTests {
    [Fact]
    public void TheLightCameraOwnsOneDepthScratchAndEveryHeldAndFadingRegion() {
        var fragment = SdfWorldPackage.LightViewFragment(maps: 4);
        Assert.Equal(expected: 4_194_304UL, actual: Assert.Single(collection: fragment.Resources,
            predicate: resource => (resource.Name == SdfWorldPackage.IndirectLightDepth)).SizeBytes);
        var package = RenderGraphPackageCatalog.Engine.Packages.Single(predicate: item => (item.Id == RenderGraphPackageCatalog.SdfWorld));
        var plan = new RenderGraphCompiler(packages: new RenderGraphPackageCatalog(packages: [package with {
            Fragment = fragment,
            // This selected camera fragment publishes its depth bank, rather than the catalog's ordinary color image.
            Outputs = [RenderGraphPackagePort.Buffer(access: RenderGraphPortAccess.ComputeWrite, count: null, strideBytes: sizeof(float))],
        }])).Compile(definition: new RenderGraphDefinition(
            Name: "light", Schema: RenderGraphSchemas.Graph, Outputs: [SdfWorldPackage.IndirectLightDepth],
            Resources: [new ShaderPipelineResource(Name: SdfWorldPackage.IndirectLightDepth, Kind: ShaderPipelineResourceKind.Buffer,
                StrideBytes: sizeof(float), SizeBytes: (4UL * 512 * 512 * sizeof(float)), Retained: true)],
            Packages: [new RenderGraphPackagePass(Name: "light", Package: RenderGraphPackageCatalog.SdfWorld, Outputs: [SdfWorldPackage.IndirectLightDepth])]));
        Assert.Equal(expected: [SdfWorldPackage.Parts.Mask, SdfWorldPackage.Parts.Beam, SdfWorldPackage.Parts.CullArgs,
            SdfWorldPackage.Parts.Mesh, SdfWorldPackage.Parts.Primary, SdfWorldPackage.LightDepth], actual: plan.Pipeline.Passes.Select(selector: pass => pass.Package!.Part));
        Assert.All(collection: plan.Pipeline.Storages, action: storage => Assert.True(condition: storage.Declaration.Retained));
        var counts = new ShaderPipelineStorageCounts(Width: 512, Height: 512) {
            Viewports = 1, Tiles = ((512 / SdfWorldPackage.TileSize) * (512 / SdfWorldPackage.TileSize)), Instances = 256, InstanceMaskWords = 8,
        };
        var bank = Assert.Single(collection: plan.Pipeline.Storages, predicate: storage => storage.Versions.Contains(value: SdfWorldPackage.IndirectLightDepth));
        Assert.Equal(expected: 4_194_304UL, actual: bank.Declaration.ResolveSizeBytes(counts: counts));
        var visibility = Storage(plan: plan.Pipeline, version: "light$visibility");
        Assert.Equal(expected: 25_165_824UL, actual: visibility.Declaration.ResolveSizeBytes(counts: counts));
        var total = PlannedBytes(plan: plan, counts: counts);
        // Visibility96 + mesh16 + hardware depth4 per pixel, then four retained R32 map regions. Count every
        // additional traversal buffer and all three rings of frame/pass constants in the remaining one-MiB ceiling.
        var blocks = (InFlight * (plan.Pipeline.Passes.Sum(selector: pass => ((long)pass.Parameters.SizeBytes)) +
            plan.Pipeline.Passes.Max(selector: pass => pass.Parameters.FrameBlockSizeBytes)));
        const ulong DepthAndMaps = (30_408_704UL + 4_194_304UL);
        Assert.InRange(actual: (total + ((ulong)blocks) - DepthAndMaps), low: 1UL, high: 1_048_576UL);
        var publication = plan.Pipeline.Passes.Single(predicate: pass => (pass.Package!.Part == SdfWorldPackage.LightDepth));
        var visibilityRead = Assert.Single(collection: publication.Accesses, predicate: access => (access.Storage == visibility.Index));
        Assert.Equal(expected: GpuAccess.ShaderWrite, actual: visibilityRead.Barrier.SourceAccess);
        Assert.Equal(expected: GpuAccess.ShaderRead, actual: visibilityRead.Barrier.DestinationAccess);
        var consumer = SdfWorldPackage.WithLightViews(fragment: SdfWorldPackage.NativeFragment, maps: 4);
        Assert.Equal(expected: [SdfWorldPackage.Parts.Views], actual: consumer.Passes
            .Where(predicate: pass => pass.Inputs.Any(predicate: input => (input.Name == SdfWorldPackage.IndirectLightDepth)))
            .Select(selector: pass => pass.Name));
        Assert.True(condition: Assert.Single(collection: consumer.Resources, predicate: resource => (resource.Name == SdfWorldPackage.IndirectLightDepth)).IsExternal);
    }
}
