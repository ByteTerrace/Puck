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
                name: "main"
            )
        ),
        denied: Document(Row(
            inputs: [new WorldViewGraphInput(Instance: "security", Resource: "feed")],
            name: "main"
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
                    name: "main"
                )
            ),
            denied: Document(
                Row(name: "security"),
                Row(
                    inputs: [new WorldViewGraphInput(Instance: "security", Resource: "left"), new WorldViewGraphInput(Instance: "security", Resource: "left")],
                    name: "main"
                )
            ),
            expected: "views.graphs[1].inputs[1].resource 'left' is bound more than once."
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
                name: "main"
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
