using Puck.Commands;
using Puck.World.Protocol;
using Xunit;

namespace Puck.World.Server.Tests;

public sealed class ScratchInputDebugExitTests {
    [Fact]
    public void APopulationRebuildingMutationReproduces() {
        using var run = new WorldHistoryHarness(on: false);
        using var probe = run.Tape.ObserveReplay();

        run.Steps(count: 3);
        run.Submit(mutation: new WorldMutation.UpsertKit(Principal: Principal.Console, Kit: run.Fixture.Server.Definition.Kits[0] with { Mass = 1f }));
        run.Steps(count: 6);
        probe.Dispose();
        Assert.Equal(-1, probe.Verify());
        Assert.Null(probe.Divergence);
    }
    [Fact]
    public void APopulationReadsTheSamePartsWarmOrCold() {
        using var fixture = Fixtures.FreshServer(definition: Fixtures.BuildDocument());
        var definition = fixture.Server.Definition;
        var warm = new WorldPopulation(definition: definition).BuiltFrom;
        var cold = new WorldPopulation(definition: (definition with { })).BuiltFrom;

        Assert.Equal(expected: cold.ToString(), actual: warm.ToString());
        var population = new WorldPopulation(definition: definition);

        population.Rebuild(definition: definition, solids: null);
        Assert.Equal(expected: warm.ToString(), actual: population.BuiltFrom.ToString());
        Assert.Equal(expected: warm.ToString(), actual: fixture.Server.Population.BuiltFrom.ToString());
    }
    [Fact]
    public void AnInputModesEditReproduces() {
        using var run = new WorldHistoryHarness(on: false);
        var server = run.Fixture.Server;
        var bootPopulation = server.Population.BuiltFrom;
        var bootInput = server.Input.BuiltFrom;

        Assert.Null(server.Definition.InputModes);
        using var probe = run.Tape.ObserveReplay();

        run.Steps(count: 3);
        run.Submit(mutation: new WorldMutation.SetInputModes(Principal: Principal.Console,
            Modes: new WorldInputModes(Global: [.. server.Definition.Channels.Select(selector: static channel => channel.Name)], Families: [])));
        run.Steps(count: 6);
        probe.Dispose();
        var verdict = probe.Verify();

        Assert.True(condition: bootPopulation.Contains(input: WorldDocumentInput.InputModes), userMessage: $"boot population read {bootPopulation}");
        Assert.True(condition: bootInput.Contains(input: WorldDocumentInput.InputModes) || bootPopulation.Contains(input: WorldDocumentInput.InputModes), userMessage: $"boot input read {bootInput}");
        Assert.Equal(-1, verdict);
    }
}
