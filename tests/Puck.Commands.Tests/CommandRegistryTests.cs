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

        Assert.True(condition: registry.TryGetId(id: out var alpha, name: "alpha"));
        Assert.True(condition: registry.TryGetId(id: out var beta, name: "beta"));

        Assert.True(condition: (alpha < beta));   // ordinal-sorted assignment: "alpha" precedes "beta"
        Assert.Equal(expected: "alpha", actual: registry.GetName(id: alpha));
        Assert.False(condition: registry.TryGetId(id: out _, name: "nope"));
    }
    [Fact]
    public void RegisteredMapsAreImmutableCommandMetadata() {
        var registry = new CommandRegistry(modules: [new CoreModule()]);

        Assert.Equal(expected: [CommandMaps.Global, "combat"], actual: registry.Maps);
        Assert.True(condition: registry.TryGetMetadata(metadata: out var beta, name: "beta"));
        Assert.Equal(expected: "combat", actual: beta.Map);
        Assert.False(condition: beta.AcceptsWireArgs);
        Assert.True(condition: registry.TryGetMetadata(metadata: out var sum, name: "sum"));
        Assert.True(condition: sum.AcceptsWireArgs);
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
        Assert.Equal(actual: submitted, expected: ["sim first", "sim\vsecond"]);

        var snapshot = router.SnapshotForTick(tick: 1UL, windowEndTick: ulong.MaxValue);

        registry.ApplySnapshot(snapshot: in snapshot);
        source.Collect();

        Assert.Equal(actual: submitted, expected: ["sim first", "sim\vsecond", "sum 2 3"]);
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

        Assert.Equal(actual: seen, expected: [(true, "payload")]);
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
    public void BoundTextSeparatorsRemainArgumentsOfOneSeatStampedCommand() {
        CommandPrincipal? seenPrincipal = null;
        string? seenLine = null;
        var secondCommandInvoked = false;
        var registry = new CommandRegistry(modules: [new SeparatorProbeModule(
            onBound: context => {
                seenPrincipal = context.Principal;
                seenLine = context.Text;
            },
            onSecond: () => secondCommandInvoked = true
        )]);
        var router = new InputRouter(
            registry: registry,
            bindings: new FixedBindings(
                command: "bound",
                text: "  first; second && privileged | fourth  "
            ),
            principalResolver: new SeatPrincipal()
        );

        router.Capture(signal: InputSignal.Press(source: "key.a"));
        var snapshot = router.SnapshotForTick(tick: 1UL, windowEndTick: ulong.MaxValue);

        registry.ApplySnapshot(snapshot: in snapshot);

        Assert.Equal(expected: CommandPrincipal.Seat(slot: 0), actual: seenPrincipal);
        Assert.Equal(expected: "bound   first; second && privileged | fourth  ", actual: seenLine);
        Assert.False(condition: secondCommandInvoked);
    }
    [Fact]
    public void WireNativeFastPathUsesTheCommandsDeclaredValueKind() {
        var registry = new CommandRegistry(modules: [new KindProbeModule()]);

        Assert.Equal(expected: "Axis1D", actual: registry.Submit(line: "kind").Output);
        Assert.Equal(expected: "Axis1D", actual: registry.Submit(line: "kind \"\"").Output);
    }
    [Fact]
    public void MoreCommandsThanTheSnapshotIdSpaceCanRepresentAreRefused() {
        _ = Assert.Throws<InvalidOperationException>(testCode: static () => new CommandRegistry(modules: [new ManyCommandsModule(count: (ushort.MaxValue + 2))]));
    }
    [Fact]
    public void TheFinalRepresentableCommandIdStillResolves() {
        var registry = new CommandRegistry(modules: [new ManyCommandsModule(count: (ushort.MaxValue + 1))]);

        Assert.NotEmpty(collection: registry.GetName(id: ushort.MaxValue));
    }
    [Fact]
    public void AnAtPrefixedTokenIsALiteralArgumentAndNeverReadsAFile() {
        var path = $"puck-response-probe-{Guid.NewGuid():N}.txt";
        var registry = new CommandRegistry(modules: [new CoreModule(), new EchoModule()]);

        File.WriteAllText(
            contents: "spliced",
            path: path
        );

        try {
            // Both the wire-native path and the quoted System.CommandLine fallback see the token verbatim: response
            // file expansion would have replaced it with the file's contents, and a missing file would have made the
            // parser echo the path back as an error.
            Assert.Equal(expected: $"@{path}", actual: registry.Submit(line: $"echo.first @{path}").Output);
            Assert.Equal(expected: $"@{path}", actual: registry.Submit(line: $"echo.first \"@{path}\"").Output);
            Assert.Equal(expected: "@nope", actual: registry.Submit(line: "echo.first @nope").Output);
            Assert.Equal(expected: "[wire.errors: 0 rejected]", actual: registry.Submit(line: "wire.errors").Output);
        } finally {
            File.Delete(path: path);
        }
    }
    [Fact]
    public void AThrowingHandlerBecomesACountedErrorResultRatherThanEscapingSubmit() {
        var registry = new CommandRegistry(modules: [new CoreModule(), new ThrowingModule()]);

        var result = registry.Submit(line: "boom.wire");

        Assert.True(condition: result.IsError);
        Assert.Contains(actualString: result.Output, expectedSubstring: nameof(InvalidTimeZoneException));
        Assert.True(condition: registry.Submit(line: "boom.parsed \"quoted\"").IsError);
        Assert.Equal(expected: "[wire.errors: 2 rejected]", actual: registry.Submit(line: "wire.errors").Output);
    }
    [Fact]
    public void AThrowingSnapshotHandlerStillLetsALaterTextEntryReleaseItsBarrier() {
        var applied = new List<string>();
        var submitted = new List<string>();
        var registry = new CommandRegistry(modules: [
            new CoreModule(),
            new ThrowingModule(),
            new RecordingSimulationModule(applied: applied),
        ]);
        var router = new InputRouter(
            registry: registry,
            bindings: new FixedBindings(command: "boom.bound"),
            principalResolver: new ConsolePrincipal()
        );
        var source = new TextCommandSource(registry: registry);
        var session = source.CreateSeatSession(
            onResult: (line, _) => submitted.Add(item: line),
            router: router,
            slot: 0
        );

        session.Enqueue(line: "sim.record payload");
        session.Enqueue(line: "sum 2 3");
        // Captured BEFORE the drain injects the text entry, so the throwing bound entry sorts ahead of it in the lane.
        router.Capture(signal: InputSignal.Press(source: "key.a"));
        source.Collect();

        Assert.Equal(actual: submitted, expected: ["sim.record payload"]);

        var snapshot = router.SnapshotForTick(tick: 1UL, windowEndTick: ulong.MaxValue);

        registry.ApplySnapshot(snapshot: in snapshot);

        // The bound handler threw first; the text entry behind it still ran, and its session's read-after-write
        // barrier still released, so the queued immediate line drains on the next frame.
        Assert.Equal(actual: applied, expected: ["payload"]);
        Assert.Equal(expected: "[wire.errors: 1 rejected]", actual: registry.Submit(line: "wire.errors").Output);

        source.Collect();

        Assert.Equal(actual: submitted, expected: ["sim.record payload", "sum 2 3"]);
    }
    [Fact]
    public void AVerbSpelledInAnotherCaseDispatchesOnEveryTextPath() {
        var registry = new CommandRegistry(modules: [new CoreModule(), new EchoModule()]);

        // Command identity is case-insensitive everywhere else in Puck (m_byName, interned ids, the binding
        // vocabulary), so the wire table and the System.CommandLine fallback must agree with it.
        Assert.Equal(expected: "5", actual: registry.Submit(line: "SUM 2 3").Output);
        Assert.Equal(expected: "a b", actual: registry.Submit(line: "Echo.First \"a b\"").Output);
        Assert.Equal(expected: "[wire.ack: on]", actual: registry.Submit(line: "Wire.Ack").Output);
        Assert.Equal(expected: "[wire.errors: 0 rejected]", actual: registry.Submit(line: "WIRE.ERRORS").Output);
    }
    [Fact]
    public void ASimulationLineSpelledInAnotherCaseDispatchesWhenItsTickApplies() {
        var applied = new List<string>();
        var registry = new CommandRegistry(modules: [new RecordingSimulationModule(applied: applied)]);
        var router = new InputRouter(
            registry: registry,
            bindings: new EmptyBindings(),
            principalResolver: new ConsolePrincipal()
        );

        registry.RouteSimulationTo(sink: router.ConsoleTextSink);

        Assert.Equal(expected: CommandResult.None, actual: registry.Submit(line: "SIM.Record payload"));

        var snapshot = router.SnapshotForTick(tick: 1UL, windowEndTick: ulong.MaxValue);

        registry.ApplySnapshot(snapshot: in snapshot);

        Assert.Equal(actual: applied, expected: ["payload"]);
        Assert.Equal(expected: "[wire.errors: 0 rejected]", actual: registry.Submit(line: "wire.errors").Output);
    }
    [Fact]
    public void ADeferredLineDispatchesWithTheEdgeItsSnapshotEntryRecorded() {
        var phases = new List<CommandPhase>();
        var registry = new CommandRegistry(modules: [new PhaseProbeModule(phases: phases)]);
        var router = new InputRouter(
            registry: registry,
            bindings: new EmptyBindings(),
            principalResolver: new ConsolePrincipal()
        );

        registry.RouteSimulationTo(sink: router.ConsoleTextSink);

        _ = registry.Submit(line: "phase.probe");

        var snapshot = router.SnapshotForTick(tick: 1UL, windowEndTick: ulong.MaxValue);

        registry.ApplySnapshot(snapshot: in snapshot);

        // A console impulse injects as a Started edge; a HELD wire verb branching on phase must see that press rather
        // than the release branch a hard-coded Completed would hand it.
        Assert.Equal(actual: phases, expected: [CommandPhase.Started]);
    }
    [Fact]
    public void TheSubmittedLineIsTheHandlersContextTextOnEveryTextPath() {
        var seen = new List<string?>();
        var registry = new CommandRegistry(modules: [new TextProbeModule(seen: seen)]);

        _ = registry.Submit(line: "text.probe hello");
        _ = registry.Submit(line: "text.probe \"a b\"");

        Assert.Equal(actual: seen, expected: ["text.probe hello", "text.probe \"a b\""]);
    }
    [Fact]
    public void AMalformedDeferredLineIsRefusedWhenItsTickApplies() {
        var registry = new CommandRegistry(modules: [new BareSimulationModule()]);
        var router = new InputRouter(
            registry: registry,
            bindings: new EmptyBindings(),
            principalResolver: new ConsolePrincipal()
        );

        registry.RouteSimulationTo(sink: router.ConsoleTextSink);

        // Submit resolves the verb and defers; it does NOT parse the arguments, so the line's one parse happens at
        // apply time and its refusal reaches wire.errors a tick later instead of the call site.
        Assert.Equal(expected: CommandResult.None, actual: registry.Submit(line: "sim.bare extra"));
        Assert.Equal(expected: "[wire.errors: 0 rejected]", actual: registry.Submit(line: "wire.errors").Output);

        var snapshot = router.SnapshotForTick(tick: 1UL, windowEndTick: ulong.MaxValue);

        registry.ApplySnapshot(snapshot: in snapshot);

        Assert.Equal(expected: "[wire.errors: 1 rejected]", actual: registry.Submit(line: "wire.errors").Output);
    }
    [Fact]
    public void BuiltInModeTokensAreReadCaseInsensitively() {
        var registry = new CommandRegistry(modules: [new CoreModule()]);

        Assert.Equal(expected: "[wire.ack: quiet]", actual: registry.Submit(line: "wire.ack QUIET").Output);
        Assert.Equal(expected: "[wire.ack: on]", actual: registry.Submit(line: "wire.ack On").Output);

        _ = registry.Submit(line: "does.not.exist");

        Assert.Equal(expected: "[wire.errors: 1 rejected]", actual: registry.Submit(line: "wire.errors RESET").Output);
        Assert.Equal(expected: "[wire.errors: 0 rejected]", actual: registry.Submit(line: "wire.errors").Output);
    }
    [Fact]
    public void TheAffordanceManifestIsHandedOutAsAnImmutableArray() {
        var registry = new CommandRegistry(modules: [new CoreModule()]);

        // IsDefault exists only on ImmutableArray, so these lines compile only while the manifest is handed out as
        // one: an IReadOnlyList over the backing arrays was a cast away from being rewritten under the registry.
        var definitions = registry.Definitions;
        var maps = registry.Maps;

        Assert.False(condition: definitions.IsDefault);
        Assert.False(condition: maps.IsDefault);
        Assert.Equal(expected: ["alpha", "beta", "ping", "sum"], actual: definitions.Select(selector: static metadata => metadata.Name));
        Assert.Equal(expected: [CommandMaps.Global, "combat"], actual: maps);
    }
    [Fact]
    public void ALineWiderThanTheWireTokenCapAgreesWithItsQuotedForm() {
        var registry = new CommandRegistry(modules: [new EchoModule()]);
        var narrow = string.Join(
            separator: ' ',
            values: Enumerable.Range(count: 8, start: 0).Select(selector: static index => $"t{index}")
        );
        var wide = string.Join(
            separator: ' ',
            values: Enumerable.Range(count: 96, start: 0).Select(selector: static index => $"t{index}")
        );

        // Under the cap the wire tokenizer serves the line; over it System.CommandLine's splitter does, as it does for
        // any quoted line. All three must hand the handler the same tokens.
        Assert.Equal(expected: narrow, actual: registry.Submit(line: $"echo.tail {narrow}").Output);
        Assert.Equal(expected: wide, actual: registry.Submit(line: $"echo.tail {wide}").Output);
        Assert.Equal(expected: "a b", actual: registry.Submit(line: "echo.tail \"a b\"").Output);
    }
    [Fact]
    public void TheTwoTokenizersAgreeExceptOnABareEndOfOptionsMarker() {
        var registry = new CommandRegistry(modules: [new EchoModule()]);

        // Unquoted whitespace, dash-prefixed tokens, and the absence of a --help/--version option are the same on both
        // grammars; only the parser's end-of-options marker is consumed on the fallback path and not on the wire one.
        Assert.Equal(expected: "a b", actual: registry.Submit(line: "echo.tail a\vb").Output);
        Assert.Equal(expected: "x a b", actual: registry.Submit(line: "echo.tail \"x\" a\vb").Output);
        Assert.Equal(expected: "--flag", actual: registry.Submit(line: "echo.tail --flag").Output);
        Assert.Equal(expected: "x --flag", actual: registry.Submit(line: "echo.tail \"x\" --flag").Output);
        Assert.Equal(expected: "-- y", actual: registry.Submit(line: "echo.tail -- y").Output);
        Assert.Equal(expected: "x y", actual: registry.Submit(line: "echo.tail \"x\" -- y").Output);
        Assert.True(condition: registry.Submit(line: "--help").IsError);
        Assert.True(condition: registry.Submit(line: "--version").IsError);
    }
    [Fact]
    public void HelpListsEveryCommandInOrdinalNameOrder() {
        var registry = new CommandRegistry(modules: [new CoreModule()]);

        var names = registry.Submit(line: "help").Output
            .Split(separator: '\n')
            .Select(selector: static entry => entry.Split(separator: " - ")[0])
            .ToArray();

        Assert.Equal(actual: names, expected: [.. names.OrderBy(
            comparer: StringComparer.Ordinal,
            keySelector: static name => name
        )]);
        Assert.Contains(collection: names, expected: "help");
        Assert.Contains(collection: names, expected: "sum");
    }
    [Fact]
    public void OneDefinitionInstanceCannotBeRegisteredIntoTwoRegistries() {
        var module = new CachedDefinitionModule();

        _ = new CommandRegistry(modules: [module]);

        // A definition owns System.CommandLine state that registration mutates, so the second registry would rewrite
        // the first one's parser graph.
        _ = Assert.Throws<InvalidOperationException>(testCode: () => new CommandRegistry(modules: [module]));
    }
    [Fact]
    public void RunawayReEntrantSubmissionIsRefusedRatherThanOverflowingTheStack() {
        var registry = new CommandRegistry(modules: [new ReEntrantModule()]);

        var result = registry.Submit(line: "recurse");

        Assert.True(condition: result.IsError);
        Assert.Contains(actualString: result.Output, expectedSubstring: "nested more than");
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
    private sealed class FixedBindings(string command, string? text = null) : IInputBindings {
        private readonly CommandBinding[] m_bindings = [new CommandBinding(Command: command, Text: text)];

        public IReadOnlyList<CommandBinding>? Resolve(int slot, string source) => m_bindings;
    }
    private sealed class SeatPrincipal : ICommandPrincipalResolver {
        public CommandPrincipal PrincipalOf(int slot) => CommandPrincipal.Seat(slot: slot);
    }
    private sealed class SeparatorProbeModule(Action<CommandContext> onBound, Action onSecond) : ICommandModule {
        public IEnumerable<CommandDefinition> GetCommands() {
            yield return CommandDefinition.WithWireArgs(
                name: "bound",
                description: "Bound text separator probe.",
                handler: (context, _) => {
                    onBound(obj: context);

                    return CommandResult.None;
                },
                bindability: CommandBindability.Bindable
            );
            yield return CommandDefinition.Verb(
                name: "privileged",
                description: "Second command injection probe.",
                valueKind: CommandValueKind.Digital,
                handler: _ => {
                    onSecond();

                    return CommandResult.None;
                },
                bindability: CommandBindability.Bindable
            );
        }
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
            for (var index = 0; (index < count); index++) {
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
    private sealed class EchoModule : ICommandModule {
        public IEnumerable<CommandDefinition> GetCommands() {
            yield return CommandDefinition.WithWireArgs(
                name: "echo.first",
                description: "Echoes its first trailing token verbatim.",
                handler: static (_, args) => new CommandResult(Output: ((args.Count == 0)
                    ? string.Empty
                    : args[0].ToString())),
                bindability: CommandBindability.Unbindable
            );
            yield return CommandDefinition.WithWireArgs(
                name: "echo.tail",
                description: "Echoes every trailing token, space-joined.",
                handler: static (_, args) => new CommandResult(Output: args.Tail(start: 0)),
                bindability: CommandBindability.Unbindable
            );
        }
    }
    private sealed class ThrowingModule : ICommandModule {
        public IEnumerable<CommandDefinition> GetCommands() {
            yield return CommandDefinition.Verb(
                name: "boom.bound",
                description: "Throws from a bound dispatch.",
                valueKind: CommandValueKind.Digital,
                handler: static _ => throw new InvalidTimeZoneException(message: "handler fault"),
                bindability: CommandBindability.Bindable
            );
            yield return CommandDefinition.WithWireArgs(
                name: "boom.wire",
                description: "Throws from the wire-native path.",
                handler: static (_, _) => throw new InvalidTimeZoneException(message: "handler fault"),
                bindability: CommandBindability.Unbindable
            );
            yield return CommandDefinition.WithWireArgs(
                name: "boom.parsed",
                description: "Throws from the System.CommandLine fallback path.",
                handler: static (_, _) => throw new InvalidTimeZoneException(message: "handler fault"),
                bindability: CommandBindability.Unbindable
            );
        }
    }
    private sealed class RecordingSimulationModule(List<string> applied) : ICommandModule {
        public IEnumerable<CommandDefinition> GetCommands() {
            yield return CommandDefinition.WithWireArgs(
                name: "sim.record",
                description: "Records its argument when its tick applies.",
                handler: (_, args) => {
                    applied.Add(item: args[0].ToString());

                    return CommandResult.None;
                },
                bindability: CommandBindability.Unbindable,
                routing: CommandRouting.Simulation
            );
        }
    }
    private sealed class PhaseProbeModule(List<CommandPhase> phases) : ICommandModule {
        public IEnumerable<CommandDefinition> GetCommands() {
            yield return CommandDefinition.WithWireArgs(
                name: "phase.probe",
                description: "Records the edge its dispatch carried.",
                handler: (context, _) => {
                    phases.Add(item: context.Phase);

                    return CommandResult.None;
                },
                bindability: CommandBindability.Bindable,
                held: true,
                routing: CommandRouting.Simulation
            );
        }
    }
    private sealed class TextProbeModule(List<string?> seen) : ICommandModule {
        public IEnumerable<CommandDefinition> GetCommands() {
            yield return CommandDefinition.WithWireArgs(
                name: "text.probe",
                description: "Records the submitted line it was dispatched from.",
                handler: (context, _) => {
                    seen.Add(item: context.Text);

                    return CommandResult.None;
                },
                bindability: CommandBindability.Unbindable
            );
        }
    }
    private sealed class BareSimulationModule : ICommandModule {
        public IEnumerable<CommandDefinition> GetCommands() {
            yield return CommandDefinition.Verb(
                name: "sim.bare",
                description: "A deferred verb that accepts no arguments.",
                valueKind: CommandValueKind.Digital,
                handler: static _ => CommandResult.None,
                bindability: CommandBindability.Unbindable,
                routing: CommandRouting.Simulation
            );
        }
    }
    private sealed class CachedDefinitionModule : ICommandModule {
        private readonly CommandDefinition m_definition = CommandDefinition.Verb(
            name: "cached",
            description: "A definition instance the module hands out more than once.",
            valueKind: CommandValueKind.Digital,
            handler: static _ => CommandResult.None,
            bindability: CommandBindability.Bindable
        );

        public IEnumerable<CommandDefinition> GetCommands() {
            yield return m_definition;
        }
    }
    private sealed class ReEntrantModule : ICommandModule {
        public IEnumerable<CommandDefinition> GetCommands() {
            yield return CommandDefinition.WithWireArgs(
                name: "recurse",
                description: "Submits itself, forever, until the registry refuses.",
                handler: static (context, _) => (context.Registry?.Submit(line: "recurse") ?? CommandResult.None),
                bindability: CommandBindability.Unbindable
            );
        }
    }
}
