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
    // The letterbox and tonemap switches a place pass binds, or zero for any other pass.
    private static (uint Letterbox, uint Tonemap) Fields(ShaderPipelinePlannedPass pass) {
        if (pass.Package?.Package != RenderGraphPackageCatalog.Place) {
            return (0u, 0u);
        }

        Assert.True(condition: pass.Parameters.TryBind(
            config: null,
            reason: out var reason,
            values: out var values
        ), userMessage: reason);

        var bytes = values.Bytes.Span;

        return (
            BinaryPrimitives.ReadUInt32LittleEndian(source: bytes[((int)pass.Parameters.BlockOffsetOf(member: RenderGraphPackageCatalog.PlaceLetterbox))..]),
            BinaryPrimitives.ReadUInt32LittleEndian(source: bytes[((int)pass.Parameters.BlockOffsetOf(member: RenderGraphPackageCatalog.PlaceTonemap))..])
        );
    }
    private static string[] PlaceFields(RenderGraphPlan plan) => [.. plan.Steps
        .Where(predicate: static step => (step.Package?.Id == RenderGraphPackageCatalog.Place))
        .Select(selector: static step => {
            var (letterbox, tonemap) = Fields(pass: step.Planned);

            return $"{step.Name}:letterbox={letterbox},tonemap={tonemap}";
        })];

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
            expected: (WorldViewGraphs.WorldInstance, WorldViewGraphs.WorldInstance, RenderGraphPackageCatalog.SdfWorld, SdfWorldPackage.NativeFragment.Passes.Count)
        );
        Assert.Empty(collection: graph.Footprints);
        Assert.Null(@object: Assert.Single(collection: graph.Graphs()));
    }
    // A pane is display-referred, its own shader's tonemap included (the moth studio's pane applies its own filmic curve),
    // so the tonemap is each view's place pass, over the view it reconstructs: no pane, post pass or overlay is tonemapped.
    [Fact]
    public void AFilmicTonemapIsEachViewsPlacePassAndNoPanePostPassOrOverlayIsTonemapped() {
        WorldViewPostPass[] post = [new(Name: "grain", Package: FilmGrain)];
        var filmic = WorldRootGraph.Compose(
            overlay: true,
            packages: RenderGraphPackageCatalog.Engine,
            panes: ["moth-pipeline"],
            post: post,
            tonemap: WorldTonemap.Filmic,
            views: 2
        );
        var plan = Assert.IsType<RenderGraphPlan>(@object: filmic.Plan);

        Assert.Equal(
            actual: plan.Steps.Select(selector: static step => $"{step.Name}:{step.Package?.Id}"),
            expected: ["main$view$1:place", "main$view$2:place", "moth-pipeline:place", $"grain:{FilmGrain}", "main$overlay:overlay"]
        );
        Assert.Equal(
            actual: PlaceFields(plan: plan),
            expected: ["main$view$1:letterbox=1,tonemap=1", "main$view$2:letterbox=0,tonemap=1", "moth-pipeline:letterbox=0,tonemap=0"]
        );
        Assert.Equal(
            actual: filmic.Tonemap,
            expected: WorldTonemap.Filmic
        );

        // With one view, a tonemap is the reason the root places it: the view's place pass reads the world's version as its
        // base and the view's own as its source, and its footprint follows its rect.
        var single = WorldRootGraph.Compose(
            overlay: false,
            packages: RenderGraphPackageCatalog.Engine,
            post: null,
            tonemap: WorldTonemap.Filmic
        );
        var singlePlan = Assert.IsType<RenderGraphPlan>(@object: single.Plan);
        var view = Assert.Single(collection: singlePlan.Steps).Planned;

        Assert.Equal(
            actual: (single.Root, Views: string.Join(separator: ",", values: single.ViewPasses), Base: view.Inputs[0].Name, Source: view.Inputs[1].Name, Footprints: single.Footprints.Count),
            expected: (WorldViewGraphs.MainInstance, Views: "main$view$1", Base: "main$world", Source: "main$view$1", Footprints: 0)
        );
        Assert.Equal(
            actual: PlaceFields(plan: singlePlan),
            expected: ["main$view$1:letterbox=1,tonemap=1"]
        );

        // None tonemaps nothing, so a world with nothing to draw over it stays its own root.
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
    /// <summary>The letterbox color is display framing, not scene light, so no tonemap reaches it: a pass that tonemaps
    /// (a place pass with <see cref="RenderGraphPackageCatalog.PlaceTonemap"/> set, which tonemaps the source it
    /// reconstructs and nothing else) never reconstructs an image the letterbox was written into, so the letterbox code
    /// reaches the display exact under a filmic tonemap with any number of views.</summary>
    [Fact]
    public void NoTonemapReadsTheLetterboxSoAFilmicFrameShowsItsCodeExact() {
        foreach (var views in ((int[])[2, 4])) {
            var plan = Assert.IsType<RenderGraphPlan>(@object: WorldRootGraph.Compose(
                overlay: true,
                packages: RenderGraphPackageCatalog.Engine,
                panes: ["pane"],
                post: [new WorldViewPostPass(Name: "grain", Package: FilmGrain)],
                tonemap: WorldTonemap.Filmic,
                views: views
            ).Plan);
            // Every version the letterbox color is in: what a letterboxing pass writes, and what any pass reading one of
            // those writes.
            var framed = new HashSet<string>(comparer: StringComparer.Ordinal);
            var letterboxed = 0;
            var tonemapped = new List<string>();

            foreach (var step in plan.Steps) {
                var fields = Fields(pass: step.Planned);

                if (fields.Letterbox == 1u) {
                    letterboxed++;
                }
                if (fields.Tonemap == 1u) {
                    // A tonemapping place pass puts its second input, the source it reconstructs, through the curve.
                    var source = step.Planned.Inputs[1].Name;

                    Assert.False(
                        condition: framed.Contains(item: source),
                        userMessage: $"{views} views: {step.Name} tonemaps {source}, which holds the letterbox color"
                    );
                    tonemapped.Add(item: step.Name);
                }
                if ((fields.Letterbox == 1u) || step.Planned.Inputs.Any(predicate: input => framed.Contains(item: input.Name))) {
                    framed.UnionWith(other: step.Planned.Outputs.Select(selector: static output => output.Name));
                }
            }

            Assert.Equal(
                actual: (Letterboxed: letterboxed, Tonemapped: tonemapped.Count),
                expected: (Letterboxed: 1, Tonemapped: views)
            );
        }
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
