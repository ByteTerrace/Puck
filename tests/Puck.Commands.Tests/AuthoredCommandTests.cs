using Xunit;

namespace Puck.Commands.Tests;

/// <summary>Pins what running a document-authored command cannot show on its own: a name that would shadow an engine
/// verb and a line reaching a verb a binding may not name are refused, an authored command never acts with more
/// authority than its caller, the nesting bound holds across authored commands, and arguments substitute exactly as
/// declared.</summary>
public sealed class AuthoredCommandTests {
    private static AuthoredCommandParameter Choice(string name, string? fallback, params string[] values) => new(
        Check: (string value, out string canonical) => {
            canonical = (values.FirstOrDefault(predicate: candidate => string.Equals(a: candidate, b: value, comparisonType: StringComparison.OrdinalIgnoreCase)) ?? value);

            return values.Contains(value: value, comparer: StringComparer.OrdinalIgnoreCase)
                ? null
                : "is no choice";
        },
        Default: fallback,
        Kind: "choice",
        Name: name
    );
    private static AuthoredCommand Command(string name, IReadOnlyList<AuthoredCommandParameter> parameters, params string[] lines) {
        var errors = new List<string>();
        var command = AuthoredCommand.TryCreate(
            errors: errors,
            help: "Test command.",
            lines: lines,
            name: name,
            parameters: parameters
        );

        Assert.Empty(collection: errors);

        return command!;
    }
    private static AuthoredCommandParameter Text(string name, string? fallback = null) => new(
        Check: null,
        Default: fallback,
        Kind: "text",
        Name: name
    );
    private static (CommandRegistry Registry, Probe Probe) Registry(params AuthoredCommand[] commands) {
        var probe = new Probe();
        var registry = new CommandRegistry(modules: [probe], observers: [probe]);
        var set = new AuthoredCommandSet(commands: commands);

        registry.UseAuthoredCommands(source: _ => set);

        return (registry, probe);
    }

    [Fact]
    public void ArgumentsSubstituteAsDeclaredAndDefaultsFillTheRest() {
        var (registry, probe) = Registry(Command(
            "gx.say",
            [Text(name: "who"), Choice("mood", "Calm", "Calm", "Loud")],
            "probe.say {who} {mood}",
            "probe.say {{literal}} {who}{who}",
            "probe.say \"quoted {who}\""
        ));

        var loud = registry.Submit(line: "GX.SAY bob loud");

        Assert.False(condition: loud.IsError, userMessage: loud.Output);
        Assert.Equal(
            actual: probe.Said,
            expected: ["bob Loud", "{literal} bobbob", "quoted bob"]
        );

        probe.Said.Clear();
        _ = registry.Submit(line: "gx.say ann");
        Assert.Equal(actual: probe.Said[0], expected: "ann Calm");

        Assert.Contains(actualString: registry.Submit(line: "gx.say").Output, expectedSubstring: "needs <who:text>");
        Assert.Contains(actualString: registry.Submit(line: "gx.say a b c").Output, expectedSubstring: "takes at most 2 arguments");
        Assert.Contains(actualString: registry.Submit(line: "gx.say a shout").Output, expectedSubstring: "mood 'shout' is no choice");
        Assert.Contains(actualString: registry.Submit(line: "gx.say \"a b\"").Output, expectedSubstring: "no quotes");
        Assert.False(condition: Command("gx.one", [Text(name: "who")], "probe.say {who}").TryExpand(arguments: ["a\"b"], lines: out _, reason: out var quoted));
        Assert.Contains(actualString: quoted, expectedSubstring: "not one plain token");
    }
    [Fact]
    public void AnAuthoredCommandAnswersEverywhereAVerbIsLookedUp() {
        var (registry, probe) = Registry(Command("gx.say", [Text(name: "who", fallback: "kim")], "probe.say {who}"));

        Assert.Equal(actual: registry.BuildHelpText(namesOnly: true, prefix: "gx."), expected: "gx.say");
        Assert.Contains(actualString: registry.Submit(line: "help gx.").Output, expectedSubstring: "Usage: gx.say [who:text=kim]");
        Assert.Contains(collection: registry.Definitions, filter: metadata => (metadata.Name == "gx.say"));
        Assert.True(condition: registry.TryGetMetadata(metadata: out var metadata, name: "GX.say"));
        Assert.Equal(actual: metadata, expected: CommandRegistry.AuthoredMetadata(name: "gx.say"));
        Assert.True(condition: registry.TryGetId(id: out var id, name: "gx.say"));
        Assert.True(condition: registry.TryGetId(id: out var again, name: "GX.SAY"));
        Assert.Equal(actual: again, expected: id);
        Assert.True(condition: (id >= registry.CommandCount));
        Assert.Equal(actual: registry.GetName(id: id), expected: "gx.say");
        Assert.Throws<ArgumentOutOfRangeException>(testCode: () => registry.GetName(id: ((ushort)(id + 1))));
        Assert.True(condition: registry.RoutesToSimulation(line: "gx.say"));
        Assert.True(condition: registry.NamesAuthoredCommand(name: "gx.say"));
        Assert.False(condition: registry.TryGetId(id: out _, name: "gx.nope"));
        Assert.False(condition: registry.TryGetMetadata(metadata: out _, name: "gx.nope"));

        Assert.Equal(actual: registry.Submit(line: "run gx.say lee").Output, expected: "said lee");
        Assert.True(condition: registry.Submit(line: "run").IsError);
        Assert.Contains(actualString: registry.Submit(line: "run gx.nope").Output, expectedSubstring: "no authored command is named 'gx.nope'");
        Assert.Equal(actual: probe.Said, expected: ["lee"]);
    }
    [Fact]
    public void ANameThatShadowsAnEngineVerbOrALineBeyondABindingsReachIsRefusedByName() {
        var (registry, _) = Registry();
        var errors = new List<string>();

        registry.CheckAuthored(
            errors: errors,
            set: new AuthoredCommandSet(commands: [
                Command("probe.say", [], "probe.say hello"),
                Command("gx.secret", [], "probe.secret"),
                Command("gx.hidden", [], "probe.hidden"),
                Command("gx.unknown", [], "probe.nothing"),
                Command("gx.a", [], "gx.b"),
                Command("gx.b", [], "gx.a"),
                Command("gx.fine", [], "run gx.b", "gx.b", "probe.say ok"),
            ])
        );

        Assert.Contains(collection: errors, filter: error => error.Contains(value: "'probe.say' takes the name of the engine verb 'probe.say'", comparisonType: StringComparison.Ordinal));
        Assert.Contains(collection: errors, filter: error => error.Contains(value: "'gx.secret' line 1 calls 'probe.secret', which answers the operator alone", comparisonType: StringComparison.Ordinal));
        Assert.Contains(collection: errors, filter: error => error.Contains(value: "'gx.hidden' line 1 calls 'probe.hidden', which is not bindable", comparisonType: StringComparison.Ordinal));
        Assert.Contains(collection: errors, filter: error => error.Contains(value: "'gx.unknown' line 1 calls 'probe.nothing', which no registered verb", comparisonType: StringComparison.Ordinal));
        Assert.Contains(collection: errors, filter: error => error.Contains(value: "'gx.a' calls itself: gx.a -> gx.b -> gx.a", comparisonType: StringComparison.Ordinal));
        Assert.DoesNotContain(collection: errors, filter: error => error.Contains(value: "'gx.fine'", comparisonType: StringComparison.Ordinal));
    }
    [Fact]
    public void AnAuthoredCommandActsWithItsCallersAuthorityAndNoMore() {
        // The set is installed unchecked, so the dispatch-time half of the rule is the only thing standing between
        // the console and an operator verb or an unbindable one.
        var (registry, probe) = Registry(
            Command("gx.say", [], "probe.say hi"),
            Command("gx.secret", [], "probe.secret"),
            Command("gx.hidden", [], "probe.hidden"),
            Command("gx.fail", [], "probe.say first", "probe.fail", "probe.say never")
        );
        var source = new TextCommandSource(registry: registry);
        var results = new List<string>();
        var seat = source.CreateSession(
            authorize: metadata => (metadata.Name != "probe.say"),
            onResult: (_, result) => results.Add(item: result.Output),
            principal: Principal.Seat(slot: 2),
            slot: 2
        );

        Assert.Contains(actualString: registry.Submit(line: "gx.secret").Output, expectedSubstring: "answers the operator alone");
        Assert.Contains(actualString: registry.Submit(line: "gx.hidden").Output, expectedSubstring: "is not bindable");
        Assert.Equal(actual: probe.SecretRuns, expected: 0);

        var failed = registry.Submit(line: "gx.fail");

        Assert.True(condition: failed.IsError);
        Assert.Contains(actualString: failed.Output, expectedSubstring: "[gx.fail: line 2 'probe.fail' refused");
        Assert.Equal(actual: probe.Said, expected: ["first"]);
        Assert.Equal(actual: registry.Submit(line: "wire.errors").Output, expected: "[wire.errors: 3 rejected]");

        seat.Enqueue(line: "gx.say");
        source.Collect();

        Assert.Contains(actualString: Assert.Single(collection: results), expectedSubstring: "this session is not authorized");
        Assert.Equal(actual: probe.Said, expected: ["first"]);

        var open = source.CreateSession(principal: Principal.Seat(slot: 1), slot: 1);

        open.Enqueue(line: "gx.say");
        source.Collect();

        Assert.Equal(actual: probe.Principals[^1], expected: Principal.Seat(slot: 1));
        Assert.Equal(actual: probe.Slots[^1], expected: 1);
    }
    [Fact]
    public void AChainOfAuthoredCommandsStopsAtTheNestingBound() {
        var commands = new List<AuthoredCommand>();

        for (var depth = 0; (depth < 10); depth++) {
            commands.Add(item: Command($"gx.c{depth}", [], ((depth == 9) ? "probe.say bottom" : $"gx.c{(depth + 1)}")));
        }

        var (registry, probe) = Registry([.. commands]);
        var result = registry.Submit(line: "gx.c0");

        Assert.True(condition: result.IsError);
        Assert.Contains(actualString: result.Output, expectedSubstring: "nested more than 8 deep");
        Assert.Empty(collection: probe.Said);
        Assert.Equal(actual: registry.Submit(line: "wire.errors").Output, expected: "[wire.errors: 1 rejected]");
        Assert.False(condition: registry.Submit(line: "gx.c4").IsError);
        Assert.Equal(actual: probe.Said, expected: ["bottom"]);
    }
    [Fact]
    public void ATypedOrBoundAuthoredCommandRunsOnTheTickUnderItsCallersPrincipal() {
        var (registry, probe) = Registry(
            Command("gx.sim", [Text(name: "word", fallback: "tick")], "probe.sim {word}", "probe.say {word}"),
            Command("gx.other", [], "probe.say other")
        );
        var router = new InputRouter(
            bindings: new Bindings(rows: new Dictionary<string, CommandBinding> {
                ["key.a"] = new(Command: "gx.sim"),
                ["key.b"] = new(Command: "gx.other"),
                ["key.c"] = new(Command: "gx.sim", Text: "bound"),
                ["key.d"] = new(Command: "run"),
            }),
            principalResolver: new SeatPrincipals(),
            registry: registry
        );

        registry.RouteSimulationTo(sink: router.ConsoleTextSink);

        Assert.Equal(actual: registry.Submit(line: "gx.sim typed"), expected: CommandResult.None);
        Assert.Empty(collection: probe.Said);

        // Two keys bound to two authored commands are two buttons: held together, both fire. A key bound to `run`
        // with no line runs nothing.
        var tick = 0UL;

        foreach (var keys in new[] { new[] { "key.a", "key.b" }, ["key.c", "key.d"] }) {
            foreach (var key in keys) {
                router.Capture(signal: InputSignal.Press(source: key));
            }

            var pressed = router.SnapshotForTick(tick: ++tick, windowEndTick: ulong.MaxValue);

            registry.ApplySnapshot(snapshot: in pressed);

            foreach (var key in keys) {
                router.Capture(signal: InputSignal.Release(source: key));
            }

            var released = router.SnapshotForTick(tick: ++tick, windowEndTick: ulong.MaxValue);

            registry.ApplySnapshot(snapshot: in released);
        }

        // Lane order between the console's injection and the seat's presses is the router's business; what each
        // line ran as is this test's.
        Assert.Equal(actual: probe.Said.Order(comparer: StringComparer.Ordinal), expected: ["bound", "other", "tick", "typed"]);
        Assert.All(collection: probe.Simulated, action: simulated => Assert.Equal(actual: simulated.Phase, expected: CommandPhase.Started));
        Assert.Equal(actual: probe.Principals.Count(predicate: principal => (principal == Principal.Console)), expected: 2);
        Assert.Equal(actual: probe.Principals.Count(predicate: principal => (principal == Principal.Seat(slot: 0))), expected: 5);
        Assert.Contains(collection: probe.Activations, filter: activation => ((activation.Name == "run") && (activation.Text == "gx.sim typed") && activation.Result.Output.Contains(value: "said typed", comparisonType: StringComparison.Ordinal)));
    }
    [Fact]
    public void AVerbAnotherBootShapeRegistersPassesValidationAndIsRefusedWhereItIsAbsent() {
        var (registry, probe) = Registry(Command("gx.view", [], "probe.say before", "probe.rendered", "probe.say after"));
        var rendered = new CommandMetadata(Name: "probe.rendered", Bindability: CommandBindability.Bindable, Routing: CommandRouting.Immediate, ValueKind: CommandValueKind.Digital);
        var hidden = rendered with { Bindability = CommandBindability.Unbindable, Name = "probe.rendered-hidden" };
        var set = new AuthoredCommandSet(commands: [
            Command("gx.view", [], "probe.rendered"),
            Command("gx.hiddenview", [], "probe.rendered-hidden"),
            Command("gx.typo", [], "probe.nowhere"),
        ]);
        var errors = new List<string>();

        registry.UseVerbCatalog(catalog: name => name switch {
            "probe.rendered" => rendered,
            "probe.rendered-hidden" => hidden,
            _ => null,
        });
        registry.CheckAuthored(errors: errors, set: set);

        Assert.Equal(actual: errors.Count, expected: 2);
        Assert.Contains(collection: errors, filter: error => error.Contains(value: "'probe.rendered-hidden', which is not bindable", comparisonType: StringComparison.Ordinal));
        Assert.Contains(collection: errors, filter: error => error.Contains(value: "'probe.nowhere', which no registered verb", comparisonType: StringComparison.Ordinal));

        var result = registry.Submit(line: "gx.view");

        Assert.True(condition: result.IsError);
        Assert.Contains(actualString: result.Output, expectedSubstring: "[probe.rendered: not available in this host");
        Assert.Equal(actual: probe.Said, expected: ["before"]);
        Assert.Contains(actualString: registry.Submit(line: "run gx.view").Output, expectedSubstring: "line 2 'probe.rendered' refused");

        registry.UseVerbCatalog(catalog: null);
        errors.Clear();
        registry.CheckAuthored(errors: errors, set: set);

        Assert.Equal(actual: errors.Count, expected: 3);
    }
    [Fact]
    public void ALineHearsTheAuthoredCommandsOfItsOwnSlot() {
        var (registry, probe) = Registry();
        var set = new AuthoredCommandSet(commands: [Command("gx.say", [], "probe.say one")]);
        var source = new TextCommandSource(registry: registry);
        var results = new List<string>();
        var others = new List<string>();

        registry.UseAuthoredCommands(source: slot => ((slot == 1) ? set : AuthoredCommandSet.Empty));

        var one = source.CreateSession(onResult: (_, result) => results.Add(item: result.Output), principal: Principal.Console, slot: 1);
        var two = source.CreateSession(onResult: (_, result) => others.Add(item: result.Output), principal: Principal.Console, slot: 2);

        one.Enqueue(line: "help --names gx.");
        one.Enqueue(line: "gx.say");
        two.Enqueue(line: "gx.say");
        source.Collect();

        Assert.Equal(actual: results, expected: ["gx.say", "said one"]);
        Assert.Contains(actualString: Assert.Single(collection: others), expectedSubstring: "unknown command 'gx.say'");
        Assert.True(condition: registry.RoutesToSimulation(line: "gx.say", slot: 1));
        Assert.False(condition: registry.RoutesToSimulation(line: "gx.say"));
        Assert.Equal(actual: probe.Slots, expected: [1]);
    }
    [Fact]
    public void AuthoredIdsStopAtTheSixteenBitCeiling() {
        var probe = Registry().Registry;
        var room = ((ushort.MaxValue + 1) - probe.CommandCount);
        var commands = Enumerable.Range(start: 0, count: (room + 1)).Select(selector: index => Command($"gx.c{index}", [], "probe.say")).ToArray();
        var set = new AuthoredCommandSet(commands: commands);

        probe.UseAuthoredCommands(source: _ => set);

        for (var index = 0; (index < room); index++) {
            Assert.True(condition: probe.TryGetId(id: out _, name: $"gx.c{index}"));
        }

        Assert.Throws<InvalidOperationException>(testCode: () => probe.TryGetId(id: out _, name: $"gx.c{room}"));
    }
    [Theory]
    [InlineData("go", "probe.say", "a name is a namespaced token")]
    [InlineData("gx.go", "{who} hi", "the verb is literal text")]
    [InlineData("gx.go", "probe.say {nobody}", "names {nobody}, which is no declared parameter")]
    [InlineData("gx.go", "probe.say {who", "opens a placeholder it never closes")]
    [InlineData("gx.go", "probe.say who}", "has a '}' that closes nothing")]
    [InlineData("gx.go", "   ", "is blank")]
    [InlineData("gx.go", "# note {who}", "is a comment")]
    [InlineData("gx.go", "probe.say {who}\nquit", "carries a control character")]
    [InlineData("gx.go", "probe.say plain", "parameter 'who' appears in no line")]
    public void AMalformedCommandIsRefusedByName(string name, string line, string expected) {
        var errors = new List<string>();

        Assert.Null(@object: AuthoredCommand.TryCreate(errors: errors, help: null, lines: [line], name: name, parameters: [Text(name: "who")]));
        Assert.Contains(collection: errors, filter: error => error.Contains(value: expected, comparisonType: StringComparison.Ordinal));
    }
    [Fact]
    public void AMalformedParameterListIsRefusedByName() {
        var errors = new List<string>();

        Assert.Null(@object: AuthoredCommand.TryCreate(
            errors: errors,
            help: null,
            lines: [],
            name: "gx.go",
            parameters: [Text(name: "a", fallback: "x"), Text(name: "b"), Text(name: "a"), Text(name: "9bad"), Choice("c", "zz", "yes")]
        ));
        Assert.Null(@object: AuthoredCommand.TryCreate(
            errors: errors,
            help: null,
            lines: [.. Enumerable.Repeat(element: "probe.say", count: (AuthoredCommand.MaxLines + 1))],
            name: "gx.many",
            parameters: [.. Enumerable.Range(start: 0, count: (AuthoredCommand.MaxParameters + 1)).Select(selector: index => Text(name: $"p{index}", fallback: "x"))]
        ));

        foreach (var expected in new[] {
            "required parameter 'b' follows an optional one",
            "parameter 'a' is declared more than once",
            "parameter '9bad' is not a name",
            "parameter 'c' defaults to 'zz', which is no choice",
            "runs no lines",
            $"runs {(AuthoredCommand.MaxLines + 1)} lines",
            $"declares {(AuthoredCommand.MaxParameters + 1)} parameters",
        }) {
            Assert.Contains(collection: errors, filter: error => error.Contains(value: expected, comparisonType: StringComparison.Ordinal));
        }

        Assert.Throws<ArgumentException>(testCode: () => new AuthoredCommandSet(commands: [Command("gx.x", [], "probe.say"), Command("GX.X", [], "probe.say")]));
        Assert.False(condition: AuthoredCommand.IsLegalName(name: null));
        Assert.False(condition: AuthoredCommand.IsLegalIdentifier(name: "a.b"));
    }

    private sealed class Bindings(Dictionary<string, CommandBinding> rows) : IInputBindings {
        public IReadOnlyList<CommandBinding>? Resolve(int slot, string source) => (rows.TryGetValue(key: source, value: out var row) ? [row] : null);
    }
    private sealed class SeatPrincipals : IPrincipalResolver {
        public Principal PrincipalOf(int slot) => Principal.Seat(slot: slot);
    }
    private sealed class Probe : ICommandModule, ICommandObserver {
        public List<CommandActivation> Activations { get; } = [];
        public List<Principal> Principals { get; } = [];
        public List<string> Said { get; } = [];
        public int SecretRuns { get; private set; }
        public List<(string Word, CommandPhase Phase)> Simulated { get; } = [];
        public List<int> Slots { get; } = [];

        public IEnumerable<CommandDefinition> GetCommands() {
            yield return CommandDefinition.WithWireArgs(
                bindability: CommandBindability.Bindable,
                description: "Records what it was told.",
                handler: (context, args) => {
                    var said = ((args.Count == 0) ? string.Empty : args.Tail(start: 0));

                    Said.Add(item: said);
                    Principals.Add(item: context.Principal);
                    Slots.Add(item: context.Slot);

                    return new CommandResult(Output: $"said {said}");
                },
                name: "probe.say"
            );
            yield return CommandDefinition.WithWireArgs(
                bindability: CommandBindability.Bindable,
                description: "Records a simulation-routed word.",
                handler: (context, args) => {
                    Simulated.Add(item: (args[0].ToString(), context.Phase));
                    Principals.Add(item: context.Principal);

                    return CommandResult.None;
                },
                name: "probe.sim",
                routing: CommandRouting.Simulation
            );
            yield return CommandDefinition.WithWireArgs(
                audience: CommandAudience.Operator,
                bindability: CommandBindability.Bindable,
                description: "An operator verb.",
                handler: (_, _) => {
                    SecretRuns++;

                    return CommandResult.None;
                },
                name: "probe.secret"
            );
            yield return CommandDefinition.WithWireArgs(
                bindability: CommandBindability.Unbindable,
                description: "An unbindable verb.",
                handler: (_, _) => CommandResult.None,
                name: "probe.hidden"
            );
            yield return CommandDefinition.WithWireArgs(
                bindability: CommandBindability.Bindable,
                description: "Always refuses.",
                handler: (_, _) => CommandResult.Error(output: "[probe.fail: no]"),
                name: "probe.fail"
            );
        }
        public void OnCommand(in CommandActivation activation) => Activations.Add(item: activation);
    }
}
