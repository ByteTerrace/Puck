using Xunit;

namespace Puck.Commands.Tests;

/// <summary>A session that settles results receives, once per line, what became of the line: the handler's result
/// when its tick applies, or the verdict of the work the handler started.</summary>
public sealed class CommandSettlementTests {
    private static (TextCommandSource Source, InputRouter Router, CommandRegistry Registry) Host(CommandSettlement? settlement = null) {
        var registry = new CommandRegistry(modules: [new SettlingModule(settlement: settlement)]);

        return (
            new TextCommandSource(registry: registry),
            new InputRouter(
                bindings: new NoBindings(),
                principalResolver: new ConsolePrincipal(),
                registry: registry
            ),
            registry
        );
    }
    private static void Tick(InputRouter router, CommandRegistry registry, ulong tick) {
        var snapshot = router.SnapshotForTick(
            tick: tick,
            windowEndTick: ulong.MaxValue
        );

        registry.ApplySnapshot(snapshot: in snapshot);
    }

    [Fact]
    public void AnImmediateLineSettlesWithItsResultAtSubmission() {
        var (source, _, _) = Host();
        var settled = new List<(string Line, CommandResult Result)>();
        using var session = source.CreateSession(
            onSettled: (line, result) => settled.Add(item: (line, result)),
            principal: CommandPrincipal.Console
        );

        session.Enqueue(line: "now");
        session.Enqueue(line: "nothing-by-this-name");
        source.Collect();

        Assert.Equal(expected: "now", actual: settled[0].Line);
        Assert.Equal(expected: "[now]", actual: settled[0].Result.Output);
        Assert.True(condition: settled[1].Result.IsError);
    }
    [Fact]
    public void ASimulationLineSettlesWithItsHandlersResultWhenItsTickApplies() {
        var (source, router, registry) = Host();
        var results = new List<CommandResult>();
        var settled = new List<CommandResult>();
        using var session = source.CreateSession(
            onResult: (_, result) => results.Add(item: result),
            onSettled: (_, result) => settled.Add(item: result),
            principal: CommandPrincipal.Console,
            simulationSink: router.ConsoleTextSink
        );

        session.Enqueue(line: "later fails");
        source.Collect();

        // The submit-time channel still answers at once with nothing; the settled one has nothing to say yet.
        Assert.Equal(expected: [CommandResult.None], actual: results);
        Assert.Empty(collection: settled);

        Tick(registry: registry, router: router, tick: 1UL);

        var verdict = Assert.Single(collection: settled);

        Assert.True(condition: verdict.IsError);
        Assert.Equal(expected: "[later: fails]", actual: verdict.Output);
    }
    [Fact]
    public void APendingSettlementHoldsTheSessionsNextLineUntilItsVerdictArrives() {
        var settlement = new CommandSettlement();

        var (source, router, registry) = Host(settlement: settlement);
        var settled = new List<string>();
        using var session = source.CreateSession(
            onSettled: (line, result) => settled.Add(item: $"{line} -> {result.Output}"),
            principal: CommandPrincipal.Console,
            simulationSink: router.ConsoleTextSink
        );

        session.Enqueue(line: "edit");
        session.Enqueue(line: "now");
        source.Collect();
        Tick(registry: registry, router: router, tick: 1UL);
        source.Collect();

        // The handler ran and started work; neither it nor the line behind it has settled.
        Assert.Empty(collection: settled);

        settlement.Settle(result: new CommandResult(Output: "[edit: applied]"));
        settlement.Settle(result: CommandResult.Error(output: "[edit: a second verdict]"));
        source.Collect();

        Assert.Equal(actual: settled, expected: ["edit -> [edit: applied]", "now -> [now]"]);
    }
    [Fact]
    public void ASessionThatSettlesNothingKeepsSubmitTimeOrdering() {
        var (source, router, registry) = Host(settlement: new CommandSettlement());
        var results = new List<string>();
        using var session = source.CreateSession(
            onResult: (line, _) => results.Add(item: line),
            principal: CommandPrincipal.Console,
            simulationSink: router.ConsoleTextSink
        );

        session.Enqueue(line: "edit");
        session.Enqueue(line: "now");
        source.Collect();
        Tick(registry: registry, router: router, tick: 1UL);
        source.Collect();

        Assert.Equal(actual: results, expected: ["edit", "now"]);
    }
    [Fact]
    public void SettlingSessionsSerializeSimulationLinesEvenWithQuietAcknowledgements() {
        var settlement = new CommandSettlement();

        var (source, router, registry) = Host(settlement);
        var settled = new List<string>();

        _ = registry.Submit(line: "wire.ack quiet");
        using var session = source.CreateSession(principal: CommandPrincipal.Console,
            simulationSink: router.ConsoleTextSink, onSettled: (line, _) => settled.Add(line));

        session.Enqueue(line: "edit");
        session.Enqueue(line: "later");
        source.Collect();
        Tick(registry: registry, router: router, tick: 1);
        source.Collect();
        Tick(registry: registry, router: router, tick: 2);
        Assert.Empty(collection: settled);
        settlement.Settle(new CommandResult("done"));
        source.Collect();
        Tick(registry: registry, router: router, tick: 3);
        Assert.Equal(actual: settled, expected: ["edit", "later"]);
    }
    [Fact]
    public void CancellationSettlesCurrentAndAbandonedCommands() {
        var (source, router, registry) = Host();
        var settled = new List<CommandResult>();
        using var first = source.CreateSession(principal: CommandPrincipal.Console,
            simulationSink: router.ConsoleTextSink, onSettled: (_, result) => settled.Add(result));
        using var second = source.CreateSession(principal: CommandPrincipal.Console,
            simulationSink: router.ConsoleTextSink, onSettled: (_, result) => settled.Add(result));

        first.Enqueue(line: "cancel");
        second.Enqueue(line: "later");
        source.Collect();
        Assert.Throws<OperationCanceledException>(testCode: () => Tick(registry: registry, router: router, tick: 1));
        Assert.Equal(2, settled.Count);
        Assert.All(settled, result => Assert.True(condition: result.IsError));
        first.Enqueue(line: "now");
        second.Enqueue(line: "now");
        source.Collect();
        Assert.Equal(4, settled.Count);
    }
    [Fact]
    public void BrokenResultObserversCannotInterruptOtherSessionsOrStrandTheBarrier() {
        var settlement = new CommandSettlement();

        var (source, router, registry) = Host(settlement);
        var seen = new List<CommandResult>();
        using var broken = source.CreateSession(principal: CommandPrincipal.Console,
            simulationSink: router.ConsoleTextSink,
            onResult: (_, _) => throw new IOException(message: "output closed"),
            onSettled: (_, _) => throw new OperationCanceledException());
        using var healthy = source.CreateSession(principal: CommandPrincipal.Console,
            simulationSink: router.ConsoleTextSink, onSettled: (_, result) => seen.Add(result));

        broken.Enqueue(line: "edit");
        healthy.Enqueue(line: "edit");
        source.Collect();
        Tick(registry: registry, router: router, tick: 1);
        settlement.Settle(new CommandResult("done"));
        Assert.Single(collection: seen);
        broken.Enqueue(line: "now");
        source.Collect();
        Assert.Equal(4, broken.ResultCallbackFaults);
    }
    [InlineData(false)]
    [InlineData(true)]
    [Theory]
    public void DiscardedCapturedCommandsSettleAndReleaseTheirSession(bool overflow) {
        var (source, router, registry) = Host();
        var settled = new List<CommandResult>();
        using var session = source.CreateSession(principal: CommandPrincipal.Console,
            simulationSink: router.ConsoleTextSink, onSettled: (_, result) => settled.Add(result));

        session.Enqueue(line: "later");
        source.Collect();
        if (overflow) {
            using var flood = source.CreateSession(principal: CommandPrincipal.Console, simulationSink: router.ConsoleTextSink);

            for (var index = 0; (index < InputRouter.MaxCapturedInjections); index++) {
                flood.Enqueue(line: "later");
            }
            source.Collect();
            Assert.Equal(1, router.DroppedInjectionCount);
        } else {
            router.Dispose();
        }
        Assert.True(condition: Assert.Single(collection: settled).IsError);
        session.Enqueue(line: "now");
        source.Collect();
        Assert.Equal(2, settled.Count);
        Assert.False(condition: settled[1].IsError);
        router.Dispose();
    }

    private sealed class SettlingModule(CommandSettlement? settlement) : ICommandModule {
        public IEnumerable<CommandDefinition> GetCommands() {
            yield return CommandDefinition.WithWireArgs(name: "cancel", bindability: CommandBindability.Unbindable, description: "Cancels the host tick.",
                handler: static (_, _) => throw new OperationCanceledException(), routing: CommandRouting.Simulation);
            yield return CommandDefinition.WithWireArgs(
                bindability: CommandBindability.Unbindable,
                description: "Answers at once.",
                handler: static (_, _) => new CommandResult(Output: "[now]"),
                name: "now"
            );
            yield return CommandDefinition.WithWireArgs(
                bindability: CommandBindability.Unbindable,
                description: "Answers when its tick applies.",
                handler: static (_, args) => ((args.Count == 0)
                    ? new CommandResult(Output: "[later]")
                    : CommandResult.Error(output: $"[later: {args[0]}]")
                ),
                name: "later",
                routing: CommandRouting.Simulation
            );
            yield return CommandDefinition.WithWireArgs(
                bindability: CommandBindability.Unbindable,
                description: "Starts work an authority decides later.",
                handler: (_, _) => CommandResult.Settling(settlement: settlement!),
                name: "edit",
                ackOnly: true,
                routing: CommandRouting.Simulation
            );
        }
    }
    private sealed class NoBindings : IInputBindings {
        public IReadOnlyList<CommandBinding>? Resolve(int slot, string source) => null;
    }
    private sealed class ConsolePrincipal : ICommandPrincipalResolver {
        public CommandPrincipal PrincipalOf(int slot) => CommandPrincipal.Console;
    }
}
