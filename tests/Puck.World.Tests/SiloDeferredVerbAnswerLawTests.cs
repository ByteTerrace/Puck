using Microsoft.Extensions.DependencyInjection;

using Puck.Commands;
using Puck.World.Protocol;
using Puck.World.Server;
using Puck.World.Silo;

using Xunit;

using static Puck.World.Tests.DeferredVerbEvictionAnswerLawTests;

namespace Puck.World.Tests;

/// <summary>Laws for the silo's console: it attaches the same deferred-verb answers the World host does
/// (<see cref="WorldSiloApplication.AnswerDeferredVerbs"/>) and taps each admitted row's echoes into them under the row's
/// name (<see cref="WorldSiloHost.TapEchoes"/>), so a late verdict prints and a late refusal of a console line counts,
/// while a remote peer's refusal, the silo's own refused reload through the bare transport, an eviction, and another
/// row's echo with the same correlation count nothing.</summary>
[Collection(name: ConsoleRedirectionCollection.Name)]
public sealed class SiloDeferredVerbAnswerLawTests {
    private const string Row = "row";

    // What the silo does with each row it admits: taps its echoes under its name, and gives the console a link to it.
    private static (LoopbackTransport Bare, IServerLink Console) TapRow(WorldDeferredVerbAnswers answers, WorldFixture fixture, string row = Row) {
        var bare = new LoopbackTransport(server: fixture.Server);

        WorldSiloHost.TapEchoes(
            answers: answers,
            row: row,
            server: fixture.Server
        );

        return (bare, bare.ForConsole(row: answers.Echoes.ForRow(row: row)));
    }

    // A typed verb's refusal arrives a tick after its line returned, so its dispatch never counted it: the silo counts
    // it through the same answers the World host taps its server's echoes into.
    [Fact]
    public void TheSiloCountsALateRefusal() {
        using var fixture = Fixtures.FreshServer();
        using var services = new ServiceCollection()
            .AddSingleton<WorldDeferredVerbEchoes>()
            .AddSingleton(implementationInstance: new CommandRegistry(modules: []))
            .BuildServiceProvider();
        var echoes = services.GetRequiredService<WorldDeferredVerbEchoes>();
        var registry = services.GetRequiredService<CommandRegistry>();
        var answers = WorldSiloApplication.AnswerDeferredVerbs(services: services);

        var (bare, link) = TapRow(answers: answers, fixture: fixture);

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
    // wire.errors counts the local console's lines only: a remote peer's envelope refused a tick later is that peer's
    // answer, not a line this console submitted. The control, a refused local line in the same run, still counts once.
    [Fact]
    public void TheSiloCountsNoRemoteConnectionsRefusal() {
        const int RemoteConnection = 7;

        using var fixture = Fixtures.FreshServer();
        using var services = new ServiceCollection()
            .AddSingleton<WorldDeferredVerbEchoes>()
            .AddSingleton(implementationInstance: new CommandRegistry(modules: []))
            .BuildServiceProvider();
        var echoes = services.GetRequiredService<WorldDeferredVerbEchoes>();
        var registry = services.GetRequiredService<CommandRegistry>();
        var answers = WorldSiloApplication.AnswerDeferredVerbs(services: services);
        var remoteRefusals = 0;

        var (bare, link) = TapRow(answers: answers, fixture: fixture);
        fixture.Server.EchoTap += echo => {
            if (
                echo.Rejected &&
                (echo.ConnectionId == RemoteConnection)
            ) {
                remoteRefusals++;
            }
        };

        var remote = Captured(action: () => {
            fixture.Server.EnqueueMutation(
                connectionId: RemoteConnection,
                correlationId: 1L,
                mutation: new WorldMutation.RemoveKit(Principal.Console, "absentKit")
            );
            fixture.Step();
        });

        Assert.Equal(actual: remoteRefusals, expected: 1);
        Assert.Empty(collection: remote.Out);
        Assert.Equal(actual: WireErrors(registry: registry), expected: "[wire.errors: 0 rejected]");

        _ = Captured(action: () => {
            _ = link.Submit(
                echoes: echoes,
                mutation: new WorldMutation.RemoveKit(Principal.Console, "absentKit"),
                verb: "world.kit.remove"
            );
            fixture.Step();
        });

        Assert.Equal(actual: WireErrors(registry: registry), expected: "[wire.errors: 1 rejected]");
    }
    // The silo's reload submits its rebuild on the row's bare transport, which registers no line, as
    // WorldSiloHost.ReloadAsync does, and answers its refusal on the reload's own reply, so wire.errors does not count
    // it. The control, the same refused rebuild submitted as a console line, counts once.
    [Fact]
    public void TheSiloCountsNoRefusedReload() {
        using var fixture = Fixtures.FreshServer();
        using var services = new ServiceCollection()
            .AddSingleton<WorldDeferredVerbEchoes>()
            .AddSingleton(implementationInstance: new CommandRegistry(modules: []))
            .BuildServiceProvider();
        var echoes = services.GetRequiredService<WorldDeferredVerbEchoes>();
        var registry = services.GetRequiredService<CommandRegistry>();
        var answers = WorldSiloApplication.AnswerDeferredVerbs(services: services);
        var refusedRebuilds = 0;
        // Refused at the tick boundary: an ungranted addon principal holds Mutate over no section.
        var refused = Principal.Addon(name: "unGranted");
        var reloadRequest = new WorldRebuildRequest(
            ContentHash: "published",
            Definition: fixture.Server.Definition,
            Force: false,
            Kind: WorldRebuildKind.Reload,
            PathHint: "hosted/row"
        );

        var (bare, link) = TapRow(answers: answers, fixture: fixture);
        fixture.Server.EchoTap += echo => {
            if (
                echo.Rejected &&
                (echo.Kind == WorldEditEchoKind.Rebuild)
            ) {
                refusedRebuilds++;
            }
        };

        var reload = Captured(action: () => {
            string? transport = null;

            Assert.True(
                condition: (bare.SubmitEnvelope(
                    completion: result => transport = result.ToString(),
                    operationId: Guid.Empty,
                    payload: new WorldSubmissionPayload.Rebuild(Value: reloadRequest),
                    principal: refused
                ) != 0L),
                userMessage: transport
            );
            fixture.Step();
        });

        Assert.Equal(actual: refusedRebuilds, expected: 1);
        Assert.Empty(collection: reload.Out);
        Assert.Equal(actual: WireErrors(registry: registry), expected: "[wire.errors: 0 rejected]");

        var line = Captured(action: () => {
            _ = link.SubmitRebuild(
                echoes: echoes,
                principal: refused,
                request: reloadRequest,
                verb: "world.reload"
            );
            fixture.Step();
        });

        Assert.Equal(actual: refusedRebuilds, expected: 2);
        Assert.Contains(actualString: line.Error, comparisonType: StringComparison.Ordinal, expectedSubstring: "[world.reload: ");
        Assert.Equal(actual: WireErrors(registry: registry), expected: "[wire.errors: 1 rejected]");
    }
    [Fact]
    public void TheSiloPrintsLateVerdictsAndCountsNoEviction() {
        using var fixture = Fixtures.FreshServer();
        using var services = new ServiceCollection()
            .AddSingleton<WorldDeferredVerbEchoes>()
            .AddSingleton(implementationInstance: new CommandRegistry(modules: []))
            .BuildServiceProvider();
        var echoes = services.GetRequiredService<WorldDeferredVerbEchoes>();
        var registry = services.GetRequiredService<CommandRegistry>();

        var (_, link) = TapRow(answers: WorldSiloApplication.AnswerDeferredVerbs(services: services), fixture: fixture);

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

        Assert.Equal(actual: (evicted.Out, evicted.Error), expected: (string.Empty, string.Empty));
        Assert.Equal(actual: WireErrors(registry: registry), expected: "[wire.errors: 0 rejected]");
    }
    // One table serves every row, but correlations are per-row link: two rows mint the same correlation for their own
    // console lines, and one row's verdict answers only its own line, however the other row's tick falls.
    [Fact]
    public void AnotherRowsEchoWithTheSameCorrelationAnswersNoLine() {
        using var first = Fixtures.FreshServer();
        using var second = Fixtures.FreshServer();
        using var services = new ServiceCollection()
            .AddSingleton<WorldDeferredVerbEchoes>()
            .AddSingleton(implementationInstance: new CommandRegistry(modules: []))
            .BuildServiceProvider();
        var echoes = services.GetRequiredService<WorldDeferredVerbEchoes>();
        var registry = services.GetRequiredService<CommandRegistry>();
        var answers = WorldSiloApplication.AnswerDeferredVerbs(services: services);

        var (_, firstLink) = TapRow(answers: answers, fixture: first, row: "first");
        var (_, secondLink) = TapRow(answers: answers, fixture: second, row: "second");

        _ = Captured(action: () => {
            _ = firstLink.SubmitUndo(count: 1, echoes: echoes, principal: Principal.Console, verb: "world.undo");
            _ = secondLink.SubmitUndo(count: 1, echoes: echoes, principal: Principal.Console, verb: "world.undo");
        });

        Assert.Equal(actual: echoes.PendingCount, expected: 2);

        var secondVerdict = Captured(action: () => second.Step());

        Assert.StartsWith(actualString: secondVerdict.Error, expectedStartString: "[world.undo: undo refused: nothing to undo]");
        Assert.Equal(actual: (echoes.PendingCount, WireErrors(registry: registry)), expected: (1, "[wire.errors: 1 rejected]"));

        _ = Captured(action: () => first.Step());

        Assert.Equal(actual: (echoes.PendingCount, WireErrors(registry: registry)), expected: (0, "[wire.errors: 2 rejected]"));
    }
}
