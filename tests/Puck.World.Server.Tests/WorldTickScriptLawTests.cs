using Puck.Commands;
using Puck.Hosting;
using Puck.Launcher;

using Xunit;

namespace Puck.World.Server.Tests;

/// <summary><c>world.at</c> and <c>world.wait until</c> driven through the real <see cref="FixedStepPump"/> over a real
/// server, every step owed in one catch-up burst: a pinned line runs right after its tick completes whatever else that
/// tick runs, and a wait for an absolute tick releases the line behind it there.</summary>
public sealed class WorldTickScriptLawTests {
    private sealed class FixedAuthority(WorldInstance instance) : IWorldConsoleAuthority {
        public bool TryResolve(CommandContext context, out WorldInstance resolved, out string refusal) {
            resolved = instance;
            refusal = string.Empty;

            return true;
        }
    }
    private sealed class FixedGate(WorldConsoleWaitGate gate) : IWorldWaitGateResolver {
        public WorldConsoleWaitGate GateFor(WorldInstance instance) => gate;
    }
    private sealed class NoBindings : IInputBindings {
        public IReadOnlyList<CommandBinding>? Resolve(int slot, string source) => null;
    }
    private sealed class ConsolePrincipal : IPrincipalResolver {
        public Principal PrincipalOf(int slot) => Principal.Console;
    }
    private sealed class AheadClock : IInputClock {
        public ulong NowTicks => (Fixtures.StepTicks * 1_000UL);
    }
    // Steps the real server and publishes its completed tick as WorldHostStep does.
    private sealed class ServerSimulation(WorldServer server, WorldConsoleWaitGate gate, Func<WorldTickScript> script) : IFixedStepSimulation {
        public bool AwaitsFrame => false;
        public uint RatePerSecond => 30U;

        public bool HoldsClock(ulong withheldTicks) => false;
        public void SettleOwedFrames() { }
        public void Step(in FixedStepContext context, in CommandSnapshot commands) {
            server.Step(context: in context);
            gate.PublishTick(tick: (server.NextInputTick - 1UL));
            script().PublishTick(tick: (server.NextInputTick - 1UL));
        }
    }
    private sealed class ProbeModule(Func<ulong> completed, List<string> log) : ICommandModule {
        public IEnumerable<CommandDefinition> GetCommands() {
            yield return CommandDefinition.WithWireArgs(
                bindability: CommandBindability.Unbindable,
                description: "Breaks without judging a test step.",
                handler: (_, _) => throw new InvalidOperationException(message: "broken handler"),
                name: "probe.fault",
                routing: CommandRouting.Simulation
            );
            yield return CommandDefinition.WithWireArgs(
                bindability: CommandBindability.Unbindable,
                description: "Answers under the submitting seat's own principal.",
                handler: (context, _) => ((context.Principal == Principal.Seat(slot: 0))
                    ? CommandResult.Error(output: "seat1 refused")
                    : new CommandResult(Output: context.Principal.Describe())),
                name: "probe.actor",
                routing: CommandRouting.Simulation
            );
            yield return CommandDefinition.WithWireArgs(
                bindability: CommandBindability.Unbindable,
                description: "Records the completed tick.",
                handler: (_, _) => {
                    log.Add(item: $"read@{completed()}");

                    return CommandResult.None;
                },
                name: "probe.read"
            );
            yield return CommandDefinition.WithWireArgs(
                bindability: CommandBindability.Unbindable,
                description: "Records the completed tick when its tick applies.",
                handler: (_, _) => {
                    log.Add(item: $"write@{completed()}");

                    return CommandResult.None;
                },
                name: "probe.write",
                routing: CommandRouting.Simulation
            );
        }
    }
    private sealed class Host : IDisposable {
        private readonly HostRow m_row;

        public Host() {
            m_row = HostRow.Build(
                definition: Fixtures.BuildDocument(),
                name: "boot"
            );

            var gate = new WorldConsoleWaitGate();
            var server = m_row.Server;

            ulong Completed() => (server.NextInputTick - 1UL);

            CommandRegistry? registry = null;

            Script = new WorldTickScript(
                completedTick: Completed,
                registry: () => registry!,
                source: () => Source!
            );
            registry = new CommandRegistry(modules: [
                new WorldWaitCommandModule(
                    authority: new FixedAuthority(instance: m_row.Instance),
                    gates: new FixedGate(gate: gate)
                ),
                new WorldTickScriptCommandModule(script: Script),
                new ProbeModule(
                    completed: Completed,
                    log: Log
                ),
            ]);
            Router = new InputRouter(
                bindings: new NoBindings(),
                clock: new AheadClock(),
                principalResolver: new ConsolePrincipal(),
                registry: registry
            );
            Source = new TextCommandSource(registry: registry);
            Pump = new FixedStepPump(
                beforeStep: Source.Collect,
                captureOriginTicks: 0UL,
                inputRouter: Router,
                registry: registry,
                simulation: new ServerSimulation(gate: gate, script: () => Script, server: server)
            );
            Answers = Source.CreateSession(
                dueNextTick: true,
                onResult: (_, result) => Output.Add(item: result.Output),
                principal: Principal.Console,
                simulationSink: Router.ConsoleTextSink
            );
        }

        public TextCommandSession Answers { get; }
        public List<string> Log { get; } = [];
        public List<string> Output { get; } = [];
        public FixedStepPump Pump { get; }
        public InputRouter Router { get; }
        public WorldTickScript Script { get; }
        public TextCommandSource Source { get; }

        public void Burst(ulong steps) {
            Source.Collect();
            _ = Pump.Advance(
                deltaTicks: (steps * Fixtures.StepTicks),
                maxFrameTicks: ulong.MaxValue,
                stepTicks: Fixtures.StepTicks
            );
        }
        public void Dispose() {
            Answers.Dispose();
            Router.Dispose();
            m_row.Dispose();
        }
    }

    [Fact]
    public void TestStepsAndOperatorLinesShareTicksButKeepTheirOwnAnswers() {
        using var host = new Host();

        Assert.Null(@object: host.Script.ArmSeat(tick: 20UL, text: "probe.actor", slot: 0, router: () => host.Router, line: out var refused));
        Assert.Null(@object: host.Script.ArmSeat(tick: 20UL, text: "probe.actor", slot: 1, router: () => host.Router, line: out var accepted));
        Assert.Null(@object: host.Script.ArmSeat(tick: 21UL, text: "probe.read", slot: 1, router: () => host.Router, line: out var immediate));
        Assert.Null(@object: host.Script.ArmSeat(tick: 22UL, text: "probe.fault", slot: 1, router: () => host.Router, line: out var faulted));
        Assert.NotNull(@object: host.Script.ArmSeat(tick: 23UL, text: "quit", slot: 1, router: () => host.Router, line: out _));
        host.Answers.Enqueue(line: "world.at 20 probe.actor");
        Assert.Equal(expected: "unreached", actual: refused.Outcome);
        host.Burst(steps: 40UL);
        Assert.Equal(expected: "refused", actual: refused.Outcome);
        Assert.Equal(expected: "seat1 refused", actual: refused.Answer);
        Assert.Equal(expected: "ran", actual: accepted.Outcome);
        Assert.Equal(expected: "seat2", actual: accepted.Answer);
        Assert.Equal(expected: "ran", actual: immediate.Outcome);
        Assert.Equal(expected: "faulted", actual: faulted.Outcome);
        Assert.Contains(expectedSubstring: "ran: probe.actor -> console", actualString: host.Script.Describe());
        // A rewind cannot submit any of these lines twice.
        host.Script.PublishTick(tick: 20UL);
        host.Source.Collect();
        Assert.Equal(expected: 0, actual: host.Script.PendingCount);
    }
    [Fact]
    public void PinnedLinesRunRightAfterTheirTicksInsideOneBurst() {
        using var host = new Host();

        // A write and a read pinned to one tick: neither waits behind the other.
        host.Answers.Enqueue(line: "world.at 20 probe.write");
        host.Answers.Enqueue(line: "world.at 20 probe.read");
        host.Answers.Enqueue(line: "world.at {\"start\": 30, \"steps\": [{\"at\": 0, \"run\": \"probe.read\"}, {\"at\": 5, \"run\": \"probe.write\"}]}");
        host.Burst(steps: 60UL);

        Assert.Equal(expected: ["read@20", "write@20", "read@30", "write@35"], actual: host.Log);
        Assert.Equal(expected: 0, actual: host.Script.PendingCount);
        Assert.Contains(expectedSubstring: "tick 35 ran: probe.write", actualString: host.Script.Describe());
    }
    [Fact]
    public void AWaitUntilATickReleasesTheNextLineRightAfterIt() {
        using var host = new Host();

        host.Answers.Enqueue(line: "world.wait until 25");
        host.Answers.Enqueue(line: "probe.read");
        host.Answers.Enqueue(line: "world.wait until 3");
        host.Answers.Enqueue(line: "probe.write");
        host.Burst(steps: 40UL);

        Assert.Equal(expected: ["read@25", "write@25"], actual: host.Log);
        Assert.Contains(collection: host.Output, filter: static line => line.Contains(comparisonType: StringComparison.Ordinal, value: "already at tick 25"));
    }
    [Fact]
    public void ARefusedScriptArmsNothing() {
        using var host = new Host();

        host.Burst(steps: 10UL);
        host.Answers.Enqueue(line: "world.at 5 probe.read");
        host.Answers.Enqueue(line: "world.at +2 world.wait 3");
        host.Answers.Enqueue(line: "world.at {\"start\": \"+1\", \"steps\": [{\"at\": 0, \"run\": \"probe.read\"}, {\"at\": 1, \"run\": \"quit\"}]}");
        host.Answers.Enqueue(line: "world.at {\"steps\": [{\"at\": \"soon\", \"run\": \"probe.read\"}]}");
        host.Answers.Enqueue(line: "world.at {not json");
        host.Answers.Enqueue(line: "world.at +4 probe.read");
        host.Answers.Enqueue(line: "world.at clear");
        host.Burst(steps: 20UL);

        Assert.Empty(collection: host.Log);
        Assert.Equal(expected: 5, actual: host.Output.Count(predicate: static line => (line.StartsWith(comparisonType: StringComparison.Ordinal, value: "[world.at: refused") || line.Contains(comparisonType: StringComparison.Ordinal, value: "step 0 is not") || line.Contains(comparisonType: StringComparison.Ordinal, value: "not JSON"))));
        Assert.Contains(collection: host.Output, filter: static line => line.Contains(comparisonType: StringComparison.Ordinal, value: "cleared 1 armed line"));
    }
}
