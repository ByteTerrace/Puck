using Xunit;

using Puck.Testing;
using Puck.World.Server;

namespace Puck.World.Tests;

/// <summary>
/// Laws for the <c>views.graphs</c> section: each refusal beside a one-value-different control that validates, a loop
/// of same-frame inputs refused naming every instance in it while a self-read and a previous-frame read validate, and
/// the server pricing each row's graph by planning its source.
/// </summary>
public sealed class WorldViewGraphLawTests {
    private static WorldDefinition Document(params WorldViewGraph[] graphs) {
        var document = Fixtures.BuildDocument();

        return (document with { ViewsRaw = (document.Views with { Graphs = graphs }) });
    }
    private static WorldViewGraph Row(string name, WorldViewGraphRefresh? refresh = null, WorldViewGraphInput[]? inputs = null) => new(
        Inputs: inputs,
        Name: name,
        Refresh: refresh,
        Source: "graphs/view.graph.json"
    );
    private static void Refuses(WorldDefinition denied, WorldDefinition control, string expected) {
        Assert.False(condition: WorldDefinitionValidator.TryValidateLocally(
            definition: denied,
            reason: out var reason
        ));
        Assert.Contains(
            actualString: reason,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: expected
        );
        Assert.True(
            condition: WorldDefinitionValidator.TryValidateLocally(
                definition: control,
                reason: out var controlReason
            ),
            userMessage: controlReason
        );
    }

    [Fact]
    public void ASameFrameLoopIsRefusedNamingBothInstancesAndAPreviousFrameReadBreaksIt() => Refuses(
        control: Document(
            Row(
                inputs: [new WorldViewGraphInput(Instance: "west", Resource: "feed")],
                name: "east"
            ),
            Row(
                inputs: [new WorldViewGraphInput(Instance: "east", PreviousFrame: true, Resource: "feed")],
                name: "west"
            ),
            Row(
                inputs: [new WorldViewGraphInput(Instance: "mirror", Resource: "feed")],
                name: "mirror"
            )
        ),
        denied: Document(
            Row(
                inputs: [new WorldViewGraphInput(Instance: "west", Resource: "feed")],
                name: "east"
            ),
            Row(
                inputs: [new WorldViewGraphInput(Instance: "east", Resource: "feed")],
                name: "west"
            )
        ),
        expected: "east -> west -> east"
    );
    [Fact]
    public void AnInputNamingNoRowIsRefused() => Refuses(
        control: Document(
            Row(name: "security"),
            Row(
                inputs: [new WorldViewGraphInput(Instance: "security", Resource: "feed")],
                name: "monitor"
            )
        ),
        denied: Document(Row(
            inputs: [new WorldViewGraphInput(Instance: "security", Resource: "feed")],
            name: "monitor"
        )),
        expected: "views.graphs[0].inputs[0].instance 'security' names no views.graphs row."
    );
    [Fact]
    public void ARefreshNamesExactlyOnePositiveRate() {
        Refuses(
            control: Document(Row(
                name: "security",
                refresh: new WorldViewGraphRefresh(Hertz: 15)
            )),
            denied: Document(Row(
                name: "security",
                refresh: new WorldViewGraphRefresh(Divisor: 2, Hertz: 15)
            )),
            expected: "views.graphs[0].refresh must author exactly one of divisor or hertz"
        );
        Refuses(
            control: Document(Row(
                name: "security",
                refresh: new WorldViewGraphRefresh(Divisor: 2)
            )),
            denied: Document(Row(
                name: "security",
                refresh: new WorldViewGraphRefresh(Divisor: 0)
            )),
            expected: "views.graphs[0].refresh must author exactly one of divisor or hertz"
        );
    }
    [Fact]
    public void ANameIsUniqueAndAnInputBindsEachResourceOnce() {
        Refuses(
            control: Document(
                Row(name: "security"),
                Row(name: "lobby")
            ),
            denied: Document(
                Row(name: "security"),
                Row(name: "security")
            ),
            expected: "views.graphs[1].name 'security' is duplicated."
        );
        Refuses(
            control: Document(
                Row(name: "security"),
                Row(
                    inputs: [new WorldViewGraphInput(Instance: "security", Resource: "left"), new WorldViewGraphInput(Instance: "security", Resource: "right")],
                    name: "monitor"
                )
            ),
            denied: Document(
                Row(name: "security"),
                Row(
                    inputs: [new WorldViewGraphInput(Instance: "security", Resource: "left"), new WorldViewGraphInput(Instance: "security", Resource: "left")],
                    name: "monitor"
                )
            ),
            expected: "views.graphs[1].inputs[1].resource 'left' is bound more than once."
        );
    }
    [Fact]
    public void ARowNamesExactlyOneOfASourceAndAPackageAndAPackageReadsNothing() {
        Refuses(
            control: Document(Row(name: "security")),
            denied: Document(Row(name: "security") with { Package = "sdf.world" }),
            expected: "views.graphs[0] must author exactly one of source and package."
        );
        Refuses(
            control: Document(Row(name: "security")),
            denied: Document(Row(name: "security") with { Source = null }),
            expected: "views.graphs[0] must author exactly one of source and package."
        );
        Refuses(
            control: Document(
                Row(name: "security"),
                new WorldViewGraph(Name: "scene", Package: "sdf.world")
            ),
            denied: Document(
                Row(name: "security"),
                new WorldViewGraph(Inputs: [new WorldViewGraphInput(Instance: "security", Resource: "feed")], Name: "scene", Package: "sdf.world")
            ),
            expected: "views.graphs[1].inputs: package instance 'scene' renders through its producer, which reads no input."
        );
    }
    [Fact]
    public void ASynthesizedNameIsTheWorldsOnlyWhenItNamesItsRoot() {
        var authored = Document(
            new WorldViewGraph(Name: WorldViewGraphs.WorldInstance, Package: "sdf.world"),
            Row(
                inputs: [new WorldViewGraphInput(Instance: WorldViewGraphs.WorldInstance, Resource: "feed")],
                name: WorldViewGraphs.MainInstance
            )
        );

        Refuses(
            control: (authored with { ViewsRaw = (authored.Views with { Root = WorldViewGraphs.MainInstance }) }),
            denied: authored,
            expected: "views.graphs[0].name 'world' is an instance of the render graph composition synthesizes"
        );
        Refuses(
            control: (authored with { ViewsRaw = (authored.Views with { Root = WorldViewGraphs.MainInstance }) }),
            denied: (authored with { ViewsRaw = (authored.Views with { Root = "absent" }) }),
            expected: "views.root 'absent' names no views.graphs row."
        );
    }
    // The synthesized root declares its own versions and passes in the generated form, so a row may take any name the
    // root once used for them (frame, stage1, overlay), and no row may take the generated form itself.
    [Fact]
    public void ARowNameIsRefusedInTheGeneratedFormTheRootDeclaresItsOwnNamesIn() => Refuses(
        control: Document(Row(name: "frame")),
        denied: Document(Row(name: GeneratedName.Join("main", "frame"))),
        expected: "views.graphs[0].name 'main$frame' carries '$' inside it"
    );
    [Fact]
    public void ASlotShowsAGraphRowByItsName() {
        var document = Document(Row(name: "security"));

        WorldDefinition Slot(string instance) => (document with {
            ViewsRaw = (document.Views with {
                Layouts = [new WorldViewLayout(
                    Name: "panes",
                    Slots: [new WorldViewSlot() with { Instance = instance }]
                )],
            }),
        });

        Refuses(
            control: Slot(instance: "security"),
            denied: Slot(instance: "lobby"),
            expected: "views.layouts[0].slots[0].instance 'lobby' names no views.graphs row."
        );
    }
    [Fact]
    public void TheServerPricesEachGraphByPlanningItsSource() {
        using var directory = new TemporaryDirectory();

        directory.WriteText(
            name: "graphs/view.graph.json",
            text: """
                {
                  "$schema": "puck.render.graph.v1",
                  "name": "view",
                  "resources": [
                    { "name": "feed", "format": "R8G8B8A8Unorm", "dimensions": { "mode": "Relative", "width": 1, "height": 1 }, "initialization": "External" },
                    { "name": "scene", "format": "R8G8B8A8Unorm", "dimensions": { "mode": "Relative", "width": 1, "height": 1 } },
                    { "name": "final", "format": "R8G8B8A8Unorm", "dimensions": { "mode": "Relative", "width": 1, "height": 1 } }
                  ],
                  "passes": [
                    { "name": "mix", "source": "mix.hlsl", "entryPoint": "main", "kind": "Compute", "inputs": [{ "name": "scene" }, { "name": "feed" }], "outputs": [{ "name": "final" }] }
                  ],
                  "packages": [
                    { "name": "world", "package": "sdf.world", "outputs": [{ "name": "scene" }] }
                  ],
                  "outputs": ["final"]
                }
                """
        );

        var sources = new WorldPipelineSources(documentDirectory: directory.RootPath);
        var definition = Document(
            Row(
                name: "security",
                refresh: new WorldViewGraphRefresh(Divisor: 4)
            ),
            Row(
                inputs: [new WorldViewGraphInput(Instance: "security", Resource: "screen")],
                name: "monitor"
            ),
            (Row(name: "missing") with { Source = "graphs/absent.graph.json" })
        );
        var presentation = WorldPresentationCost.Measure(
            definition: definition,
            passes: sources.PlanGraph
        );

        Assert.Equal(expected: (((int?)2), ((string?)null)), actual: (presentation.Instances[0].Passes, presentation.Instances[0].Issue));
        Assert.Equal(expected: 0.5, actual: presentation.Instances[0].PassDisplaysPerFrame);
        Assert.Equal(expected: 2, actual: presentation.Instances[1].Passes);
        Assert.Contains(
            expectedSubstring: "screen name no external version",
            actualString: presentation.Instances[1].Issue
        );
        Assert.Null(@object: presentation.Instances[2].Passes);
        Assert.Contains(
            expectedSubstring: "security (graphs/view.graph.json) extent<=display rate 1/4 frames passes 2 <= 0.5 display pass-pixels/frame",
            actualString: presentation.Describe()
        );
    }
}
