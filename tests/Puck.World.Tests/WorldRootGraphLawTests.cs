using System.Text.Json;
using Puck.Hosting;
using Puck.Shaders;

using Xunit;

namespace Puck.World.Tests;

/// <summary>The default render graph a world gets when it authors no root (<see cref="WorldRootGraph"/>): the world
/// alone as the root when nothing is drawn over it; otherwise the root graph reading the world and running each
/// <c>render.extensions</c> entry as its <c>post.&lt;id&gt;</c> pass in document order, then the overlay, planned by the
/// graph compiler; and an entry whose config does not bind refused by the compiler, naming the entry.</summary>
public sealed class WorldRootGraphLawTests {
    private const string FilmGrain = "sdf-film-grain";

    private static JsonElement Json(string text) => JsonDocument.Parse(json: text).RootElement.Clone();

    [Fact]
    public void WithNothingToDrawOverItTheWorldIsTheRoot() {
        var graph = WorldRootGraph.Compose(
            extensions: null,
            overlay: false,
            packages: RenderGraphPackageCatalog.Shipped
        );
        var world = Assert.Single(collection: graph.Instances.Instances);

        Assert.Null(@object: graph.Plan);
        Assert.Equal(
            actual: (graph.Root, world.Name, world.ExternalPackage, world.Passes),
            expected: (WorldViewGraphs.WorldInstance, WorldViewGraphs.WorldInstance, RenderGraphPackageCatalog.SdfWorld, Puck.SdfVm.SdfEngineNode.PassLabels.Length)
        );
        Assert.Empty(collection: graph.Footprints);
        Assert.Null(@object: Assert.Single(collection: graph.Graphs()));
    }
    [Fact]
    public void ThePostPassesThenTheOverlayRunInDocumentOrderOverTheWorld() {
        var graph = WorldRootGraph.Compose(
            extensions: [
                new WorldRenderExtensionEntry(Id: FilmGrain),
                new WorldRenderExtensionEntry(
                    Config: Json(text: """{"intensity":0.3}"""),
                    Id: FilmGrain
                ),
            ],
            overlay: true,
            packages: RenderGraphPackageCatalog.Shipped
        );
        var plan = Assert.IsType<RenderGraphPlan>(@object: graph.Plan);

        Assert.Equal(
            actual: plan.Steps.Select(selector: static step => $"{step.Name}:{step.Package?.Id}"),
            expected: [$"{FilmGrain}:post.{FilmGrain}", $"{FilmGrain}-2:post.{FilmGrain}", "overlay:overlay"]
        );
        Assert.Equal(
            actual: graph.PostPasses[FilmGrain],
            expected: [FilmGrain, $"{FilmGrain}-2"]
        );
        Assert.Equal(
            actual: (graph.Root, plan.Inputs.Single(), plan.Outputs.Single()),
            expected: (WorldViewGraphs.MainInstance, "world", "frame")
        );

        var main = graph.Instances.Instances[graph.Instances.IndexOf(name: WorldViewGraphs.MainInstance)];

        Assert.Equal(
            actual: (main.Passes, main.Kind, Assert.Single(collection: main.Reads).Producer),
            expected: (3, RenderGraphInstanceKind.Graph, WorldViewGraphs.WorldInstance)
        );
        Assert.Equal(
            actual: Assert.Single(collection: graph.Footprints),
            expected: new RenderGraphFootprint(
                Consumer: WorldViewGraphs.MainInstance,
                Height: 1.0,
                Producer: WorldViewGraphs.WorldInstance,
                Width: 1.0
            )
        );
        Assert.Equal(
            actual: graph.Graphs().Select(selector: static installed => installed?.Inputs.Single()),
            expected: [null, new RenderGraphRuntimeInput(Producer: WorldViewGraphs.WorldInstance, Version: "world")]
        );
    }
    [Fact]
    public void OnlyTheOverlayIsDrawnOverAWorldWithNoExtensions() {
        var graph = WorldRootGraph.Compose(
            extensions: [],
            overlay: true,
            packages: RenderGraphPackageCatalog.Shipped
        );

        Assert.Equal(
            actual: Assert.Single(collection: graph.Plan!.Steps).Name,
            expected: RenderGraphPackageCatalog.Overlay
        );
        Assert.Empty(collection: graph.PostPasses);
    }
    [Fact]
    public void AnEntryWhoseConfigDoesNotBindIsTheCompilersRefusalNamingTheEntry() {
        var refusal = Assert.Throws<WorldRootGraphRefusedException>(testCode: () => WorldRootGraph.Compose(
            extensions: [
                new WorldRenderExtensionEntry(Id: FilmGrain),
                new WorldRenderExtensionEntry(
                    Config: Json(text: """{"intensity":"loud"}"""),
                    Id: FilmGrain
                ),
            ],
            overlay: false,
            packages: RenderGraphPackageCatalog.Shipped
        ));

        Assert.StartsWith(
            actualString: refusal.Message,
            expectedStartString: $"render.extensions[1] '{FilmGrain}': RENDERGRAPH_PACKAGE_CONFIG: "
        );
    }
}
