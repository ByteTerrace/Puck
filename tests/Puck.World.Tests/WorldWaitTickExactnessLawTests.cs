using Puck.Commands;
using Puck.Hosting;
using Puck.Launcher;
using Puck.World.Server;

using Xunit;

namespace Puck.World.Tests;

/// <summary>The real <c>world.wait</c> verb driven through the real <see cref="FixedStepPump"/>, the way every host loop
/// drives it: the console drains before every step, so a line held behind a wait runs right after the tick that
/// releases it, even when every step arrives in one catch-up burst. Each law carries its control — the same script
/// without the per-step drain, or through a session whose simulation lines are stamped with wall time, lands late.</summary>
public sealed class WorldWaitTickExactnessLawTests {
    private const ulong StepTicks = (EngineTicks.PerSecond / 30UL);

    // Resolves every invocation to the fixture's one row, as the desktop's boot console authority does.
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
    // The wall clock far ahead of the simulation, as it is throughout a catch-up burst.
    private sealed class AheadClock : IInputClock {
        public ulong NowTicks => (StepTicks * 1_000UL);
    }
    // Publishes each completed tick to the wait gate at the end of its step, as WorldHostStep does.
    private sealed class TickPublishingSimulation(WorldConsoleWaitGate gate) : IFixedStepSimulation {
        public bool AwaitsFrame => false;

        public bool HoldsClock(ulong withheldTicks) => false;
        public void SettleOwedFrames() { }

        public ulong Completed { get; private set; }
        public uint RatePerSecond => 30U;

        public void Step(in FixedStepContext context, in CommandSnapshot commands) {
            Completed++;
            gate.PublishTick(tick: Completed);
        }
    }
    // probe.read answers at once with the ticks completed so far; probe.write applies with a tick's input, so it
    // records the ticks completed before the step that consumes it.
    private sealed class ProbeModule(Func<ulong> completed, List<string> log) : ICommandModule {
        public IEnumerable<CommandDefinition> GetCommands() {
            yield return CommandDefinition.WithWireArgs(
                bindability: CommandBindability.Unbindable,
                description: "Records the completed tick count.",
                handler: (_, _) => {
                    log.Add(item: $"read@{completed()}");

                    return CommandResult.None;
                },
                name: "probe.read"
            );
            yield return CommandDefinition.WithWireArgs(
                bindability: CommandBindability.Unbindable,
                description: "Records the completed tick count when its tick applies.",
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

        public Host(bool drainBeforeEveryStep) {
            m_row = HostRow.Build(
                definition: Fixtures.BuildDocument(),
                name: "boot"
            );

            var gate = new WorldConsoleWaitGate();

            Simulation = new TickPublishingSimulation(gate: gate);

            var registry = new CommandRegistry(modules: [
                new WorldWaitCommandModule(
                    authority: new FixedAuthority(instance: m_row.Instance),
                    gates: new FixedGate(gate: gate)
                ),
                new ProbeModule(
                    completed: () => Simulation.Completed,
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
                beforeStep: (drainBeforeEveryStep
                    ? Source.Collect
                    : null
                ),
                captureOriginTicks: 0UL,
                inputRouter: Router,
                registry: registry,
                simulation: Simulation
            );
        }

        public List<string> Log { get; } = [];
        public FixedStepPump Pump { get; }
        public InputRouter Router { get; }
        public TickPublishingSimulation Simulation { get; }
        public TextCommandSource Source { get; }

        // One host-loop iteration: the loop's own drain, then one Advance that owes every step at once.
        public void Burst(ulong steps) {
            Source.Collect();
            _ = Pump.Advance(
                deltaTicks: (steps * StepTicks),
                maxFrameTicks: ulong.MaxValue,
                stepTicks: StepTicks
            );
        }
        public void Dispose() {
            Router.Dispose();
            m_row.Dispose();
        }
    }

    [Fact]
    public void AReadAfterAWaitLandsOnTheReleaseTickInsideOneBurst() {
        using var host = new Host(drainBeforeEveryStep: true);

        host.Source.Enqueue(line: "world.wait 20");
        host.Source.Enqueue(line: "probe.read");
        host.Source.Enqueue(line: "world.wait 10");
        host.Source.Enqueue(line: "probe.read");
        host.Burst(steps: 60UL);

        Assert.Equal(
            expected: ["read@20", "read@30"],
            actual: host.Log
        );
        Assert.Equal(
            expected: 60UL,
            actual: host.Simulation.Completed
        );
    }
    [Fact]
    public void ControlWithoutTheStepDrainTheReadLandsAtTheBurstsEnd() {
        using var host = new Host(drainBeforeEveryStep: false);

        host.Source.Enqueue(line: "world.wait 20");
        host.Source.Enqueue(line: "probe.read");
        host.Burst(steps: 60UL);
        host.Source.Collect();

        Assert.Equal(
            expected: ["read@60"],
            actual: host.Log
        );
    }
    [Fact]
    public void ASimulationLineAfterAWaitAppliesInTheNextStepInsideOneBurst() {
        using var host = new Host(drainBeforeEveryStep: true);

        host.Source.Enqueue(line: "world.wait 20");
        host.Source.Enqueue(line: "probe.write");
        host.Burst(steps: 60UL);

        Assert.Equal(
            expected: ["write@20"],
            actual: host.Log
        );
    }
    [Fact]
    public void ControlASessionStampedWithWallTimeMissesEveryStepOfTheBurst() {
        using var host = new Host(drainBeforeEveryStep: true);
        using var session = host.Source.CreateSession(
            principal: Principal.Console,
            simulationSink: host.Router.ConsoleTextSink
        );

        session.Enqueue(line: "world.wait 20");
        session.Enqueue(line: "probe.write");
        host.Burst(steps: 60UL);

        Assert.Empty(collection: host.Log);
    }
    [Fact]
    public void LinesQueuedBeforeTheFirstStepRunBeforeIt() {
        using var host = new Host(drainBeforeEveryStep: true);

        host.Source.Enqueue(line: "probe.read");
        host.Source.Enqueue(line: "probe.write");
        host.Burst(steps: 5UL);

        Assert.Equal(
            expected: ["read@0", "write@0"],
            actual: host.Log
        );
    }
}
