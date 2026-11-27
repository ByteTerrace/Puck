using Puck.Commands;
using Puck.World.Protocol;
using Xunit;

namespace Puck.World.Server.Tests;

/// <summary>What a product was built from is a function of the document, never of which caller derived a shared value
/// first (<see cref="WorldDocumentDerivedTable{TValue}"/>): an install plans the same rebuilds live and in a replay
/// whose restore built its products from a fresh document.</summary>
public sealed class DocumentDerivedReadsLawTests {
    [Fact]
    public void APopulationReadsTheSamePartsWhetherItsInputCatalogWasHeldOrNot() {
        using var fixture = Fixtures.FreshServer(definition: Fixtures.BuildDocument());
        var definition = fixture.Server.Definition;
        var held = new WorldPopulation(definition: definition).BuiltFrom;
        var fresh = new WorldPopulation(definition: (definition with { })).BuiltFrom;
        var rebuilt = new WorldPopulation(definition: definition);

        rebuilt.Rebuild(definition: definition, solids: null);
        Assert.True(condition: held.Contains(input: WorldDocumentInput.InputModes), userMessage: $"the population read {held}");
        Assert.Equal(expected: fresh, actual: held);
        Assert.Equal(expected: held, actual: rebuilt.BuiltFrom);
        Assert.Equal(expected: held, actual: fixture.Server.Population.BuiltFrom);
    }
    [Fact]
    public void AnInputModesEditReconfiguresInputAndReproduces() {
        using var run = new WorldHistoryHarness(on: false);
        var server = run.Fixture.Server;

        Assert.Null(server.Definition.InputModes);
        using var probe = run.Tape.ObserveReplay();

        run.Steps(count: 3);
        var revision = (server.Input.TryGet(input: out var before, principal: Principal.Seat(slot: 0)) ? before!.Revision : 0U);

        run.Submit(mutation: new WorldMutation.SetInputModes(Principal: Principal.Console,
            Modes: new WorldInputModes(Global: [.. server.Definition.Channels.Select(selector: static channel => channel.Name)], Families: [])));
        run.Steps(count: 6);
        Assert.True(condition: server.Input.TryGet(input: out var after, principal: Principal.Seat(slot: 0)));
        Assert.True(condition: (after!.Revision > revision), userMessage: "the edit left the seat's input owner as it was");
        probe.Dispose();
        Assert.Equal(expected: -1, actual: probe.Verify());
    }
}
