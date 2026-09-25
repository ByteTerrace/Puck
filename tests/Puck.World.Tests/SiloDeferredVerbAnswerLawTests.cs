using Microsoft.Extensions.DependencyInjection;

using Puck.Commands;
using Puck.World.Protocol;
using Puck.World.Silo;

using Xunit;

using static Puck.World.Tests.DeferredVerbEvictionAnswerLawTests;

namespace Puck.World.Tests;

/// <summary>Laws for the silo's console: it attaches the same deferred-verb answers the World host does
/// (<see cref="WorldSiloApplication.AnswerDeferredVerbs"/>) and taps each admitted row's echoes into them
/// (<see cref="WorldSiloHost.TapEchoes"/>), so a late verdict prints, and a late refusal and an eviction print and count.</summary>
[Collection(name: ConsoleRedirectionCollection.Name)]
public sealed class SiloDeferredVerbAnswerLawTests {
    // What the silo does with each row it admits.
    private static void TapRow(WorldDeferredVerbAnswers answers, WorldFixture fixture) => WorldSiloHost.TapEchoes(answers: answers, server: fixture.Server);

    // A typed verb's refusal arrives a tick after its line returned, so its dispatch never counted it: the silo counts
    // it through the same answers the World host taps its server's echoes into.
    [Fact]
    public void TheSiloCountsALateRefusal() {
        using var fixture = Fixtures.FreshServer();
        var link = new LoopbackTransport(server: fixture.Server);
        using var services = new ServiceCollection()
            .AddSingleton<WorldDeferredVerbEchoes>()
            .AddSingleton(implementationInstance: new CommandRegistry(modules: []))
            .BuildServiceProvider();
        var echoes = services.GetRequiredService<WorldDeferredVerbEchoes>();
        var registry = services.GetRequiredService<CommandRegistry>();
        var answers = WorldSiloApplication.AnswerDeferredVerbs(services: services);

        TapRow(answers: answers, fixture: fixture);

        var late = Captured(action: () => {
            var submitted = link.Submit(
                echoes: echoes,
                mutation: new WorldMutation.RemoveKit(Principal.Console, "absentKit"),
                verb: "world.kit.remove"
            );

            Assert.NotNull(@object: submitted.Settlement);
            fixture.Step();
        });

        Assert.Contains(actualString: late.Error, comparisonType: StringComparison.Ordinal, expectedSubstring: "[world.kit.remove: ");
        Assert.Equal(actual: WireErrors(registry: registry), expected: "[wire.errors: 1 rejected]");
    }
    [Fact]
    public void TheSiloPrintsLateVerdictsAndCountsEvictions() {
        using var fixture = Fixtures.FreshServer();
        var link = new LoopbackTransport(server: fixture.Server);
        using var services = new ServiceCollection()
            .AddSingleton<WorldDeferredVerbEchoes>()
            .AddSingleton(implementationInstance: new CommandRegistry(modules: []))
            .BuildServiceProvider();
        var echoes = services.GetRequiredService<WorldDeferredVerbEchoes>();
        var registry = services.GetRequiredService<CommandRegistry>();

        _ = WorldSiloApplication.AnswerDeferredVerbs(services: services);

        var late = Captured(action: () => {
            var submitted = link.Submit(
                echoes: echoes,
                mutation: new WorldMutation.UpsertStateRow(
                    Principal: Principal.Console,
                    Row: new WorldStateRow(Name: CellName.Parse(candidate: "siloProbe"), Kind: CellKind.Int)
                ),
                verb: "world.state.row.set"
            );

            Assert.NotNull(@object: submitted.Settlement);
            fixture.Step();
        });

        Assert.Contains(actualString: late.Out, comparisonType: StringComparison.Ordinal, expectedSubstring: "[world.state.row.set: ");

        var evicted = Captured(action: () => EvictTheFirst(echoes: echoes));

        Assert.StartsWith(actualString: evicted.Error, expectedStartString: $"[{EvictedVerb}: evicted unanswered");
        Assert.Equal(actual: WireErrors(registry: registry), expected: "[wire.errors: 1 rejected]");
    }
}
