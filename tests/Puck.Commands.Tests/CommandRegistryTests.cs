using System.Globalization;

using Xunit;

namespace Puck.Commands.Tests;

/// <summary>Exercises the text-dispatch surface: the wire-native fast path and its argument parsing, rejection
/// accounting, quiet acknowledgements, interned command identity, and command-map gating.</summary>
public sealed class CommandRegistryTests {
    [Fact]
    public void WireNativeFastPathParsesTrailingArguments() {
        var registry = new CommandRegistry(modules: [new CoreModule()]);

        var result = registry.Submit(line: "sum 2 3");

        Assert.False(condition: result.IsError);
        Assert.Equal(expected: "5", actual: result.Output);
    }

    [Fact]
    public void AnUnknownVerbIsRejected() {
        var registry = new CommandRegistry(modules: [new CoreModule()]);

        var result = registry.Submit(line: "does.not.exist");

        Assert.True(condition: result.IsError);
    }

    [Fact]
    public void WireErrorsCountsRefusalsAndResetZeroesThem() {
        var registry = new CommandRegistry(modules: [new CoreModule()]);

        _ = registry.Submit(line: "does.not.exist");
        _ = registry.Submit(line: "sum notanumber 3");

        Assert.Equal(expected: "[wire.errors: 2 rejected]", actual: registry.Submit(line: "wire.errors").Output);
        Assert.Equal(expected: "[wire.errors: 2 rejected]", actual: registry.Submit(line: "wire.errors reset").Output);
        Assert.Equal(expected: "[wire.errors: 0 rejected]", actual: registry.Submit(line: "wire.errors").Output);
    }

    [Fact]
    public void QuietModeDropsAcknowledgementSuccessesButNotAnswersOrErrors() {
        var registry = new CommandRegistry(modules: [new CoreModule()]);
        _ = registry.Submit(line: "wire.ack quiet");

        // ackOnly success → dropped to None; an answer-bearing verb and every error still surface.
        Assert.Equal(expected: CommandResult.None, actual: registry.Submit(line: "ping"));
        Assert.Equal(expected: "5", actual: registry.Submit(line: "sum 2 3").Output);
        Assert.True(condition: registry.Submit(line: "sum bad 3").IsError);
    }

    [Fact]
    public void CommandIdentityIsInternedInOrdinalNameOrder() {
        var registry = new CommandRegistry(modules: [new CoreModule()]);

        Assert.True(condition: registry.TryGetId(name: "alpha", id: out var alpha));
        Assert.True(condition: registry.TryGetId(name: "beta", id: out var beta));

        Assert.True(condition: alpha < beta);   // ordinal-sorted assignment: "alpha" precedes "beta"
        Assert.Equal(expected: "alpha", actual: registry.GetName(id: alpha));
        Assert.False(condition: registry.TryGetId(name: "nope", id: out _));
    }

    [Fact]
    public void RegisteredMapsAreImmutableCommandMetadata() {
        var registry = new CommandRegistry(modules: [new CoreModule()]);

        Assert.Equal(expected: [CommandMaps.Global, "combat"], actual: registry.Maps);
        Assert.True(condition: registry.TryGetMetadata(name: "beta", metadata: out var beta));
        Assert.Equal(expected: "combat", actual: beta.Map);
    }

    [Fact]
    public void ACommandNameClaimedTwiceIsRefusedAtConstruction() {
        _ = Assert.Throws<InvalidOperationException>(testCode: static () => new CommandRegistry(modules: [new CoreModule(), new CoreModule()]));
    }

    [Fact]
    public void SimulationLinesSeparatedByNonSpaceWhitespaceDrainBehindTheSubmissionBarrier() {
        var submitted = new List<string>();
        var registry = new CommandRegistry(modules: [new CoreModule(), new SimulationModule()]);
        var router = new InputRouter(
            registry: registry,
            bindings: new EmptyBindings(),
            principalResolver: new ConsolePrincipal()
        );
        var source = new TextCommandSource(
            registry: registry,
            onResult: (line, _) => submitted.Add(item: line)
        );

        registry.RouteSimulationTo(sink: router.ConsoleTextSink);
        source.Enqueue(line: "sim first");
        source.Enqueue(line: "sim\vsecond");
        source.Enqueue(line: "sum 2 3");
        source.Collect();

        // Both simulation mutations join the pending snapshot FIFO. The immediate read-back remains queued until
        // that snapshot applies.
        Assert.Equal(expected: ["sim first", "sim\vsecond"], actual: submitted);

        var snapshot = router.SnapshotForTick(tick: 1UL, windowEndTick: ulong.MaxValue);

        registry.ApplySnapshot(snapshot: in snapshot);
        source.Collect();

        Assert.Equal(expected: ["sim first", "sim\vsecond", "sum 2 3"], actual: submitted);
    }

    [Fact]
    public void PlainSimulationLineUsesWireArgumentsWhenItsTickApplies() {
        var seen = new List<(bool ParseWasNull, string Argument)>();
        var registry = new CommandRegistry(modules: [new SimulationProbeModule(seen: seen)]);
        var router = new InputRouter(
            registry: registry,
            bindings: new EmptyBindings(),
            principalResolver: new ConsolePrincipal()
        );

        registry.RouteSimulationTo(sink: router.ConsoleTextSink);

        Assert.Equal(expected: CommandResult.None, actual: registry.Submit(line: "sim.probe payload"));
        Assert.Empty(collection: seen);

        var snapshot = router.SnapshotForTick(tick: 1UL, windowEndTick: ulong.MaxValue);

        registry.ApplySnapshot(snapshot: in snapshot);

        Assert.Equal(expected: [(true, "payload")], actual: seen);
    }

    [Fact]
    public void AnUnspecifiedBindabilityRegistrationIsRefusedByName() {
        _ = Assert.Throws<InvalidOperationException>(testCode: static () => new CommandRegistry(modules: [new UnspecifiedBindabilityModule()]));
    }

    [Fact]
    public void SnapshotCannotBeAppliedThroughAnotherRegistrysCommandIdNamespace() {
        var sourceRegistry = new CommandRegistry(modules: [new SingleCommandModule(name: "harmless")]);
        var invoked = false;
        var targetRegistry = new CommandRegistry(modules: [new SingleCommandModule(
            name: "privileged",
            onInvoke: () => invoked = true
        )]);
        var router = new InputRouter(
            registry: sourceRegistry,
            bindings: new FixedBindings(command: "harmless"),
            principalResolver: new ConsolePrincipal()
        );

        router.Capture(signal: InputSignal.Press(source: "key.a"));
        var snapshot = router.SnapshotForTick(tick: 1UL, windowEndTick: ulong.MaxValue);

        _ = Assert.Throws<ArgumentException>(testCode: () => targetRegistry.ApplySnapshot(snapshot: in snapshot));
        Assert.False(condition: invoked);
    }

    [Fact]
    public void WireNativeFastPathUsesTheCommandsDeclaredValueKind() {
        var registry = new CommandRegistry(modules: [new KindProbeModule()]);

        Assert.Equal(expected: "Axis1D", actual: registry.Submit(line: "kind").Output);
        Assert.Equal(expected: "Axis1D", actual: registry.Submit(line: "kind \"\"").Output);
    }

    [Fact]
    public void MoreCommandsThanTheSnapshotIdSpaceCanRepresentAreRefused() {
        _ = Assert.Throws<InvalidOperationException>(testCode: static () => new CommandRegistry(modules: [new ManyCommandsModule(count: ushort.MaxValue + 2)]));
    }

    [Fact]
    public void TheFinalRepresentableCommandIdStillResolves() {
        var registry = new CommandRegistry(modules: [new ManyCommandsModule(count: ushort.MaxValue + 1)]);

        Assert.NotEmpty(registry.GetName(id: ushort.MaxValue));
    }

    private sealed class CoreModule : ICommandModule {
        public IEnumerable<CommandDefinition> GetCommands() {
            yield return CommandDefinition.Verb(
                name: "alpha",
                description: "First.",
                valueKind: CommandValueKind.Digital,
                handler: static _ => CommandResult.None,
                bindability: CommandBindability.Bindable
            );
            yield return CommandDefinition.Verb(
                name: "beta",
                description: "Second.",
                valueKind: CommandValueKind.Digital,
                handler: static _ => CommandResult.None,
                bindability: CommandBindability.Bindable,
                map: "combat"
            );
            yield return CommandDefinition.WithWireArgs(
                name: "sum",
                description: "Adds two integers.",
                handler: static (_, args) => ((args.TryInt(index: 0, out var a) && args.TryInt(index: 1, out var b))
                    ? new CommandResult(Output: (a + b).ToString(provider: CultureInfo.InvariantCulture))
                    : CommandResult.Error(output: "[sum: two integers]")),
                bindability: CommandBindability.Unbindable
            );
            yield return CommandDefinition.WithWireArgs(
                name: "ping",
                description: "Acknowledges.",
                handler: static (_, args) => (args.Echo
                    ? new CommandResult(Output: "pong")
                    : CommandResult.None),
                bindability: CommandBindability.Unbindable,
                ackOnly: true
            );
        }
    }

    private sealed class UnspecifiedBindabilityModule : ICommandModule {
        public IEnumerable<CommandDefinition> GetCommands() {
            yield return CommandDefinition.Verb(
                name: "unspecified",
                description: "Declares no bindability.",
                valueKind: CommandValueKind.Digital,
                handler: static _ => CommandResult.None,
                bindability: CommandBindability.Unspecified
            );
        }
    }

    private sealed class SimulationModule : ICommandModule {
        public IEnumerable<CommandDefinition> GetCommands() {
            yield return CommandDefinition.WithWireArgs(
                name: "sim",
                description: "Deferred simulation probe.",
                handler: static (_, _) => CommandResult.None,
                bindability: CommandBindability.Unbindable,
                routing: CommandRouting.Simulation
            );
        }
    }

    private sealed class EmptyBindings : IInputBindings {
        public IReadOnlyList<CommandBinding>? Resolve(int slot, string source) => null;
    }

    private sealed class SimulationProbeModule(List<(bool ParseWasNull, string Argument)> seen) : ICommandModule {
        public IEnumerable<CommandDefinition> GetCommands() {
            yield return CommandDefinition.WithWireArgs(
                name: "sim.probe",
                description: "Deferred wire-path probe.",
                handler: (context, args) => {
                    seen.Add(item: (
                        ParseWasNull: (context.Parse is null),
                        Argument: args[0].ToString()
                    ));

                    return CommandResult.None;
                },
                bindability: CommandBindability.Unbindable,
                routing: CommandRouting.Simulation
            );
        }
    }

    private sealed class FixedBindings(string command) : IInputBindings {
        private readonly CommandBinding[] m_bindings = [new CommandBinding(Command: command)];

        public IReadOnlyList<CommandBinding>? Resolve(int slot, string source) => m_bindings;
    }

    private sealed class SingleCommandModule(string name, Action? onInvoke = null) : ICommandModule {
        public IEnumerable<CommandDefinition> GetCommands() {
            yield return CommandDefinition.Verb(
                name: name,
                description: "Snapshot provenance probe.",
                valueKind: CommandValueKind.Digital,
                handler: _ => {
                    onInvoke?.Invoke();

                    return CommandResult.None;
                },
                bindability: CommandBindability.Bindable
            );
        }
    }

    private sealed class KindProbeModule : ICommandModule {
        public IEnumerable<CommandDefinition> GetCommands() {
            yield return CommandDefinition.WithWireArgs(
                name: "kind",
                description: "Reports the context value kind.",
                handler: static (context, _) => new CommandResult(Output: context.Value.Kind.ToString()),
                bindability: CommandBindability.Unbindable,
                valueKind: CommandValueKind.Axis1D
            );
        }
    }

    private sealed class ManyCommandsModule(int count) : ICommandModule {
        public IEnumerable<CommandDefinition> GetCommands() {
            for (var index = 0; index < count; index++) {
                yield return CommandDefinition.Verb(
                    name: $"command.{index}",
                    description: "Capacity probe.",
                    valueKind: CommandValueKind.Digital,
                    handler: static _ => CommandResult.None,
                    bindability: CommandBindability.Bindable
                );
            }
        }
    }

    private sealed class ConsolePrincipal : ICommandPrincipalResolver {
        public CommandPrincipal PrincipalOf(int slot) => CommandPrincipal.Console;
    }
}
