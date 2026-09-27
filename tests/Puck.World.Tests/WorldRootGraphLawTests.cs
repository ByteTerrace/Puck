using System.Buffers.Binary;
using System.Text.Json;
using Puck.Hosting;
using Puck.Shaders;
using Puck.World.Client;

using Xunit;

namespace Puck.World.Tests;

/// <summary>The default render graph a world gets when it authors no root (<see cref="WorldRootGraph"/>): the world
/// alone as the root when nothing is drawn over it; otherwise the root graph reading the world and running each
/// <c>views.post</c> row as a pass of its package, named by the row, in document order, then the overlay, planned by the
/// graph compiler; a <c>place</c> pass per pane a layout slot names, ahead of them, reading the pane's instance; and a
/// row whose config does not bind refused by the compiler, naming the row.</summary>
public sealed class WorldRootGraphLawTests {
    private const string FilmGrain = RenderGraphPackageCatalog.SdfFilmGrain;

    private static JsonElement Json(string text) => JsonDocument.Parse(json: text).RootElement.Clone();

    [Fact]
    public void WithNothingToDrawOverItTheWorldIsTheRoot() {
        var graph = WorldRootGraph.Compose(
            overlay: false,
            packages: RenderGraphPackageCatalog.Engine,
            post: null
        );
        var world = Assert.Single(collection: graph.Instances);

        Assert.Null(@object: graph.Plan);
        Assert.Equal(
            actual: (graph.Root, world.Name, world.ExternalPackage, world.Passes),
            expected: (WorldViewGraphs.WorldInstance, WorldViewGraphs.WorldInstance, RenderGraphPackageCatalog.SdfWorld, SdfWorldPackage.Fragment.Passes.Count)
        );
        Assert.Empty(collection: graph.Footprints);
        Assert.Null(@object: Assert.Single(collection: graph.Graphs()));
    }
    // A pane is display-referred, its own shader's tonemap included (the moth studio's pane applies its own filmic curve),
    // so the tonemap reads the placed views alone: every pane, post pass and the overlay draws over its output, and none
    // is tonemapped by the root.
    [Fact]
    public void AFilmicTonemapReadsOnlyThePlacedViewsAndEveryPanePostPassAndTheOverlayDrawOverItsOutput() {
        WorldViewPostPass[] post = [new(Name: "grain", Package: FilmGrain)];
        var filmic = WorldRootGraph.Compose(
            overlay: true,
            packages: RenderGraphPackageCatalog.Engine,
            panes: ["moth-pipeline"],
            post: post,
            tonemap: WorldTonemap.Filmic,
            views: 2
        );
        var steps = Assert.IsType<RenderGraphPlan>(@object: filmic.Plan).Steps;

        Assert.Equal(
            actual: steps.Select(selector: static step => $"{step.Name}:{step.Package?.Id}"),
            expected: ["main$view$1:place", "main$view$2:place", $"main$tonemap:{RenderGraphPackageCatalog.SdfTonemap}", "moth-pipeline:place", $"grain:{FilmGrain}", "main$overlay:overlay"]
        );

        // The tonemap reads what the last view's place pass wrote, and the pane's place pass takes the tonemap's output
        // as its base and the pane's own image, never a tonemapped one, as its source.
        var tonemap = steps[2].Planned;
        var pane = steps[3].Planned;

        Assert.Equal(
            actual: (Scene: Assert.Single(collection: tonemap.Inputs).Name, Base: pane.Inputs[0].Name, Source: pane.Inputs[1].Name),
            expected: (Scene: steps[1].Planned.Outputs.Single().Name, Base: tonemap.Outputs.Single().Name, Source: "moth-pipeline")
        );
        Assert.Equal(
            actual: filmic.Tonemap,
            expected: WorldTonemap.Filmic
        );

        // One view has no place pass, so the tonemap reads the world's version.
        var single = WorldRootGraph.Compose(
            overlay: true,
            packages: RenderGraphPackageCatalog.Engine,
            panes: ["moth-pipeline"],
            post: null,
            tonemap: WorldTonemap.Filmic
        );
        var first = Assert.IsType<RenderGraphPlan>(@object: single.Plan).Steps[0];

        Assert.Equal(
            actual: (first.Name, Assert.Single(collection: first.Planned.Inputs).Name),
            expected: ("main$tonemap", "main$world")
        );

        // With nothing else over the world, a filmic tonemap alone makes the root graph, which shows the world over its
        // whole extent.
        var alone = WorldRootGraph.Compose(
            overlay: false,
            packages: RenderGraphPackageCatalog.Engine,
            post: null,
            tonemap: WorldTonemap.Filmic
        );

        Assert.Equal(
            actual: (alone.Root, Footprint: Assert.Single(collection: alone.Footprints).Producer),
            expected: (WorldViewGraphs.MainInstance, Footprint: WorldViewGraphs.WorldInstance)
        );
        Assert.Equal(
            actual: Assert.IsType<RenderGraphPlan>(@object: alone.Plan).Steps.Select(selector: static step => step.Name),
            expected: ["main$tonemap"]
        );

        // None runs no pass, so a world with nothing to draw over it stays its own root.
        var none = WorldRootGraph.Compose(
            overlay: false,
            packages: RenderGraphPackageCatalog.Engine,
            post: null,
            tonemap: WorldTonemap.None
        );

        Assert.Equal(
            actual: (none.Root, none.Plan, none.Tonemap),
            expected: (WorldViewGraphs.WorldInstance, ((RenderGraphPlan?)null), WorldTonemap.None)
        );
    }
    [Fact]
    public void ThePostPassesThenTheOverlayRunInDocumentOrderOverTheWorld() {
        WorldViewPostPass[] post = [
            new(Name: "grain", Package: FilmGrain),
            new(
                Config: Json(text: """{"intensity":0.3}"""),
                Name: "heavy-grain",
                Package: FilmGrain
            ),
        ];
        var graph = WorldRootGraph.Compose(
            overlay: true,
            packages: RenderGraphPackageCatalog.Engine,
            post: post
        );
        var plan = Assert.IsType<RenderGraphPlan>(@object: graph.Plan);

        Assert.Equal(
            actual: plan.Steps.Select(selector: static step => $"{step.Name}:{step.Package?.Id}"),
            expected: [$"grain:{FilmGrain}", $"heavy-grain:{FilmGrain}", "main$overlay:overlay"]
        );
        Assert.Equal(
            actual: graph.Post,
            expected: post
        );
        Assert.Equal(
            actual: (graph.Root, plan.Inputs.Single(), plan.Outputs.Single()),
            expected: (WorldViewGraphs.MainInstance, "main$world", "main$frame")
        );

        var main = graph.Instances.Single(predicate: static instance => (instance.Name == WorldViewGraphs.MainInstance));

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
            expected: [null, new RenderGraphRuntimeInput(Producer: WorldViewGraphs.WorldInstance, Version: "main$world")]
        );
    }
    [Fact]
    public void EachPaneIsPlacedOverTheWorldBeforeThePostPassesAndMainBecomesTheRoot() {
        var graph = WorldRootGraph.Compose(
            overlay: false,
            packages: RenderGraphPackageCatalog.Engine,
            panes: ["left", "right"],
            post: [new WorldViewPostPass(Name: "grain", Package: FilmGrain)]
        );
        var plan = Assert.IsType<RenderGraphPlan>(@object: graph.Plan);
        var main = graph.Instances.Single(predicate: static instance => (instance.Name == WorldViewGraphs.MainInstance));

        Assert.Equal(
            actual: plan.Steps.Select(selector: static step => $"{step.Name}:{step.Package?.Id}"),
            expected: ["left:place", "right:place", $"grain:{FilmGrain}"]
        );
        Assert.Equal(
            actual: (graph.Root, string.Join(separator: ",", values: main.Reads.Select(selector: static read => read.Producer))),
            expected: (WorldViewGraphs.MainInstance, "world,left,right")
        );
        Assert.Equal(
            actual: graph.Graphs()[1]!.Inputs,
            expected: [
                new RenderGraphRuntimeInput(Producer: WorldViewGraphs.WorldInstance, Version: "main$world"),
                new RenderGraphRuntimeInput(Producer: "left", Version: "left"),
                new RenderGraphRuntimeInput(Producer: "right", Version: "right"),
            ]
        );

        // With nothing else drawn over the world, a pane alone still makes main the root.
        var panesOnly = WorldRootGraph.Compose(
            overlay: false,
            packages: RenderGraphPackageCatalog.Engine,
            panes: ["left"],
            post: null
        );

        Assert.Equal(
            actual: (panesOnly.Root, Assert.Single(collection: panesOnly.Plan!.Steps).Name),
            expected: (WorldViewGraphs.MainInstance, "left")
        );
    }
    [Fact]
    public void ThePanesAreTheInstancesAnyLayoutSlotNamesInFirstNamedOrder() {
        var views = new WorldViewDefaults(Layouts: [
            new WorldViewLayout(Name: "a", Slots: [new WorldViewSlot(Instance: "right"), new WorldViewSlot(Camera: "c")]),
            new WorldViewLayout(Name: "b", Slots: [new WorldViewSlot(Instance: "left"), new WorldViewSlot(Instance: "right")]),
        ]);

        Assert.Equal(
            actual: WorldRootGraph.PanesOf(views: views),
            expected: ["right", "left"]
        );
        Assert.Empty(collection: WorldRootGraph.PanesOf(views: new WorldViewDefaults()));
    }
    [Fact]
    public void OnlyTheOverlayIsDrawnOverAWorldWithNoPostPasses() {
        var graph = WorldRootGraph.Compose(
            overlay: true,
            packages: RenderGraphPackageCatalog.Engine,
            post: []
        );

        Assert.Equal(
            actual: Assert.Single(collection: graph.Plan!.Steps).Name,
            expected: "main$overlay"
        );
        Assert.Empty(collection: graph.Post);
    }
    // A pane takes its authored name as its version and its place pass, a post pass its row's name, and the root declares
    // its own versions and passes in the generated form, so a pane or a post pass may be named what those once were.
    [Fact]
    public void APaneOrAPostPassNamedLikeTheRootsOwnVersionsAndPassesComposes() {
        var graph = WorldRootGraph.Compose(
            overlay: true,
            packages: RenderGraphPackageCatalog.Engine,
            panes: ["frame", "stage1"],
            post: [new WorldViewPostPass(Name: "overlay", Package: FilmGrain)]
        );

        Assert.Equal(
            actual: graph.Plan!.Steps.Select(selector: static step => step.Name),
            expected: ["frame", "stage1", "overlay", "main$overlay"]
        );
    }
    [Fact]
    public void ARowWhoseConfigDoesNotBindIsTheCompilersRefusalNamingTheRow() {
        var refusal = Assert.Throws<WorldRootGraphRefusedException>(testCode: () => WorldRootGraph.Compose(
            overlay: false,
            packages: RenderGraphPackageCatalog.Engine,
            post: [
                new WorldViewPostPass(Name: "grain", Package: FilmGrain),
                new WorldViewPostPass(
                    Config: Json(text: """{"intensity":"loud"}"""),
                    Name: "loud",
                    Package: FilmGrain
                ),
            ]
        ));

        Assert.StartsWith(
            actualString: refusal.Message,
            expectedStartString: "views.post[1] 'loud': RENDERGRAPH_PACKAGE_CONFIG: "
        );
    }
    [Fact]
    public void WithMoreThanOneViewEachViewIsAProducerPlacedAheadOfThePanesAndMainIsTheRoot() {
        var graph = WorldRootGraph.Compose(
            overlay: false,
            packages: RenderGraphPackageCatalog.Engine,
            panes: ["pane"],
            post: null,
            views: 3
        );
        var plan = Assert.IsType<RenderGraphPlan>(@object: graph.Plan);

        Assert.Equal(
            actual: graph.Producers.Select(selector: static producer => $"{producer.Name}:{producer.ExternalPackage}"),
            expected: ["world:sdf.world", "world$2:sdf.world", "world$3:sdf.world"]
        );
        Assert.Equal(
            actual: (graph.Root, graph.Views, graph.Instances.Count),
            expected: (WorldViewGraphs.MainInstance, 3, 4)
        );
        Assert.Equal(
            actual: plan.Steps.Select(selector: static step => $"{step.Name}:{step.Package?.Id}"),
            expected: ["main$view$1:place", "main$view$2:place", "main$view$3:place", "pane:place"]
        );
        Assert.Equal(
            actual: graph.ViewPasses,
            expected: ["main$view$1", "main$view$2", "main$view$3"]
        );
        // The first view's version is a second version of the first producer, so its base and its source differ.
        Assert.Equal(
            actual: graph.Graphs()[3]!.Inputs.Select(selector: static input => $"{input.Version}<-{input.Producer}"),
            expected: ["main$world<-world", "main$view$1<-world", "main$view$2<-world$2", "main$view$3<-world$3", "pane<-pane"]
        );
        // Each view's footprint follows its rect frame by frame, so the root states none of its own.
        Assert.Empty(collection: graph.Footprints);
    }
    /// <summary>The first view's place pass writes the letterbox color outside its rect and every later place pass keeps
    /// its base there, so pixels no view or pane covers show the letterbox color, never a view's clamped edge.</summary>
    [Fact]
    public void OnlyTheFirstViewsPlacePassLetterboxesOutsideItsRect() {
        var plan = Assert.IsType<RenderGraphPlan>(@object: WorldRootGraph.Compose(
            overlay: false,
            packages: RenderGraphPackageCatalog.Engine,
            panes: ["pane"],
            post: null,
            views: 3
        ).Plan);

        Assert.Equal(
            actual: plan.Pipeline.Passes.Select(selector: static pass => {
                Assert.True(condition: pass.Parameters.TryBind(
                    config: null,
                    reason: out var reason,
                    values: out var values
                ), userMessage: reason);

                return $"{pass.Name}:{BinaryPrimitives.ReadUInt32LittleEndian(source: values.Bytes.Span[((int)pass.Parameters.BlockOffsetOf(member: RenderGraphPackageCatalog.PlaceLetterbox))..])}";
            }),
            expected: ["main$view$1:1", "main$view$2:0", "main$view$3:0", "pane:0"]
        );
    }
    [Fact]
    public void AViewProducersNameReadsBackAsItsView() {
        Assert.Equal(
            actual: (WorldViewNames.World(view: 2), WorldViewNames.ViewOf(instance: "world$2"), WorldViewNames.ViewOf(instance: "world$5")),
            expected: ("world$2", ((int?)1), ((int?)4))
        );
        Assert.Null(@object: WorldViewNames.ViewOf(instance: WorldViewGraphs.WorldInstance));
        Assert.Null(@object: WorldViewNames.ViewOf(instance: "world$1"));
        Assert.Null(@object: WorldViewNames.ViewOf(instance: "world$02"));
        Assert.Equal(
            actual: WorldRootGraph.ViewsOf(views: new WorldViewDefaults()),
            expected: PlayerRoster.MaxSlots
        );
    }
}
