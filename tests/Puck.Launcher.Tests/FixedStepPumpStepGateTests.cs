using Puck.Commands;
using Puck.Hosting;
using Xunit;

namespace Puck.Launcher.Tests;

/// <summary>Covers <see cref="FixedStepPump"/>'s per-step seams: the drain that runs before every step, the
/// <c>mayStep</c> gate a host holds its first step on until its boot input is read (and stops on once it is exiting),
/// the stops that keep the time owed: a drain that changes the simulation's rate, and a step that owes the host a
/// frame, and the clock a holding pump withholds while that frame is unserved.</summary>
public sealed class FixedStepPumpStepGateTests {
    private const ulong StepTicks = (EngineTicks.PerSecond / 30UL);

    private sealed class CountingSimulation : IFixedStepSimulation {
        // Answers AwaitsFrame from the completed step count, the way a capture armed at one tick owes one frame.
        public Func<int, bool>? AwaitsFrameAfter { get; set; }
        public bool AwaitsFrame => (AwaitsFrameAfter?.Invoke(arg: Steps) ?? false);
        // Whether the owed frame has been served, which a holding pump waits on; a law serves it by setting this.
        public bool FrameServed { get; set; }
        public uint RatePerSecond { get; set; } = 30U;
        public int Steps { get; private set; }
        public ulong WithheldTicks { get; private set; }

        public bool HoldsClock(ulong withheldTicks) {
            if (
                !AwaitsFrame ||
                FrameServed
            ) {
                return false;
            }

            WithheldTicks += withheldTicks;

            return true;
        }
        public void SettleOwedFrames() { }
        public void Step(in FixedStepContext context, in CommandSnapshot commands) => Steps++;
    }
    private sealed class NoBindings : IInputBindings {
        public IReadOnlyList<CommandBinding>? Resolve(int slot, string source) => null;
    }
    private sealed class ConsolePrincipal : IPrincipalResolver {
        public Principal PrincipalOf(int slot) => Principal.Console;
    }

    private static (FixedStepPump Pump, CountingSimulation Simulation, InputRouter Router) NewPump(Action? beforeStep, Func<bool>? mayStep, bool holdsClock = false) {
        var registry = new CommandRegistry(modules: []);
        var router = new InputRouter(
            bindings: new NoBindings(),
            principalResolver: new ConsolePrincipal(),
            registry: registry
        );
        var simulation = new CountingSimulation();

        return (new FixedStepPump(
            beforeStep: beforeStep,
            captureOriginTicks: 0UL,
            holdsClock: holdsClock,
            inputRouter: router,
            mayStep: mayStep,
            registry: registry,
            simulation: simulation
        ), simulation, router);
    }

    private sealed class NowClock : IInputClock {
        public ulong NowTicks => 0UL;
    }
    // probe.late plays the standard-input reader landing between a drain and the gate: from another thread it queues
    // probe.write and releases the backlog, after the drain has already taken the lines present at its entry.
    // probe.read records the completed step count at once; probe.write records how many steps completed before the
    // step that applied it.
    private sealed class LateReaderModule(Func<TextCommandSource> source, StandardInputBacklog backlog, Func<int> completed, List<string> log) : ICommandModule {
        public IEnumerable<CommandDefinition> GetCommands() {
            yield return CommandDefinition.WithWireArgs(
                bindability: CommandBindability.Unbindable,
                description: "Queues probe.write and releases the backlog from another thread.",
                handler: (_, _) => {
                    var reader = new Thread(start: () => {
                        source().Enqueue(line: "probe.write");
                        backlog.Release();
                    });

                    reader.Start();
                    reader.Join();

                    return CommandResult.None;
                },
                name: "probe.late"
            );
            yield return CommandDefinition.WithWireArgs(
                bindability: CommandBindability.Unbindable,
                description: "Records the completed step count at once.",
                handler: (_, _) => {
                    log.Add(item: $"read@{completed()}");

                    return CommandResult.None;
                },
                name: "probe.read"
            );
            yield return CommandDefinition.WithWireArgs(
                bindability: CommandBindability.Unbindable,
                description: "Records the completed step count when its step applies.",
                handler: (_, _) => {
                    log.Add(item: $"write@{completed()}");

                    return CommandResult.None;
                },
                name: "probe.write",
                routing: CommandRouting.Simulation
            );
        }
    }
    private sealed class HostedPump : IDisposable {
        private readonly BufferedConsoleOutput m_output = new();

        public HostedPump() {
            TextCommandSource? source = null;
            var registry = new CommandRegistry(modules: [
                new LateReaderModule(
                    backlog: Backlog,
                    completed: () => Simulation.Steps,
                    log: Log,
                    source: () => source!
                ),
            ]);

            source = new TextCommandSource(registry: registry);
            Source = source;
            Router = new InputRouter(
                bindings: new NoBindings(),
                principalResolver: new ConsolePrincipal(),
                registry: registry
            );
            Pump = FixedStepPump.CreateHosted(
                holdsClock: false,
                inputBacklog: Backlog,
                inputClock: new NowClock(),
                inputRouter: Router,
                output: m_output,
                registry: registry,
                simulation: Simulation,
                terminal: new TerminalControl(),
                textSource: source
            )!;
            Backlog.Claim();
        }

        public StandardInputBacklog Backlog { get; } = new();
        public List<string> Log { get; } = [];

        public FixedStepPump Pump { get; }
        public InputRouter Router { get; }

        public CountingSimulation Simulation { get; } = new();

        public TextCommandSource Source { get; }

        // One host frame owing exactly one step.
        public void Frame() => _ = Pump.Advance(
            deltaTicks: StepTicks,
            maxFrameTicks: ulong.MaxValue,
            stepTicks: StepTicks
        );
        public void Dispose() {
            Router.Dispose();
            m_output.Dispose();
        }
    }

    [Fact]
    public void APausedWriterHoldsTheFirstStepUntilTheSessionWaitsForOne() {
        using var host = new HostedPump();

        // The first chunk, then a pause with the pipe open: a further Simulation line could still join the first step.
        host.Source.Enqueue(line: "probe.write");
        host.Frame();
        host.Frame();

        Assert.Equal(
            expected: 0,
            actual: host.Simulation.Steps
        );

        // The second chunk: another Simulation line, then a read the barrier holds until a step applies them both.
        host.Source.Enqueue(line: "probe.write");
        host.Source.Enqueue(line: "probe.read");
        host.Frame();
        host.Frame();

        Assert.Equal(
            expected: ["write@0", "write@0", "read@1"],
            actual: host.Log
        );
    }
    [Fact]
    public void OnceStepsHaveBegunAnIdleSessionDoesNotStopThem() {
        using var host = new HostedPump();

        host.Source.Enqueue(line: "probe.write");
        host.Source.Enqueue(line: "probe.read");
        host.Frame();
        host.Frame();
        host.Frame();

        Assert.Equal(
            expected: 3,
            actual: host.Simulation.Steps
        );
    }
    [Fact]
    public void ALineTheReaderQueuesAfterTheDrainStillPrecedesTheFirstStep() {
        using var host = new HostedPump();

        host.Source.Enqueue(line: "probe.late");
        host.Frame();
        host.Frame();
        host.Frame();

        Assert.Equal(
            expected: ["write@0"],
            actual: host.Log
        );
    }
    [Fact]
    public void TheDrainRunsBeforeEveryStepOfABurst() {
        var drainsAtStep = new List<int>();
        CountingSimulation? observed = null;

        var (pump, simulation, router) = NewPump(
            beforeStep: () => drainsAtStep.Add(item: observed!.Steps),
            mayStep: null
        );

        using (router) {
            observed = simulation;

            Assert.Equal(
                expected: 4,
                actual: pump.Advance(
                    deltaTicks: (4UL * StepTicks),
                    maxFrameTicks: ulong.MaxValue,
                    stepTicks: StepTicks
                )
            );
            Assert.Equal(
                actual: drainsAtStep,
                expected: [0, 1, 2, 3]
            );
        }
    }
    [Fact]
    public void AClosedGateTakesNoStepAndOwesNothingOnceItOpens() {
        var open = false;

        var (pump, simulation, router) = NewPump(
            beforeStep: null,
            mayStep: () => open
        );

        using (router) {
            Assert.Equal(
                expected: 0,
                actual: pump.Advance(
                    deltaTicks: ((10UL * StepTicks) + 7UL),
                    maxFrameTicks: ulong.MaxValue,
                    stepTicks: StepTicks
                )
            );
            // Only the sub-step remainder is kept, and the capture pin moves by the discarded whole steps.
            Assert.Equal(
                expected: 7UL,
                actual: pump.AccumulatorTicks
            );
            Assert.Equal(
                expected: (10UL * StepTicks),
                actual: pump.CaptureOriginTicks
            );

            open = true;

            Assert.Equal(
                expected: 1,
                actual: pump.Advance(
                    deltaTicks: StepTicks,
                    maxFrameTicks: ulong.MaxValue,
                    stepTicks: StepTicks
                )
            );
            Assert.Equal(
                expected: 1,
                actual: simulation.Steps
            );
        }
    }
    [Fact]
    public void AGateThatClosesMidBurstStopsTheBurstThere() {
        CountingSimulation? observed = null;

        var (pump, simulation, router) = NewPump(
            beforeStep: null,
            mayStep: () => (observed!.Steps < 3)
        );

        using (router) {
            observed = simulation;

            Assert.Equal(
                expected: 3,
                actual: pump.Advance(
                    deltaTicks: (10UL * StepTicks),
                    maxFrameTicks: ulong.MaxValue,
                    stepTicks: StepTicks
                )
            );
            Assert.Equal(
                expected: 0UL,
                actual: pump.AccumulatorTicks
            );
        }
    }
    [Fact]
    public void ADrainThatChangesTheRateStopsTheBurstAndKeepsTheTimeOwed() {
        CountingSimulation? observed = null;

        var (pump, simulation, router) = NewPump(
            beforeStep: () => {
                if (observed!.Steps == 3) {
                    observed.RatePerSecond = 60U;
                }
            },
            mayStep: null
        );

        using (router) {
            observed = simulation;

            Assert.Equal(
                expected: 3,
                actual: pump.Advance(
                    deltaTicks: (10UL * StepTicks),
                    maxFrameTicks: ulong.MaxValue,
                    stepTicks: StepTicks
                )
            );
            Assert.Equal(
                expected: (7UL * StepTicks),
                actual: pump.AccumulatorTicks
            );
        }
    }
    [Fact]
    public void AStepThatAwaitsAFrameEndsTheBurstAndKeepsTheTimeOwed() {
        var (pump, simulation, router) = NewPump(
            beforeStep: null,
            mayStep: null
        );

        using (router) {
            simulation.AwaitsFrameAfter = static steps => ((steps == 10) || (steps == 30));

            var bursts = new List<int>();

            // One host iteration owes 60 steps; each call is followed by the frame the host would compose.
            bursts.Add(item: pump.Advance(
                deltaTicks: (60UL * StepTicks),
                maxFrameTicks: ulong.MaxValue,
                stepTicks: StepTicks
            ));

            while (pump.AccumulatorTicks >= StepTicks) {
                bursts.Add(item: pump.Advance(
                    deltaTicks: 0UL,
                    maxFrameTicks: ulong.MaxValue,
                    stepTicks: StepTicks
                ));
            }

            Assert.Equal(
                actual: bursts,
                expected: [10, 20, 30]
            );
            Assert.Equal(
                expected: 60,
                actual: simulation.Steps
            );
            Assert.Equal(
                expected: (60UL * StepTicks),
                actual: pump.ElapsedTicks
            );
        }
    }
    [Fact]
    public void ControlWithNoFrameOwedTheSameTimeIsOneBurst() {
        var (pump, simulation, router) = NewPump(
            beforeStep: null,
            mayStep: null
        );

        using (router) {
            Assert.Equal(
                expected: 60,
                actual: pump.Advance(
                    deltaTicks: (60UL * StepTicks),
                    maxFrameTicks: ulong.MaxValue,
                    stepTicks: StepTicks
                )
            );
            Assert.Equal(
                expected: 60,
                actual: simulation.Steps
            );
        }
    }
    [Fact]
    public void AHoldingPumpStepsNoTickPastAnOwedFrameAndSpendsTheTimeItWithholds() {
        var (pump, simulation, router) = NewPump(
            beforeStep: null,
            holdsClock: true,
            mayStep: null
        );

        using (router) {
            simulation.AwaitsFrameAfter = static steps => (steps == 10);

            Assert.Equal(
                expected: 10,
                actual: pump.Advance(
                    deltaTicks: (60UL * StepTicks),
                    maxFrameTicks: ulong.MaxValue,
                    stepTicks: StepTicks
                )
            );

            // Five host iterations whose frames serve nothing: every one withholds whatever whole steps are due.
            for (var iteration = 0; (iteration < 5); iteration++) {
                Assert.Equal(
                    expected: 0,
                    actual: pump.Advance(
                        deltaTicks: (3UL * StepTicks),
                        maxFrameTicks: ulong.MaxValue,
                        stepTicks: StepTicks
                    )
                );
            }

            Assert.Equal(
                expected: 10,
                actual: simulation.Steps
            );
            Assert.Equal(
                expected: (65UL * StepTicks),
                actual: simulation.WithheldTicks
            );
            Assert.Equal(
                expected: 0UL,
                actual: pump.AccumulatorTicks
            );

            // Served, the next iteration steps only the time it brings: nothing withheld comes back as a burst.
            simulation.FrameServed = true;

            Assert.Equal(
                expected: 3,
                actual: pump.Advance(
                    deltaTicks: (3UL * StepTicks),
                    maxFrameTicks: ulong.MaxValue,
                    stepTicks: StepTicks
                )
            );
            Assert.Equal(
                expected: 13,
                actual: simulation.Steps
            );
        }
    }
    [Fact]
    public void ControlAPumpThatDoesNotHoldStepsPastTheOwedFrameOnItsNextCall() {
        var (pump, simulation, router) = NewPump(
            beforeStep: null,
            mayStep: null
        );

        using (router) {
            simulation.AwaitsFrameAfter = static steps => (steps == 10);

            Assert.Equal(
                expected: 10,
                actual: pump.Advance(
                    deltaTicks: (60UL * StepTicks),
                    maxFrameTicks: ulong.MaxValue,
                    stepTicks: StepTicks
                )
            );
            Assert.Equal(
                expected: 53,
                actual: pump.Advance(
                    deltaTicks: (3UL * StepTicks),
                    maxFrameTicks: ulong.MaxValue,
                    stepTicks: StepTicks
                )
            );
            Assert.Equal(
                expected: 0UL,
                actual: simulation.WithheldTicks
            );
        }
    }
}
