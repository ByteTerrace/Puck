using System.Diagnostics;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Puck.Abstractions.Pacing;
using Puck.Commands;
using Puck.Hosting;

namespace Puck.Launcher;

/// <summary>
/// The headless boot shape's outermost host loop — a composition root's <c>host.presentation: none</c> /
/// <c>--headless</c> run-time twin. No window, no GPU device, no swapchain, no audio device: it paces the SAME
/// <see cref="FixedStepPump"/> the windowed <see cref="LauncherWindowHostedService"/> drives, off a high-resolution
/// waitable-timer wait instead of a present cadence — wall clock paces, it never enters simulation state
/// (<see cref="TickClock"/> converts the sampled delta to engine ticks exactly, same as windowed). The console pump
/// (stdin → <see cref="CommandRegistry"/>) runs every iteration exactly like the windowed loop, so a headless session
/// is scriptable over stdin/stdout identically.
/// </summary>
public sealed class HeadlessTickHostedService : BackgroundService {
    private readonly IHostApplicationLifetime m_applicationLifetime;
    private readonly BufferedConsoleOutput m_bufferedOutput;
    private readonly IInputClock m_inputClock;
    private readonly InputRouter? m_inputRouter;
    private readonly ILogger<HeadlessTickHostedService> m_logger;
    private readonly LauncherOptions m_options;
    private readonly IPrecisionWaiter? m_precisionWaiter;
    private readonly CommandRegistry m_registry;
    private readonly IFixedStepSimulation? m_simulation;
    private readonly TerminalControl m_terminal;
    private readonly TextCommandSource m_textSource;

    public HeadlessTickHostedService(
        IHostApplicationLifetime applicationLifetime,
        BufferedConsoleOutput bufferedOutput,
        IInputClock inputClock,
        ILogger<HeadlessTickHostedService> logger,
        LauncherOptions options,
        IEnumerable<InputRouter> inputRouters,
        IEnumerable<IFixedStepSimulation> simulations,
        IEnumerable<IPrecisionWaiter> precisionWaiters,
        CommandRegistry registry,
        TextCommandSource textSource,
        TerminalControl terminal
    ) {
        ArgumentNullException.ThrowIfNull(applicationLifetime);
        ArgumentNullException.ThrowIfNull(bufferedOutput);
        ArgumentNullException.ThrowIfNull(inputClock);
        ArgumentNullException.ThrowIfNull(inputRouters);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(precisionWaiters);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(textSource);
        ArgumentNullException.ThrowIfNull(terminal);

        m_applicationLifetime = applicationLifetime;
        m_bufferedOutput = bufferedOutput;
        m_inputClock = inputClock;
        m_inputRouter = LauncherHostLoop.SingleOrDefault(
            items: inputRouters,
            name: nameof(InputRouter),
            hostDescription: "headless host"
        );
        m_logger = logger;
        m_options = options;
        m_precisionWaiter = precisionWaiters.FirstOrDefault();
        m_registry = registry;
        m_textSource = textSource;
        m_simulation = LauncherHostLoop.SingleOrDefault(
            items: simulations,
            name: nameof(IFixedStepSimulation),
            hostDescription: "headless host"
        );
        m_terminal = terminal;

        if ((m_simulation is null) != (m_inputRouter is null)) {
            throw new InvalidOperationException(message: "A fixed-step simulation and its InputRouter must be registered together. Use AddFixedStepSimulation<TSimulation>().");
        }

        // The console text door's OWN sink — bound to the Console principal when the router built it, so wiring it
        // here cannot choose what a submitted line acts as (mirrors LauncherWindowHostedService's own constructor
        // wiring; FixedStepPump below re-asserts the SAME mapping, which is idempotent).
        m_registry.RouteSimulationTo(sink: m_inputRouter?.ConsoleTextSink);
    }

    private void RunHeadlessLoop(CancellationToken stoppingToken) {
        try {
            if (m_logger.IsEnabled(logLevel: LogLevel.Information)) {
                m_logger.LogInformation(message: "Headless boot: no window, no GPU device, no swapchain, no audio device — the authoritative server, console, and tape only.");
            }

            var clock = TickClock.Start();
            // Mirrors LauncherWindowHostedService's own null-simulation tolerance: a composition root that registers
            // no fixed-step sim still runs the console pump alone.
            var pump = (((m_simulation is { } pumpSimulation) && (m_inputRouter is { } pumpInputRouter))
                ? new FixedStepPump(
                    simulation: pumpSimulation,
                    inputRouter: pumpInputRouter,
                    registry: m_registry,
                    captureOriginTicks: m_inputClock.NowTicks
                )
                : null
            );
            var frequency = Stopwatch.Frequency;
            var maxFrameTicks = (EngineTicks.PerSecond / 4UL);
            // The registered simulation declares its own rate; DefaultUpdateRate is the null-simulation fallback
            // (console pump alone) and the fallback while the registered simulation reports 0 (an authored
            // simulation.rateHz durable stop) — the loop's own pacing (this wait grid, and the pump's calling
            // cadence below) is presentation-adjacent host pacing, never sim state, and must never depend on a rate
            // that can legitimately be zero (EngineTicks.PerRate refuses zero outright). The registered simulation
            // still gates whether it actually steps internally (WorldSimulation.ShouldStepBoot); this value only
            // decides how often Advance is called, so a stopped world's console keeps answering.
            //
            // Resolved per iteration, not once before the loop: a live world.load can swap in a differently-rated
            // document mid-session, and this boot shape must adopt the new cadence — both the step width handed to
            // Advance and the wall-clock wait grid below — the next iteration rather than keep pacing at a stale
            // rate.
            static uint ResolveRatePerSecond(IFixedStepSimulation? simulation) {
                var simRatePerSecond = (simulation?.RatePerSecond ?? LauncherHostLoop.DefaultUpdateRate);

                return ((simRatePerSecond == 0U)
                    ? LauncherHostLoop.DefaultUpdateRate
                    : simRatePerSecond
                );
            }

            var spinThreshold = ((frequency / 1000L) * LauncherHostLoop.SpinThresholdMilliseconds);
            var nextDeadline = Stopwatch.GetTimestamp();
            var exitAfterTimestamp = ((m_options.ExitAfter is { } exitAfter)
                ? (nextDeadline + ((long)(exitAfter.TotalSeconds * frequency)))
                : (long?)null
            );

            while (!stoppingToken.IsCancellationRequested) {
                m_textSource.Collect();

                // Flush the command pump's buffered result echoes ONCE, right after the drain that produced them (see
                // BufferedConsoleOutput) — the windowed loop's own precedent.
                m_bufferedOutput.Flush();

                if (
                    (exitAfterTimestamp is { } deadline) &&
                    (Stopwatch.GetTimestamp() >= deadline)
                ) {
                    m_terminal.RequestExit();
                }

                if (m_terminal.TryConsumeExit()) {
                    break;
                }

                var deltaTicks = clock.Sample();
                // Re-resolved every iteration — see ResolveRatePerSecond's own remarks above.
                var ratePerSecond = ResolveRatePerSecond(simulation: m_simulation);
                var stepTicks = EngineTicks.PerRate(ratePerSecond: ratePerSecond);
                // The wall-clock pacing grid for ONE fixed step — presentation-adjacent only (paces the wait, never
                // enters sim state); the TickClock sample above is what actually measures elapsed time for the
                // accumulator.
                var period = (frequency / ((long)ratePerSecond));

                pump?.Advance(
                    deltaTicks: deltaTicks,
                    maxFrameTicks: maxFrameTicks,
                    stepTicks: stepTicks
                );

                // Simulation-routed console handlers run while snapshots are applied above. Flush their real results
                // in this iteration rather than leaving them buffered until the next tick.
                m_bufferedOutput.Flush();

                nextDeadline += period;

                var nowTimestamp = Stopwatch.GetTimestamp();

                // CATCH-UP: fell more than a whole slot behind (a scripted burst, a stalled thread) — re-origin the
                // grid at now instead of accumulating debt as a scheduling storm of steps. The FIXED-STEP ACCUMULATOR
                // (inside FixedStepPump) is what actually absorbs the jitter this introduces: whatever real time
                // elapses before the next Advance call converts to the right whole number of steps regardless of
                // when this wait actually woke up.
                if ((nowTimestamp - nextDeadline) > period) {
                    nextDeadline = nowTimestamp;
                } else {
                    LauncherHostLoop.WaitUntil(
                        deadlineTimestamp: nextDeadline,
                        frequency: frequency,
                        precisionWaiter: m_precisionWaiter,
                        spinThreshold: spinThreshold
                    );
                }
            }

            m_logger.LogInformation(message: "Headless run ending; shutting the host down.");
        } finally {
            // Flush any buffered echo tail before teardown so the final lines a scripted run emits (e.g. right before
            // an --exit-after shutdown, or the frame a quit/exit verb lands) are never lost.
            m_bufferedOutput.Flush();

            m_applicationLifetime.StopApplication();
        }
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken) {
        var pumpThread = new Thread(start: () => RunHeadlessLoop(stoppingToken: stoppingToken)) {
            IsBackground = true,
            Name = "Puck.Launcher Headless Tick Pump",
        };

        pumpThread.Start();

        return Task.CompletedTask;
    }

}
