using Microsoft.Extensions.DependencyInjection;
using Puck.Abstractions.Counting;
using Puck.Commands;
using Puck.World.Protocol;

using Xunit;

namespace Puck.World.Tests;

/// <summary>
/// CONTRACT UNDER TEST: the server's own counters — <c>state.arena</c>, <c>state.rules</c>, <c>state.search</c> — are
/// registered for <c>world.counters</c>, and a reading never goes down across a definition rebuild
/// (<c>world.reload</c>'s in-process path), although the rebuild replaces the arena and the search behind them: the
/// retired instances' totals are carried forward exactly once.
/// </summary>
public sealed class WorldServerCounterSourceLawTests {
    private static CellName Name(string text) => CellName.Parse(candidate: text);
    // A pooled state section, so an install adopts a freshly built arena (and a fresh search over it) rather than
    // reloading the live one in place.
    private static WorldDefinition PooledDocument() {
        var document = Fixtures.BuildDocument();

        return document with {
            StateRaw = document.StateRaw! with {
                Records = [new StateRecord(Name: Name(text: "actor"), Fields: [
                    new StatePoolField(Name: Name(text: "score"), Default: CellValue.Int(value: 7)),
                ])],
                Pools = [new StatePool(Name: Name(text: "actors"), Record: Name(text: "actor"), Capacity: 3, Initial: [new StatePoolSeed(Slot: 0), new StatePoolSeed(Slot: 1)])],
            },
            Rules = [new WorldRule(
                Name: Name(text: "remove"), Mode: ActionTriggerMode.Edge,
                Effects: [new ActionEffect.ForEachPool(Pool: "actors", Binding: Name(text: "x"), Effects: [new ActionEffect.Release(Binding: Name(text: "x"))])]
            )],
        };
    }
    private static long[] Read(IWorkCounterSource source) {
        var kinds = source.WorkKinds.ToArray();
        var values = new long[kinds.Length];

        for (var index = 0; (index < kinds.Length); index++) {
            Assert.True(condition: source.TryRead(kind: kinds[index], value: out values[index]));
        }

        return values;
    }
    private static void Reload(WorldFixture fixture) {
        var candidate = fixture.Server.Definition;

        fixture.Server.EnqueueRebuild(
            principal: Principal.Console,
            request: new WorldRebuildRequest(
                ContentHash: WorldDefinitionFileSource.ComputeContentHash(content: WorldDefinitionSerialization.Serialize(definition: candidate)),
                Definition: candidate,
                Force: true,
                Kind: WorldRebuildKind.Reload,
                PathHint: "counter-source-reload-probe.world.json"
            )
        );
        fixture.Step();
    }

    [Fact]
    public void ARebuildCarriesTheRetiredArenaAndSearchForward() {
        using var fixture = Fixtures.FreshServer(definition: PooledDocument());

        fixture.Step();
        fixture.Step();

        var server = fixture.Server;
        var retiredArena = server.Arena;
        var retiredSearch = server.Search;
        var arenaBefore = Read(source: server.ArenaWorkSource);
        var rulesBefore = Read(source: server.RuleHost.RuleWork);
        var searchBefore = Read(source: server.SearchWorkSource);

        Reload(fixture: fixture);

        // The rebuild replaced both instances; read directly, the new ones start over.
        Assert.NotSame(expected: retiredArena, actual: server.Arena);
        Assert.NotSame(expected: retiredSearch, actual: server.Search);

        var arenaAfter = Read(source: server.ArenaWorkSource);
        var retiredArenaTotals = Read(source: retiredArena);
        var liveArenaTotals = Read(source: server.Arena);
        var searchAfter = Read(source: server.SearchWorkSource);
        var retiredSearchTotals = Read(source: retiredSearch);
        var liveSearchTotals = Read(source: server.Search);

        Assert.Contains(collection: arenaBefore, filter: static value => (value > 0L));

        for (var index = 0; (index < arenaAfter.Length); index++) {
            Assert.True(condition: (arenaAfter[index] >= arenaBefore[index]), userMessage: $"{ArenaWork.Kinds[index].Name} went down across the rebuild");
            Assert.Equal(actual: arenaAfter[index], expected: (retiredArenaTotals[index] + liveArenaTotals[index]));
        }

        for (var index = 0; (index < searchAfter.Length); index++) {
            Assert.True(condition: (searchAfter[index] >= searchBefore[index]));
            Assert.Equal(actual: searchAfter[index], expected: (retiredSearchTotals[index] + liveSearchTotals[index]));
        }

        Assert.True(condition: Read(source: server.RuleHost.RuleWork).Zip(second: rulesBefore).All(predicate: static pair => (pair.First >= pair.Second)));
    }
    [Fact]
    public void WorldCountersShowsTheServerSections() {
        using var fixture = Fixtures.FreshServer(definition: PooledDocument());
        var services = new ServiceCollection();

        _ = services.AddSingleton(implementationInstance: fixture.Server);
        _ = services.AddWorldServerCounters();
        _ = services.AddWorldCounters();

        using var provider = services.BuildServiceProvider();
        var result = new CommandRegistry(modules: provider.GetServices<ICommandModule>()).Submit(line: "world.counters state");

        Assert.False(condition: result.IsError, userMessage: result.Output);
        Assert.StartsWith(expectedStartString: "[world.counters: state.arena\nstate.arena.visits ", actualString: result.Output);
        Assert.Contains(expectedSubstring: "\nstate.rules\nstate.rules.evaluations ", actualString: result.Output);
        Assert.Contains(expectedSubstring: "\nstate.search\nstate.search.candidates ", actualString: result.Output);
    }
}
