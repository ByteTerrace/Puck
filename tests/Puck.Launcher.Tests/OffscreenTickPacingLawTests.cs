using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Puck.Abstractions.Pacing;
using Puck.Commands;
using Puck.Hosting;
using Xunit;

namespace Puck.Launcher.Tests;

/// <summary>
/// The offscreen host's time is its tick count, never the wall clock: each iteration runs at most one step
/// (<see cref="FixedStepPump.TryStep"/>) and composes the frame that step owes
/// (<see cref="OffscreenTickHostedService.ComposesFrame"/>), so every tick composes exactly one frame whatever a frame
/// cost, and a frame spans the simulation time its step advanced. The host laws run the real
/// <see cref="OffscreenTickHostedService"/> over a manual <see cref="TimeProvider"/> its frames advance by what they
/// cost, so a slow frame is injected through the host's own clock. Each carries its red leg: the same frame costs
/// replayed through the wall-clock rule a display-paced host keeps (<see cref="FixedStepPump.Advance"/> over the sampled
/// interval) burst several ticks into the iteration after a slow frame, which the law's measure catches. No GPU is
/// involved.
/// </summary>
public sealed class OffscreenTickPacingLawTests {
    private const uint Rate = 30U;
    private const ulong StepTicks = (EngineTicks.PerSecond / Rate);
    // A capture armed at this tick, two ticks after a slow frame.
    private const ulong CaptureTick = 10UL;
    private const int Frames = 12;

    // One produced frame: the simulation tick it composed, the frame context it was handed, whether it served the armed
    // capture, and the completion the root reported.
    private readonly record struct ProducedFrame(ulong Tick, ulong ElapsedTicks, ulong DeltaTicks, ulong FrameDeltaTicks, ulong AccumulatorTicks, bool Served, FrameCompletion Completion = FrameCompletion.Rendered);

    // A root that cannot render tick 1 for its first four compositions (a cold build) nor tick 5 for its first two (a
    // rebuild), and renders every other composition.
    private static FrameCompletion ColdThenRebuild(ulong tick, int attempt) => (tick, attempt) switch {
        (1UL, <= 4) => FrameCompletion.NotYetRenderable,
        (5UL, <= 2) => FrameCompletion.NotYetRenderable,
        _ => FrameCompletion.Rendered,
    };
    // What the frame composing a tick costs: a slow first render, a rebuild, a hitch two ticks before the capture, and
    // fast frames between them.
    private static TimeSpan CostOf(ulong tick) => tick switch {
        1UL => TimeSpan.FromSeconds(value: 3),
        4UL => TimeSpan.FromMilliseconds(value: 1500),
        8UL => TimeSpan.FromMilliseconds(value: 400),
        _ => TimeSpan.FromMilliseconds(value: 5),
    };

    // A host clock in microseconds that moves only when a frame costs time, a wait waits, or it is read (one microsecond
    // a read, so a wait's spin tail ends).
    private sealed class ManualClock : TimeProvider {
        private long m_now;

        public override long TimestampFrequency => 1_000_000L;

        public void Advance(TimeSpan by) => _ = Interlocked.Add(
            location1: ref m_now,
            value: ((long)(by.TotalSeconds * TimestampFrequency))
        );
        public override long GetTimestamp() => Interlocked.Increment(location: ref m_now);
    }
    // Waits by moving the manual clock.
    private sealed class ManualWaiter(ManualClock clock) : IPrecisionWaiter {
        public bool TryWait(TimeSpan duration) {
            clock.Advance(by: duration);

            return true;
        }
    }
    // Counts its steps, and owes a frame for a capture armed at captureTick until a frame serves it.
    private sealed class PacingSimulation(ulong? captureTick) : IFixedStepSimulation {
        public bool AwaitsFrame => ((captureTick == Completed) && !CaptureServed);
        public bool CaptureServed { get; set; }
        public ulong Completed { get; private set; }
        public uint RatePerSecond => Rate;

        public bool HoldsClock(ulong withheldTicks) => AwaitsFrame;
        public void SettleOwedFrames() { }
        public void Step(in FixedStepContext context, in CommandSnapshot commands) => Completed++;
    }
    // Records every frame, costs each the time the law gives it on the host clock, serves an armed capture on the
    // second frame composed for it (a render chain that needs one frame more), and ends the run after its frames.
    private sealed class PacingRoot(PacingSimulation simulation, int frames, Func<ulong, int, FrameCompletion>? completion = null) : IRenderRoot {
        private int m_attempts;
        private ulong m_attemptTick;
        private int m_framesWhileOwed;

        public ManualClock? Clock { get; init; }
        public List<ProducedFrame> Frames { get; } = [];
        public TerminalControl? Terminal { get; set; }

        public void Dispose() { }
        public RootFrame ProduceFrame(in FrameContext context) {
            var served = (
                simulation.AwaitsFrame &&
                (++m_framesWhileOwed == 2)
            );

            simulation.CaptureServed |= served;

            if (m_attemptTick != simulation.Completed) {
                m_attemptTick = simulation.Completed;
                m_attempts = 0;
            }

            var completed = (completion?.Invoke(
                arg1: simulation.Completed,
                arg2: ++m_attempts
            ) ?? FrameCompletion.Rendered);

            Frames.Add(item: new ProducedFrame(
                AccumulatorTicks: context.AccumulatorTicks,
                Completion: completed,
                DeltaTicks: context.DeltaTicks,
                ElapsedTicks: context.ElapsedTicks,
                FrameDeltaTicks: context.FrameDeltaTicks,
                Served: served,
                Tick: simulation.Completed
            ));
            Clock?.Advance(by: CostOf(tick: simulation.Completed));

            if (Frames.Count == frames) {
                Terminal?.RequestExit();
            }

            return new RootFrame(
                Completion: completed,
                Reason: ((completed == FrameCompletion.Rendered)
                    ? null
                    : "the law's root has not rendered this tick"),
                Surface: default
            );
        }
    }
    private sealed class NoBindings : IInputBindings {
        public IReadOnlyList<CommandBinding>? Resolve(int slot, string source) => null;
    }
    private sealed class ConsolePrincipal : IPrincipalResolver {
        public Principal PrincipalOf(int slot) => Principal.Console;
    }

    // Runs the real offscreen host over the manual clock until the root has produced its frames.
    private static async Task<(PacingSimulation Simulation, List<ProducedFrame> Frames)> RunHostAsync(ulong? captureTick, int frames, Func<ulong, int, FrameCompletion>? completion = null) {
        var clock = new ManualClock();
        var simulation = new PacingSimulation(captureTick: captureTick);
        var root = new PacingRoot(
            completion: completion,
            frames: frames,
            simulation: simulation
        ) {
            Clock = clock,
        };
        var builder = Host.CreateApplicationBuilder(settings: new HostApplicationBuilderSettings {
            DisableDefaults = true,
        });

        builder.Logging.ClearProviders();
        // A backstop on the manual clock, so a run that never steps ends rather than spinning.
        builder.Services.AddSingleton(implementationInstance: new LauncherOptions {
            ExitAfter = TimeSpan.FromMinutes(value: 10),
        });
        builder.Services.AddSingleton(implementationInstance: new OffscreenRenderOptions(
            height: 32U,
            width: 32U
        ));
        builder.Services.AddSingleton<IRenderRoot>(implementationInstance: root);
        builder.Services.AddSingleton<TimeProvider>(implementationInstance: clock);
        builder.Services.AddSingleton<IPrecisionWaiter>(implementationInstance: new ManualWaiter(clock: clock));
        builder.Services.AddSingleton<IPrincipalResolver, ConsolePrincipal>();
        builder.Services.AddSingleton(implementationInstance: simulation);
        builder.Services.AddFixedStepSimulation<PacingSimulation>(bindings: new NoBindings());
        builder.Services.AddLauncherOffscreenTerminal();
        // No standard-input reader: the run has no script, and a reader over the test process's own input would hold the
        // first step on whatever that input is. The reader is the terminal's one hosted service made by a factory.
        for (var index = (builder.Services.Count - 1); (index >= 0); index--) {
            if (
                (builder.Services[index].ServiceType == typeof(IHostedService)) &&
                (builder.Services[index].ImplementationFactory is not null)
            ) {
                builder.Services.RemoveAt(index: index);
            }
        }

        using var host = builder.Build();

        root.Terminal = host.Services.GetRequiredService<TerminalControl>();
        await WindowedHostFixture.RunAsync(host: host);

        Assert.Equal(
            actual: root.Frames.Count,
            expected: frames
        );

        return (simulation, root.Frames);
    }
    // The red leg: the same frame costs through the wall-clock rule the offscreen host no longer has, the sampled
    // interval (one period, or a slow frame's whole cost) handed to Advance, a frame composed after any step or for an
    // owed frame.
    private static List<ProducedFrame> ReplayWallPaced(ulong? captureTick, int frames) {
        var simulation = new PacingSimulation(captureTick: captureTick);
        var root = new PacingRoot(
            frames: frames,
            simulation: simulation
        );
        var registry = new CommandRegistry(modules: []);

        using var router = new InputRouter(
            bindings: new NoBindings(),
            principalResolver: new ConsolePrincipal(),
            registry: registry
        );
        var pump = new FixedStepPump(
            captureOriginTicks: 0UL,
            holdsClock: true,
            inputRouter: router,
            registry: registry,
            simulation: simulation
        );
        var interval = StepTicks;

        for (var iteration = 0; ((iteration < 10_000) && (root.Frames.Count < frames)); iteration++) {
            var steps = pump.Advance(
                deltaTicks: interval,
                maxFrameTicks: (EngineTicks.PerSecond / 4UL),
                stepTicks: StepTicks
            );

            interval = StepTicks;

            if (
                (steps == 0) &&
                !simulation.AwaitsFrame
            ) {
                continue;
            }

            var context = new FrameContext(
                AccumulatorTicks: pump.AccumulatorTicks,
                DeltaTicks: (((ulong)steps) * StepTicks),
                ElapsedTicks: pump.ElapsedTicks,
                FrameDeltaTicks: 0UL,
                Host: HostContext.Empty,
                StepTicks: StepTicks,
                TargetHeight: 32U,
                TargetWidth: 32U
            );

            _ = root.ProduceFrame(context: in context);
            interval = Math.Max(
                val1: StepTicks,
                val2: ((ulong)(CostOf(tick: simulation.Completed).TotalSeconds * EngineTicks.PerSecond))
            );
        }

        return root.Frames;
    }
    // Whether each frame composed the tick after the frame before it, advancing exactly one step, unless it composed
    // again a frame its tick still owed, advancing nothing.
    private static bool EveryFrameIsOneTick(IReadOnlyList<ProducedFrame> frames) {
        var previous = 0UL;

        foreach (var frame in frames) {
            var advanced = (frame.Tick - previous);

            if (!((
                (advanced == 1UL) &&
                (frame.DeltaTicks == StepTicks)
            ) || (
                (advanced == 0UL) &&
                (frame.DeltaTicks == 0UL)
            ))) {
                return false;
            }

            previous = frame.Tick;
        }

        return true;
    }

    [InlineData(true, true, false, true)]
    [InlineData(true, false, false, false)]
    [InlineData(true, false, true, true)]
    [InlineData(true, true, true, true)]
    [InlineData(false, false, false, true)]
    [Theory]
    public void AFrameIsComposedOnlyForAStepOrAFrameAStepOwes(bool hasSimulation, bool stepped, bool owesFrame, bool composes) =>
        Assert.Equal(
            actual: OffscreenTickHostedService.ComposesFrame(
                hasSimulation: hasSimulation,
                owesFrame: owesFrame,
                stepped: stepped
            ),
            expected: composes
        );
    [InlineData(true, FrameCompletion.Rendered, false)]
    [InlineData(true, FrameCompletion.NotYetRenderable, true)]
    [InlineData(true, FrameCompletion.Refused, false)]
    [InlineData(false, FrameCompletion.NotYetRenderable, false)]
    [Theory]
    public void OnlyAFrameNotYetRenderableHoldsItsTick(bool hasSimulation, FrameCompletion completion, bool holds) =>
        Assert.Equal(
            actual: OffscreenTickHostedService.HoldsTick(
                completion: completion,
                hasSimulation: hasSimulation
            ),
            expected: holds
        );
    /// <summary>One step per call, whatever the interval: a slow iteration's interval rebases the input pin by what the
    /// step does not take, and steps nothing more. The red leg hands the same interval to <see cref="FixedStepPump.Advance"/>,
    /// which takes every step it covers.</summary>
    [Fact]
    public void TryStepRunsOneStepWhateverTheInterval() {
        var simulation = new PacingSimulation(captureTick: null);
        var registry = new CommandRegistry(modules: []);

        using var router = new InputRouter(
            bindings: new NoBindings(),
            principalResolver: new ConsolePrincipal(),
            registry: registry
        );
        var pump = new FixedStepPump(
            captureOriginTicks: 0UL,
            inputRouter: router,
            registry: registry,
            simulation: simulation
        );
        var slow = (5UL * StepTicks);

        Assert.True(condition: pump.TryStep(
            intervalTicks: slow,
            stepTicks: StepTicks
        ));
        Assert.Equal(
            actual: (simulation.Completed, pump.ElapsedTicks, pump.AccumulatorTicks, pump.CaptureOriginTicks),
            expected: (1UL, StepTicks, 0UL, (slow - StepTicks))
        );
        Assert.Equal(
            actual: pump.Advance(
                deltaTicks: slow,
                maxFrameTicks: ulong.MaxValue,
                stepTicks: StepTicks
            ),
            expected: 5
        );
    }
    /// <summary>Over frames that cost three seconds, a second and a half and four tenths of a second among fast ones,
    /// every produced frame composes the tick after the one before, spans one step of simulation time, and presents
    /// that tick exactly; the run stepped once per frame. The red leg replays the same costs through the wall-clock rule
    /// and finds a frame that advanced several ticks.</summary>
    [Fact]
    public async Task ASlowFrameAdvancesExactlyOneTickPerProducedFrame() {
        var (simulation, frames) = await RunHostAsync(
            captureTick: null,
            frames: Frames
        );

        Assert.True(condition: EveryFrameIsOneTick(frames: frames));
        Assert.All(
            action: static frame => {
                Assert.Equal(
                    actual: (frame.DeltaTicks, frame.FrameDeltaTicks, frame.AccumulatorTicks, frame.ElapsedTicks),
                    expected: (StepTicks, StepTicks, 0UL, (frame.Tick * StepTicks))
                );
            },
            collection: frames
        );
        Assert.Equal(
            actual: simulation.Completed,
            expected: ((ulong)frames.Count)
        );

        var replayed = ReplayWallPaced(
            captureTick: null,
            frames: Frames
        );

        Assert.False(condition: EveryFrameIsOneTick(frames: replayed));
        Assert.Contains(
            collection: replayed,
            filter: static frame => (frame.DeltaTicks > StepTicks)
        );
    }
    /// <summary>A capture armed two ticks after a slow frame, served one frame late: the frame that serves
    /// it composed exactly the armed tick, the frame before it composed that tick too and advanced nothing (the held,
    /// owed frame), and the frame before those composed the tick before, so the capture's frame reprojects across one
    /// tick. The run steps on afterwards. The red leg replays the same costs through the wall-clock rule, whose capture
    /// frame follows a frame several ticks earlier.</summary>
    [Fact]
    public async Task ACaptureRecordsTheTickItsFrameComposed() {
        var (simulation, frames) = await RunHostAsync(
            captureTick: CaptureTick,
            frames: Frames
        );
        var served = frames.FindIndex(match: static frame => frame.Served);

        Assert.True(condition: simulation.CaptureServed);
        Assert.True(condition: EveryFrameIsOneTick(frames: frames));
        Assert.Equal(
            actual: frames[served],
            expected: new ProducedFrame(
                AccumulatorTicks: 0UL,
                DeltaTicks: 0UL,
                ElapsedTicks: (CaptureTick * StepTicks),
                FrameDeltaTicks: 0UL,
                Served: true,
                Tick: CaptureTick
            )
        );
        Assert.Equal(
            actual: (frames[(served - 1)].Tick, frames[(served - 1)].DeltaTicks, frames[(served - 2)].Tick),
            expected: (CaptureTick, StepTicks, (CaptureTick - 1UL))
        );
        Assert.Equal(
            actual: frames[(served + 1)].Tick,
            expected: (CaptureTick + 1UL)
        );

        var replayed = ReplayWallPaced(
            captureTick: CaptureTick,
            frames: Frames
        );
        var replayedServed = replayed.FindIndex(match: static frame => frame.Served);

        Assert.Equal(
            actual: replayed[replayedServed].Tick,
            expected: CaptureTick
        );
        Assert.True(condition: (replayed[(replayedServed - 2)].Tick < (CaptureTick - 1UL)));
    }
    /// <summary>A root that cannot render a tick yet holds it: over a cold build of tick 1 and a rebuild at tick 5, the
    /// host composes the held tick again, stepping none and advancing nothing, until the root renders it, so the rendered
    /// frames show ticks 1, 2, 3 and on with none skipped, each tick's first composition spanning its step. The red leg
    /// drives the same root through one step an iteration whatever it reports, and ticks 1 and 5 never get a rendered
    /// frame.</summary>
    [Fact]
    public async Task ATickHeldByAColdBuildRendersItsOwnFrameBeforeTheNextTickSteps() {
        var (simulation, frames) = await RunHostAsync(
            captureTick: null,
            completion: ColdThenRebuild,
            frames: Frames
        );
        var rendered = frames.Where(predicate: static frame => (frame.Completion == FrameCompletion.Rendered)).ToList();

        Assert.True(condition: EveryFrameIsOneTick(frames: frames));
        Assert.Equal(
            actual: rendered.Select(selector: static frame => frame.Tick),
            expected: Enumerable.Range(
                count: rendered.Count,
                start: 1
            ).Select(selector: static tick => ((ulong)tick))
        );
        Assert.Equal(
            actual: frames.Select(selector: static frame => (frame.Tick, frame.DeltaTicks, frame.Completion)).Take(count: 9),
            expected: [
                (1UL, StepTicks, FrameCompletion.NotYetRenderable),
                (1UL, 0UL, FrameCompletion.NotYetRenderable),
                (1UL, 0UL, FrameCompletion.NotYetRenderable),
                (1UL, 0UL, FrameCompletion.NotYetRenderable),
                (1UL, 0UL, FrameCompletion.Rendered),
                (2UL, StepTicks, FrameCompletion.Rendered),
                (3UL, StepTicks, FrameCompletion.Rendered),
                (4UL, StepTicks, FrameCompletion.Rendered),
                (5UL, StepTicks, FrameCompletion.NotYetRenderable),
            ]
        );
        Assert.All(
            action: static frame => Assert.Equal(
                actual: frame.ElapsedTicks,
                expected: (frame.Tick * StepTicks)
            ),
            collection: frames
        );
        Assert.Equal(
            actual: simulation.Completed,
            expected: frames[^1].Tick
        );

        var unheld = new PacingSimulation(captureTick: null);
        var root = new PacingRoot(
            completion: ColdThenRebuild,
            frames: Frames,
            simulation: unheld
        );
        var registry = new CommandRegistry(modules: []);

        using var router = new InputRouter(
            bindings: new NoBindings(),
            principalResolver: new ConsolePrincipal(),
            registry: registry
        );
        var pump = new FixedStepPump(
            captureOriginTicks: 0UL,
            inputRouter: router,
            registry: registry,
            simulation: unheld
        );

        while (root.Frames.Count < Frames) {
            _ = pump.TryStep(
                intervalTicks: StepTicks,
                stepTicks: StepTicks
            );

            var context = new FrameContext(
                AccumulatorTicks: 0UL,
                DeltaTicks: StepTicks,
                ElapsedTicks: pump.ElapsedTicks,
                FrameDeltaTicks: StepTicks,
                Host: HostContext.Empty,
                StepTicks: StepTicks,
                TargetHeight: 32U,
                TargetWidth: 32U
            );

            _ = root.ProduceFrame(context: in context);
        }

        // Ticks 1 and 5 stepped on with no rendered frame.
        Assert.Equal(
            actual: root.Frames.Where(predicate: static frame => (frame.Completion != FrameCompletion.Rendered)).Select(selector: static frame => frame.Tick),
            expected: [1UL, 5UL]
        );
        Assert.DoesNotContain(
            collection: root.Frames.Where(predicate: static frame => (frame.Completion == FrameCompletion.Rendered)).Select(selector: static frame => frame.Tick),
            filter: static tick => ((tick == 1UL) || (tick == 5UL))
        );
    }
    /// <summary>A refused frame releases its tick: the root names its refusal, and the host steps on one tick per
    /// iteration as for a rendered frame.</summary>
    [Fact]
    public async Task ARefusedFrameReleasesItsTick() {
        var (_, frames) = await RunHostAsync(
            captureTick: null,
            completion: static (tick, _) => ((tick >= 3UL)
                ? FrameCompletion.Refused
                : FrameCompletion.Rendered),
            frames: Frames
        );

        Assert.Equal(
            actual: frames.Select(selector: static frame => (frame.Tick, frame.DeltaTicks)),
            expected: Enumerable.Range(
                count: Frames,
                start: 1
            ).Select(selector: static tick => (((ulong)tick), StepTicks))
        );
        Assert.All(
            action: static frame => Assert.Equal(
                actual: frame.Completion,
                expected: FrameCompletion.Refused
            ),
            collection: frames.Skip(count: 2)
        );
    }
}
