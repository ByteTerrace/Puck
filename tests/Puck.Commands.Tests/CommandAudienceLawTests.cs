using Xunit;

namespace Puck.Commands.Tests;

/// <summary>Pins <see cref="CommandAudience.Operator"/>: the registry refuses an operator verb for every principal but
/// the console at each door a line or a press reaches it through, and the handler never runs for the refused one.</summary>
public sealed class CommandAudienceLawTests {
    private static CommandSnapshot Tick(InputRouter router) => router.SnapshotForTick(
        tick: 1UL,
        windowEndTick: ulong.MaxValue
    );

    [Fact]
    public void AnOperatorVerbRunsForTheConsoleAndIsRefusedForASeatOnTheWirePath() {
        var runs = new List<Principal>();
        var registry = new CommandRegistry(modules: [new AudienceModule(runs: runs)]);
        var router = new InputRouter(
            bindings: new EmptyBindings(),
            principalResolver: new SeatPrincipal(),
            registry: registry
        );
        var source = new TextCommandSource(registry: registry);
        var results = new List<CommandResult>();
        var seat = source.CreateSeatSession(
            onResult: (_, result) => results.Add(item: result),
            router: router,
            slot: 1
        );

        Assert.False(condition: registry.Submit(line: "probe.operator one").IsError);
        seat.Enqueue(line: "probe.operator one");
        seat.Enqueue(line: "probe.anyone one");
        source.Collect();

        Assert.Equal(
            actual: runs,
            expected: [Principal.Console, Principal.Seat(slot: 1)]
        );
        Assert.True(condition: results[0].IsError);
        Assert.Equal(
            actual: results[0].Output,
            expected: "[probe.operator: refused — an operator verb; seat2 is not the operator]"
        );
        Assert.False(condition: results[1].IsError);
    }
    [Fact]
    public void AnOperatorVerbIsRefusedForASeatOnTheFullParsePath() {
        var runs = new List<Principal>();
        var registry = new CommandRegistry(modules: [new AudienceModule(runs: runs)]);
        var router = new InputRouter(
            bindings: new EmptyBindings(),
            principalResolver: new SeatPrincipal(),
            registry: registry
        );
        var source = new TextCommandSource(registry: registry);
        var results = new List<CommandResult>();
        var seat = source.CreateSeatSession(
            onResult: (_, result) => results.Add(item: result),
            router: router,
            slot: 0
        );

        // A quoted argument takes the System.CommandLine parse rather than the wire-native path.
        seat.Enqueue(line: "probe.operator \"quoted value\"");
        source.Collect();

        Assert.Empty(collection: runs);
        Assert.True(condition: results[0].IsError);
        Assert.StartsWith(
            actualString: results[0].Output,
            expectedStartString: "[probe.operator: refused — an operator verb;",
            comparisonType: StringComparison.Ordinal
        );
    }
    [Fact]
    public void AnOperatorVerbDeferredToTheTickIsRefusedWhenTheTickAppliesIt() {
        var runs = new List<Principal>();
        var registry = new CommandRegistry(modules: [new AudienceModule(runs: runs)]);
        var router = new InputRouter(
            bindings: new EmptyBindings(),
            principalResolver: new SeatPrincipal(),
            registry: registry
        );
        var source = new TextCommandSource(registry: registry);
        var seat = source.CreateSeatSession(
            router: router,
            slot: 2
        );

        seat.Enqueue(line: "probe.operator.sim one");
        source.Collect();

        var snapshot = Tick(router: router);

        registry.ApplySnapshot(snapshot: in snapshot);

        Assert.Empty(collection: runs);
    }
    [Fact]
    public void AnOperatorVerbBoundToASeatsInputIsRefused() {
        var runs = new List<Principal>();
        var registry = new CommandRegistry(modules: [new AudienceModule(runs: runs)]);
        var router = new InputRouter(
            bindings: new FixedBindings(command: "probe.operator.bound"),
            principalResolver: new SeatPrincipal(),
            registry: registry
        );

        router.Capture(signal: InputSignal.Press(source: "key.a"));

        var snapshot = Tick(router: router);

        registry.ApplySnapshot(snapshot: in snapshot);

        Assert.Empty(collection: runs);

        // The control: the same bound verb runs for a console-resolved press.
        var consoleRouter = new InputRouter(
            bindings: new FixedBindings(command: "probe.operator.bound"),
            principalResolver: new ConsolePrincipal(),
            registry: registry
        );

        consoleRouter.Capture(signal: InputSignal.Press(source: "key.a"));

        var consoleSnapshot = Tick(router: consoleRouter);

        registry.ApplySnapshot(snapshot: in consoleSnapshot);

        Assert.Equal(
            actual: runs,
            expected: [Principal.Console]
        );
    }
    [Fact]
    public void TheAudienceIsPartOfTheCommandsPublicMetadata() {
        var registry = new CommandRegistry(modules: [new AudienceModule(runs: [])]);

        Assert.True(condition: registry.TryGetMetadata(
            metadata: out var operatorVerb,
            name: "probe.operator"
        ));
        Assert.Equal(
            actual: operatorVerb.Audience,
            expected: CommandAudience.Operator
        );
        Assert.True(condition: registry.TryGetMetadata(
            metadata: out var anyoneVerb,
            name: "probe.anyone"
        ));
        Assert.Equal(
            actual: anyoneVerb.Audience,
            expected: CommandAudience.Anyone
        );
    }

    private sealed class AudienceModule(List<Principal> runs) : ICommandModule {
        public IEnumerable<CommandDefinition> GetCommands() {
            yield return CommandDefinition.WithWireArgs(
                audience: CommandAudience.Operator,
                bindability: CommandBindability.Unbindable,
                description: "An operator diagnostic.",
                handler: (context, _) => {
                    runs.Add(item: context.Principal);

                    return new CommandResult(Output: "[probe.operator: ran]");
                },
                name: "probe.operator"
            );
            yield return CommandDefinition.WithWireArgs(
                audience: CommandAudience.Operator,
                bindability: CommandBindability.Unbindable,
                description: "An operator diagnostic applied at the tick.",
                handler: (context, _) => {
                    runs.Add(item: context.Principal);

                    return CommandResult.None;
                },
                name: "probe.operator.sim",
                routing: CommandRouting.Simulation
            );
            yield return CommandDefinition.Verb(
                audience: CommandAudience.Operator,
                bindability: CommandBindability.Bindable,
                description: "An operator verb a binding may name.",
                handler: context => {
                    runs.Add(item: context.Principal);

                    return CommandResult.None;
                },
                name: "probe.operator.bound",
                valueKind: CommandValueKind.Digital
            );
            yield return CommandDefinition.WithWireArgs(
                bindability: CommandBindability.Unbindable,
                description: "A verb anyone may run.",
                handler: (context, _) => {
                    runs.Add(item: context.Principal);

                    return new CommandResult(Output: "[probe.anyone: ran]");
                },
                name: "probe.anyone"
            );
        }
    }
    private sealed class ConsolePrincipal : IPrincipalResolver {
        public Principal PrincipalOf(int slot) => Principal.Console;
    }
    private sealed class SeatPrincipal : IPrincipalResolver {
        public Principal PrincipalOf(int slot) => Principal.Seat(slot: slot);
    }
    private sealed class EmptyBindings : IInputBindings {
        public IReadOnlyList<CommandBinding>? Resolve(int slot, string source) => null;
    }
    private sealed class FixedBindings(string command) : IInputBindings {
        private readonly CommandBinding[] m_bindings = [new CommandBinding(Command: command)];

        public IReadOnlyList<CommandBinding>? Resolve(int slot, string source) => m_bindings;
    }
}
